using ClassShout.Core.Remote;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia.Threading;
using ClassShout.Core.Audio;
using ClassShout.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>
/// 语音喊话页。
///
/// 采集到的 PCM 先进入一个有界队列，再由后台任务按序写到 TCP 上。
/// 为什么要多这一层队列：麦克风回调在采集线程上触发，不能在里面 await 网络发送；
/// 而直接 fire-and-forget 又会在网络变慢时把任务堆到内存里。
/// 有界队列 + DropOldest 让延迟可控 —— 宁可丢掉最旧的几十毫秒，也不要越积越延迟。
/// </summary>
public partial class VoiceShoutViewModel : ObservableObject, IDisposable
{
    /// <summary>队列容量：每片约 20 毫秒，200 片约 4 秒缓冲。</summary>
    private const int QueueCapacity = 200;

    /// <summary>单次喊话时长上限，防止忘记松手一直录下去。</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    private readonly IShoutTransport _channel;

    private IAudioRecorder? _recorder;

    /// <summary>
    /// 已经开始过语音会话（已经给教室端发过 audioStart）。
    ///
    /// 用一个显式标志而不是"看 closeChannel 参数"，是因为要不要补发 audioEnd
    /// 取决于"教室端是不是已经进入播放状态了"，而不是"用户点的是发送还是取消"。
    /// </summary>
    private bool _audioSessionOpen;

    private Channel<byte[]>? _audioQueue;
    private Task? _pumpTask;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private double _elapsedSeconds;

    /// <summary>这次录音额外要发的几间教室（当前那条链路发的那间不在里面）。</summary>
    private List<RelayVoiceSession> _sessions = [];

    /// <summary>
    /// 当前那条链路（<see cref="_channel"/>）正在发往哪间教室。
    ///
    /// 由外壳设置：局域网连上时是那间的 UUID，绑着服务器时是绑定的那间。
    /// 语音是实时流，"这一片该不该再单独发给某间"完全取决于它 ——
    /// 少了这个值，被勾中的当前教室会被发两遍（叠音）。
    /// </summary>
    public string? ActiveTargetUuid { get; set; }

    /// <summary>多班发送器。由外壳注入；为 null 时只发当前那一间。</summary>
    public ClassroomBroadcaster? Broadcaster { get; set; }

    /// <summary>把这片 PCM 同时写进每一路（当前链路 + 额外那几间）。</summary>
    private void WriteToAllTargets(ReadOnlyMemory<byte> pcm)
    {
        foreach (var session in _sessions)
        {
            session.Send(pcm);
        }
    }

    /// <summary>收尾时把额外的几路都补上 audioEnd —— 漏掉的话教室端会一直停在"语音喊话中"。</summary>
    private async Task CloseSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = _sessions;
        _sessions = [];

        foreach (var session in sessions)
        {
            try
            {
                await session.CompleteAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Log?.Invoke($"向「{session.Name}」收尾时出错：{ex.Message}");
            }
        }
    }

    /// <summary>记一句日志（绑定失败、某一路收尾出错）。由外壳接到界面上。</summary>
    public event Action<string>? Log;

    public VoiceShoutViewModel(IShoutTransport channel)
    {
        _channel = channel;

        // 平台把"应用进入后台"转到这里，让录音能在切后台/锁屏时自己收尾。
        // 在构造函数里订阅、在 Dispose 里退订，这样就不用把钩子一路从
        // Activity 传到视图模型 —— 谁关心这件事，谁自己接。
        TeacherPlatform.Backgrounded += OnAppBackgrounded;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRecording;

    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private double _elapsed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>本机麦克风采集格式，实际由平台实现决定。</summary>
    [ObservableProperty]
    private string _formatText = AudioFormat.Default.ToString();

    public bool IsIdle => !IsRecording;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>关掉录音区那条错误提示（它同样会自动收起，见下面的超时）。</summary>
    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private CancellationTokenSource? _errorCts;

    /// <summary>
    /// 录音/发送失败的那条提示自己收起。
    ///
    /// 和顶栏那条错误横幅一个道理：关不掉又不会消失的提示会一直占着位置，
    /// 让人以为"现在还是坏的"—— 而麦克风被占用这类问题往往过一会儿就好了。
    /// </summary>
    private void StartErrorTimeout()
    {
        _errorCts?.Cancel();
        _errorCts?.Dispose();
        _errorCts = null;

        if (string.IsNullOrWhiteSpace(ErrorMessage))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _errorCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TeacherShellViewModel.ErrorBannerSeconds), cts.Token)
                    .ConfigureAwait(false);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_errorCts, cts))
                    {
                        ErrorMessage = null;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 被新的错误顶掉、或用户手动关掉，都属正常
            }
        });
    }

    /// <summary>录音区标题，随录音状态切换。</summary>
    public string TitleText => IsRecording ? "正在录音，说完了点一下发送" : "点击麦克风开始喊话";

    public string ElapsedText => TimeSpan.FromSeconds(Elapsed).ToString(@"mm\:ss");

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        StartErrorTimeout();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(TitleText));
    }

    public bool CanToggle => IsConnected;

    /// <summary>点击麦克风：未在录就开录，已在录就停止并发送。</summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        if (IsRecording)
        {
            await StopAndSendAsync().ConfigureAwait(true);
        }
        else
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    private async Task StartAsync()
    {
        ErrorMessage = null;

        if (!TeacherPlatform.HasRecorder)
        {
            ErrorMessage = "本平台尚未注册麦克风采集实现。";
            return;
        }

        // 正在连着的那间一定会收到这次语音：声音是从某个教室的喇叭出来的，
        // 而"当前链路"只有一条。老师把它取消勾选时不该静默地照发，
        // 也不该静默地不发 —— 这里直接把它勾回来，界面上看得见。
        EnsureActiveTargetSelected();

        if (HasExtraTargets && Broadcaster is null)
        {
            ErrorMessage = "这台设备还不能一次喊给多个班：请先登录服务器账号。";
            return;
        }

        try
        {
            _recorder = TeacherPlatform.CreateAudioRecorder();
            _recorder.LevelChanged += OnLevelChanged;
            _recorder.Failed += OnRecorderFailed;

            _cts = new CancellationTokenSource();
            _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            // 先开传输会话，再开麦克风：避免第一片音频早于 audioStart 到达教室端
            await _channel.BeginAudioAsync(_recorder.Format, _cts.Token).ConfigureAwait(true);
            _audioSessionOpen = true;

            // 勾了多间时，另外几间各开一路（绑定 + audioStart）。
            // 放在 _channel 之后、麦克风之前：这几路准备好之前不该开始采集，
            // 否则开头那一两百毫秒只有当前这间听得到。
            await OpenExtraSessionsAsync(_recorder.Format, _cts.Token).ConfigureAwait(true);

            FormatText = _recorder.Format.ToString();
            _pumpTask = PumpAsync(_audioQueue.Reader, _cts.Token);

            await _recorder.StartAsync(OnPcmChunk, _cts.Token).ConfigureAwait(true);

            _elapsedSeconds = 0;
            Elapsed = 0;
            Level = 0;
            IsRecording = true;
            StartTimer();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法开始录音：{ex.Message}";
            await CleanupAsync(closeChannel: true).ConfigureAwait(true);
        }
    }

    private async Task StopAndSendAsync()
    {
        StopTimer();
        var seconds = _elapsedSeconds;
        var wasSent = _audioSessionOpen;

        IsRecording = false;
        Level = 0;

        // 计时归零：这一条已经发出去了，屏幕上还挂着「00:03」会让人以为**还在录**，
        // 或者以为刚才那段没发出去。留着秒数记录在这一句之上（先取再清）。
        ResetElapsed();

        await CleanupAsync(closeChannel: true).ConfigureAwait(true);

        // 只在真的发出去过的时候记：中途取消不该出现在"我喊过什么"里。
        // 语音没法存下内容，留一句说明就够 —— 让老师记起"那会儿喊了一句"。
        if (wasSent && seconds >= 1)
        {
            ShoutHistoryStore.Record($"（语音喊话 {seconds:0} 秒）", isVoice: true);
        }
    }

    /// <summary>把计时清回 00:00（发完、取消、采集失败都要清）。</summary>
    private void ResetElapsed()
    {
        _elapsedSeconds = 0;
        Elapsed = 0;
    }

    // ======================== 发给哪几个班 ========================

    /// <summary>
    /// 可以喊到的班级 —— 与文字页是**同一份**「可发送班级」列表（局域网直连那间
    /// 与服务器上绑定的班级合在一起）。语音这一页此前只能发给当前绑定的那一间，
    /// 而"可发送班级"既然是一张表，勾了几间就该喊给几间。
    /// </summary>
    public ObservableCollection<ShoutTargetItem> Targets { get; } = [];

    /// <summary>由外壳刷新（与文字页共用同一份来源）。</summary>
    public void SyncTargets(IReadOnlyList<ShoutTargetInfo> infos)
    {
        var previous = Targets
            .Where(t => t.IsSelected)
            .Select(t => t.Uuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isFirstFill = Targets.Count == 0;

        Targets.Clear();

        foreach (var info in infos)
        {
            var selected = isFirstFill
                ? info.Source == ShoutTargetSources.Lan || info.IsCurrent
                : previous.Contains(info.Uuid);

            var item = new ShoutTargetItem(info, selected) { IsCurrent = info.IsCurrent };

            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShoutTargetItem.IsSelected))
                {
                    OnPropertyChanged(nameof(TargetHint));
                    OnPropertyChanged(nameof(HasMultipleTargets));
                }
            };

            Targets.Add(item);
        }

        if (Targets.Count > 0 && Targets.All(t => !t.IsSelected))
        {
            var fallback = Targets.FirstOrDefault(t => t.IsLan)
                           ?? Targets.FirstOrDefault(t => t.IsCurrent)
                           ?? Targets[0];

            fallback.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasMultipleTargets));
        OnPropertyChanged(nameof(TargetHint));
    }

    public bool HasTargets => Targets.Count > 0;

    public bool HasMultipleTargets => Targets.Count > 1;

    /// <summary>额外要发的那几间（当前链路已经发的那间不算）。</summary>
    private IReadOnlyList<ShoutTargetItem> ExtraTargets => Targets
        .Where(t => t.IsSelected && !t.IsLan && t.Record is not null)
        .Where(t => ActiveTargetUuid is null || !string.Equals(t.Uuid, ActiveTargetUuid, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private bool HasExtraTargets => ExtraTargets.Count > 0;

    /// <summary>这句话会说给哪几间听。</summary>
    public string TargetHint
    {
        get
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();

            if (Targets.Count == 0)
            {
                return "还没连上任何教室：去「设备」页连一间，或登录服务器账号拿到授权的班级。";
            }

            if (selected.Count <= 1)
            {
                return selected.Count == 0
                    ? "还没勾任何班级 —— 勾一个才会发出去。"
                    : $"这一句只说给「{selected[0].Name}」（{selected[0].SourceLabel}）。";
            }

            var offline = selected.Where(t => !t.IsOnline).ToList();

            var text = $"这一句会同时说给 {selected.Count} 个班级："
                       + string.Join("、", selected.Select(t => t.Name)) + "。";

            return offline.Count == 0
                ? text
                : text + $"其中 {string.Join("、", offline.Select(t => t.Name))} 当前离线，多半收不到。";
        }
    }

    /// <summary>把"当前链路发的那间"勾上（没勾的话）。</summary>
    private void EnsureActiveTargetSelected()
    {
        if (ActiveTargetUuid is null)
        {
            return;
        }

        if (Targets.FirstOrDefault(t =>
                string.Equals(t.Uuid, ActiveTargetUuid, StringComparison.OrdinalIgnoreCase)) is not { } active)
        {
            return;
        }

        if (!active.IsSelected)
        {
            active.IsSelected = true;

            // 界面上的勾会被这一次改动带回来，但老师可能没看见，所以再留一句说明
            Log?.Invoke($"「{active.Name}」是当前连着的教室，这次语音一定会从它这儿发出去 —— 已替你勾上。");
        }
    }

    /// <summary>给额外的几间各开一路语音会话。</summary>
    private async Task OpenExtraSessionsAsync(AudioFormat format, CancellationToken cancellationToken)
    {
        var extras = ExtraTargets;

        if (extras.Count == 0 || Broadcaster is null)
        {
            return;
        }

        var failed = new List<string>();

        _sessions = [.. await Broadcaster
            .BeginVoiceAsync(
                extras.Select(t => t.Record!).ToList(),
                format,
                target => NameForTarget(target.Uuid),
                failed,
                cancellationToken)
            .ConfigureAwait(true)];

        foreach (var reason in failed)
        {
            Log?.Invoke($"这一句没能同时说给 {reason}。");
        }

        if (_sessions.Count > 0)
        {
            Log?.Invoke($"这一句会说到 {_sessions.Count + 1} 个班级。");
        }
    }

    /// <summary>某间教室该用哪个称呼（来源里的科目按班取）。</summary>
    public Func<string?, string>? ShoutNameFor { get; set; }

    private string NameForTarget(string? uuid) => ShoutNameFor?.Invoke(uuid) ?? string.Empty;

    /// <summary>
    /// 中断本次喊话。
    ///
    /// 注意它并**不是**"当作什么都没发生"：音频是边录边实时流的，
    /// audioStart 在开口之前就已经发出去了，教室里可能已经响过一声。
    /// 所以取消同样要补一个 audioEnd —— 原来的注释写着"无论发送还是取消，
    /// 都要告诉教室端会话结束"，但代码传的是 closeChannel: false，
    /// 于是取消之后教室端永远停在"语音喊话中…"，播放设备也一直占着不释放。
    /// </summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        StopTimer();
        IsRecording = false;
        Level = 0;
        ResetElapsed();

        await CleanupAsync(closeChannel: true).ConfigureAwait(true);
    }

    private void OnPcmChunk(ReadOnlyMemory<byte> pcm)
    {
        // 采集线程上执行：只做入队，不做任何阻塞操作
        var copy = pcm.ToArray();
        _audioQueue?.Writer.TryWrite(copy);
    }

    private void OnLevelChanged(object? sender, float value) => Post(() => Level = value);

    /// <summary>
    /// 采集中途失败。
    ///
    /// 必须把它当成"这一路喊话结束了"来处理：界面要停止计时、按钮要恢复，
    /// 而且要给教室端补一个 audioEnd —— 否则教室端会一直停在"语音喊话中"。
    /// </summary>
    private void OnRecorderFailed(object? sender, string message)
    {
        Post(() =>
        {
            ErrorMessage = message;

            if (IsRecording)
            {
                _ = CancelAsync();
            }
        });
    }

    private async Task PumpAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _channel.SendAudioAsync(chunk, cancellationToken).ConfigureAwait(false);

                // 同一片再写进额外那几路：语音是实时流，几间教室必须听到同一段声音，
                // 而不是"这一段给 A、下一段给 B"。
                WriteToAllTargets(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            Post(() => ErrorMessage = $"发送中断：{ex.Message}");
        }
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            _elapsedSeconds += 0.1;
            Elapsed = _elapsedSeconds;

            // 到达上限自动结束，避免一直录下去
            if (_elapsedSeconds >= MaxDuration.TotalSeconds)
            {
                _ = StopAndSendAsync();
            }
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>拆掉本次录音涉及的所有资源。</summary>
    private async Task CleanupAsync(bool closeChannel)
    {
        // 进入时就把这一套资源抓进局部变量。
        //
        // 方法体里有好几个 await（停麦克风、排空队列，最长要等两秒），
        // 这期间用户完全可能再点一次"按住说话"—— 新的一套 _cts / _audioQueue
        // 已经挂到字段上了。原来的清理在后半段直接 _cts?.Cancel()、
        // _audioQueue = null，动的是**字段**，也就是刚起步的那次新录音：
        // 新录音无声无息地死掉，界面还显示正在录。
        var recorder = _recorder;
        var cts = _cts;
        var queue = _audioQueue;
        var pump = _pumpTask;

        if (ReferenceEquals(_recorder, recorder))
        {
            _recorder = null;
        }

        if (recorder is not null)
        {
            recorder.LevelChanged -= OnLevelChanged;
            recorder.Failed -= OnRecorderFailed;
            try
            {
                await recorder.StopAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // 停止失败不影响后续清理
            }

            await recorder.DisposeAsync().ConfigureAwait(true);
        }

        // 先让队列里剩余的音频写完，再关闭会话，否则最后一句会被截断
        queue?.Writer.TryComplete();
        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 写不完就算了，不能因此卡住界面
            }
        }

        cts?.Cancel();
        cts?.Dispose();

        // 只清"还是我这一套"的字段，别把期间新建的那一套抹掉
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }

        if (ReferenceEquals(_audioQueue, queue))
        {
            _audioQueue = null;
        }

        if (ReferenceEquals(_pumpTask, pump))
        {
            _pumpTask = null;
        }

        // 只要教室端已经进入过播放状态，就必须告诉它会话结束 ——
        // 否则教室端界面一直停在"语音喊话中"，播放设备也一直占着不释放。
        // 没开过会话就什么都不用发：那种情况下教室端根本没进过播放状态。
        if (closeChannel && _audioSessionOpen)
        {
            try
            {
                await _channel.EndAudioAsync().ConfigureAwait(true);
                _audioSessionOpen = false;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                // 连接已断时忽略
            }
        }

        // 额外那几路同样要收尾：漏掉的话那几间教室会一直停在"语音喊话中"。
        // 放在最后、且不受 closeChannel 影响 —— 取消时也要收，因为它们已经收到过 audioStart。
        await CloseSessionsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>连接断开时把界面恢复到可操作状态。</summary>
    public void ResetOnDisconnect()
    {
        StopTimer();
        IsRecording = false;
        Level = 0;
        _ = CleanupAsync(closeChannel: false);
    }

    /// <summary>平台通知"应用进入后台"时要走的那条路（见 TeacherPlatform.Backgrounded）。</summary>
    private void OnAppBackgrounded()
    {
        if (!IsRecording)
        {
            return;
        }

        global::System.Diagnostics.Debug.WriteLine("应用进入后台，结束进行中的录音。");
        _ = CancelAsync();
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public void Dispose()
    {
        TeacherPlatform.Backgrounded -= OnAppBackgrounded;

        StopTimer();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // 有界等待即可：这里跑在可能已经进入后台的线程上，
        // 无限等下去等于让调用方挂住。recorder 内部自己有释放逻辑。
        _recorder?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        _recorder = null;
    }
}
