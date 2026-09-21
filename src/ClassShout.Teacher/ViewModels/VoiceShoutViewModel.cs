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
    private Channel<byte[]>? _audioQueue;
    private Task? _pumpTask;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private double _elapsedSeconds;

    public VoiceShoutViewModel(IShoutTransport channel)
    {
        _channel = channel;
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

    /// <summary>录音区标题，随录音状态切换。</summary>
    public string TitleText => IsRecording ? "正在录音，说完了点一下发送" : "点击麦克风开始喊话";

    public string ElapsedText => TimeSpan.FromSeconds(Elapsed).ToString(@"mm\:ss");

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

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

        try
        {
            _recorder = TeacherPlatform.CreateAudioRecorder();
            _recorder.LevelChanged += OnLevelChanged;

            _cts = new CancellationTokenSource();
            _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            // 先开传输会话，再开麦克风：避免第一片音频早于 audioStart 到达教室端
            await _channel.BeginAudioAsync(_recorder.Format, _cts.Token).ConfigureAwait(true);

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
        IsRecording = false;
        Level = 0;

        await CleanupAsync(closeChannel: true).ConfigureAwait(true);
    }

    /// <summary>放弃本次录音，不发送给教室端。</summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        StopTimer();
        IsRecording = false;
        Level = 0;
        await CleanupAsync(closeChannel: false).ConfigureAwait(true);
    }

    private void OnPcmChunk(ReadOnlyMemory<byte> pcm)
    {
        // 采集线程上执行：只做入队，不做任何阻塞操作
        var copy = pcm.ToArray();
        _audioQueue?.Writer.TryWrite(copy);
    }

    private void OnLevelChanged(object? sender, float value) => Post(() => Level = value);

    private async Task PumpAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _channel.SendAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
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
        var recorder = _recorder;
        _recorder = null;

        if (recorder is not null)
        {
            recorder.LevelChanged -= OnLevelChanged;
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
        _audioQueue?.Writer.TryComplete();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 写不完就算了，不能因此卡住界面
            }

            _pumpTask = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _audioQueue = null;

        if (closeChannel)
        {
            // 无论发送还是取消，都要告诉教室端会话结束，
            // 否则教室端界面会一直停在"语音喊话中"。
            // EndAudioAsync 在没有活动会话时是空操作，无需额外判断。
            try
            {
                await _channel.EndAudioAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                // 连接已断时忽略
            }
        }
    }

    /// <summary>连接断开时把界面恢复到可操作状态。</summary>
    public void ResetOnDisconnect()
    {
        StopTimer();
        IsRecording = false;
        Level = 0;
        _ = CleanupAsync(closeChannel: false);
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
        StopTimer();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _recorder?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        _recorder = null;
    }
}
