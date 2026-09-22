using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassShout.Classroom.Services;
using ClassShout.Core.Audio;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

/// <summary>教室端当前处于哪种展示状态，界面据此切换中间的大字区。</summary>
public enum ClassroomStage
{
    /// <summary>待机，等待教师端连接。</summary>
    Idle,

    /// <summary>正在用系统 TTS 朗读文字喊话。</summary>
    SpeakingText,

    /// <summary>正在播放教师端的语音喊话。</summary>
    PlayingAudio,
}

/// <summary>一条运行日志。</summary>
/// <param name="Time">发生时间。</param>
/// <param name="Kind">类别：连接 / 文字 / 语音 / 系统。</param>
/// <param name="Message">内容。</param>
public sealed record LogEntry(DateTime Time, string Kind, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>已连接的教师端摘要。</summary>
public sealed record TeacherInfo(string Name, string RemoteEndPoint, int TextCount, int AudioCount);

/// <summary>
/// 教室端主视图模型。
///
/// 职责边界：本类只做编排 —— 收到什么、该干什么、界面显示什么。
/// 真正的网络收发在 <see cref="ClassroomServer"/>，朗读在 WindowsSpeechSynthesizer，
/// 播放音在 NAudioLoopbackPlayer，三者互不知道对方存在。
/// </summary>
public partial class ClassroomViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ClassroomServer _server;
    private readonly ClassroomAnnouncer _announcer;
    private readonly WindowsSpeechSynthesizer _speech;
    private readonly NAudioLoopbackPlayer _player = new();
    private readonly Dictionary<string, TeacherSession> _sessions = [];
    private readonly Dictionary<string, int> _textCounts = [];
    private readonly Dictionary<string, int> _audioCounts = [];

    /// <summary>当前这一路语音的格式。按它算时长，而不是用默认格式 —— 采样率可能不同。</summary>
    private AudioFormat _currentAudioFormat = AudioFormat.Default;

    /// <summary>
    /// 当前这一路语音的归属与格式。
    ///
    /// 单独加一把锁：写入发生在 UI 线程（收到 audioStart 之后），
    /// 读取发生在接收线程（每个音频分片都要按格式算时长）。
    /// 原来直接裸读写一个结构体字段，既可能读到撕裂的值，
    /// 也拦不住"甲老师刚开始、乙老师在路上的分片混进来"。
    /// </summary>
    private readonly Lock _audioStateLock = new();
    private string? _audioOwner;

    // —— 跨局域网中继 ——
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ClassroomRelaySettings _relaySettings;
    private ClassroomRelayClient? _relay;

    // —— 屏幕边缘弹窗 ——
    private readonly ClassroomNotificationSettings _notificationSettings;
    private readonly NotificationPresenter _notificationPresenter;

    public ClassroomViewModel(int port = ShoutProtocol.DefaultTcpPort, int discoveryPort = ShoutProtocol.DefaultDiscoveryPort)
    {
        _server = new ClassroomServer(port);
        _announcer = new ClassroomAnnouncer(discoveryPort);
        _speech = new WindowsSpeechSynthesizer();

        // 读出本机身份：UUID 首次启动生成一次后永久保留，是这台教室在服务器上的身份
        _relaySettings = LocalSettings.LoadClassroom();
        _relaySettings.EnsureUuid();
        _relaySettings.ClassroomName = ClassroomName;
        LocalSettings.SaveClassroom(_relaySettings);

        RelayServerUrl = _relaySettings.ServerUrl ?? string.Empty;
        RelayUuid = _relaySettings.Uuid;
        RelaySecret = _relaySettings.Secret;

        // 让协议层在握手完成后自动回状态，内容由这里提供运行态
        _server.StatusFactory = BuildStatus;

        _server.Log += message => Post(() => AddLog("系统", message));
        _server.SessionOpened += OnSessionOpened;
        // 刻意不订阅 _server.SessionClosed：会话自身的 Closed 事件一定会触发，
        // 且带上了断开原因。两个都订阅会导致同一次断开记录两遍日志。
        _speech.SpeakingChanged += (_, speaking) => Post(() =>
        {
            IsSpeaking = speaking;
            if (speaking)
            {
                Stage = ClassroomStage.SpeakingText;
            }
            else if (Stage == ClassroomStage.SpeakingText)
            {
                Stage = ClassroomStage.Idle;
            }
        });

        foreach (var voice in _speech.GetVoices())
        {
            Voices.Add(voice);
        }

        SelectedVoice = _speech.GetDefaultChineseVoice() ?? Voices.FirstOrDefault();

        LocalAddress = $"{NetworkUtility.GetLocalIPv4()}:{port}";

        // 弹窗配置与呈现器。呈现器必须在这里建（构造函数跑在 UI 线程上），
        // 它内部持有 Avalonia 窗口，换线程创建会拿到 null 的 Dispatcher。
        _notificationSettings = ClassroomNotificationSettings.Load();
        _notificationPresenter = new NotificationPresenter(_notificationSettings);

        NotificationEnabled = _notificationSettings.Enabled;
        NotificationDuration = _notificationSettings.DurationSeconds;
        SelectedCorner = CornerOptions.FirstOrDefault(o => o.Value == _notificationSettings.Corner) ?? CornerOptions[1];
        SelectedTopmost = TopmostOptions.FirstOrDefault(o => o.Value == _notificationSettings.Topmost) ?? TopmostOptions[2];
    }

    // ======================== 可绑定状态 ========================

    [ObservableProperty]
    private string _classroomName = "三年二班";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalAddressDisplay))]
    private string _localAddress = string.Empty;

    [ObservableProperty]
    private ClassroomStage _stage = ClassroomStage.Idle;

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string _currentSpeaker = string.Empty;

    [ObservableProperty]
    private string _statusText = "等待教师端连接";

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private double _audioLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private int _volume = 100;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>TTS 语速，-10 ~ 10。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateText))]
    private int _rate = 1;

    [ObservableProperty]
    private string? _selectedVoice;

    [ObservableProperty]
    private bool _isServerRunning;

    /// <summary>正在播放的语音已持续秒数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioSecondsText))]
    private double _audioSeconds;

    // ======================== 界面显示用派生属性 ========================
    // 放在视图模型里而不是 XAML 的 StringFormat：格式化逻辑可测试，也让 XAML 更干净。

    public string LocalAddressDisplay => $"教室端 {LocalAddress}";

    public string AudioSecondsText => $"{AudioSeconds:F1} 秒";

    public string VolumeText => $"{Volume}%";

    public string RateText => Rate switch
    {
        < 0 => $"偏慢（{Rate}）",
        > 0 => $"偏快（+{Rate}）",
        _ => "正常语速",
    };

    /// <summary>中间大字区的标题。</summary>
    public string StageHeadline => Stage switch
    {
        ClassroomStage.SpeakingText => "正在朗读文字喊话",
        ClassroomStage.PlayingAudio => "正在播放语音喊话",
        _ => "等待教师端连接",
    };

    /// <summary>待机时的引导语。</summary>
    public string IdleHint => Teachers.Count == 0
        ? "教师端与本机处于同一局域网时，打开应用即可自动搜索到本教室"
        : "教师端已连接，随时可以开始喊话";

    public ObservableCollection<string> Voices { get; } = [];

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public ObservableCollection<TeacherInfo> Teachers { get; } = [];

    public bool IsIdle => Stage == ClassroomStage.Idle;

    public bool IsSpeakingText => Stage == ClassroomStage.SpeakingText;

    public bool IsPlayingAudio => Stage == ClassroomStage.PlayingAudio;

    public bool HasTeachers => Teachers.Count > 0;

    public string TeacherCountText => Teachers.Count == 0 ? "未连接" : $"{Teachers.Count} 个教师端在线";

    /// <summary>仅在播放语音时才刷新波形，避免待机时白白重绘。</summary>
    public bool ShouldMeterAudio => Stage == ClassroomStage.PlayingAudio;

    partial void OnStageChanged(ClassroomStage value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsSpeakingText));
        OnPropertyChanged(nameof(IsPlayingAudio));
        OnPropertyChanged(nameof(ShouldMeterAudio));
        OnPropertyChanged(nameof(StageHeadline));

        StatusText = value switch
        {
            ClassroomStage.SpeakingText => "正在朗读文字喊话",
            ClassroomStage.PlayingAudio => "正在播放语音喊话",
            _ => Teachers.Count == 0 ? "等待教师端连接" : "已就绪",
        };
    }

    partial void OnCurrentSpeakerChanged(string value) => OnPropertyChanged(nameof(SpeakerText));

    /// <summary>说话人显示串，未连接时给一个中性提示。</summary>
    public string SpeakerText => string.IsNullOrWhiteSpace(CurrentSpeaker) ? "教师端" : CurrentSpeaker;

    partial void OnClassroomNameChanged(string value)
    {
        _server.ClassroomName = value;
        _announcer.Current = BuildAnnouncement();
        NotifyTeachers();

        // 改了教室名要同步到服务器，否则控制台上显示的还是旧名字。
        // 重新注册即可 —— RegisterOrVerify 在名字不同时会更新记录，并顺带换发会话令牌。
        _relaySettings.ClassroomName = value;
        LocalSettings.SaveClassroom(_relaySettings);

        if (_relay is { IsConnected: true })
        {
            _ = RefreshRelayRegistrationAsync();
        }
    }

    /// <summary>
    /// 重新注册以同步教室名。
    ///
    /// 之所以要拆掉旧连接再重建：注册会换发新令牌，旧的轮询循环仍拿着旧令牌，
    /// 不换掉的话它会一直收到 401 并不断触发"重新注册"，形成来回打架。
    /// </summary>
    private async Task RefreshRelayRegistrationAsync()
    {
        // 调用方已经判过非空，但方法签名上 _relay 仍是可空的 —— 这里再取一次局部引用，
        // 既消掉了可空警告，也避免了 await 期间字段被别处改掉的可能。
        var previous = _relay;
        _relay = null;

        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(true);
        }

        var client = new ClassroomRelayClient(_http, _relaySettings);
        client.ShoutReceived += OnRelayShoutReceived;
        client.ConnectionChanged += connected => Post(() => IsRelayConnected = connected);
        client.Log += message => Post(() => AddLog("服务器", message));

        if (await client.RegisterAsync(message => Post(() => AddLog("服务器", message))).ConfigureAwait(true))
        {
            _relaySettings.Secret = client.Secret ?? _relaySettings.Secret;
            RelaySecret = _relaySettings.Secret;
            LocalSettings.SaveClassroom(_relaySettings);

            client.StartPolling();
            _relay = client;
            IsRelayConnected = true;
            AddLog("服务器", $"教室名已同步为「{ClassroomName}」。");
        }
        else
        {
            await client.DisposeAsync().ConfigureAwait(true);
            IsRelayConnected = false;
            AddLog("服务器", "教室名同步失败，中继链路已断开。");
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (value)
        {
            StopEverything("已静音");
        }

        NotifyTeachers();
    }

    partial void OnVolumeChanged(int value) => NotifyTeachers();

    // ======================== 生命周期 ========================

    /// <summary>启动监听。端口被占用时抛 <see cref="System.Net.Sockets.SocketException"/>。</summary>
    public void Start()
    {
        _server.ClassroomName = ClassroomName;
        _server.Start();
        IsServerRunning = true;

        _announcer.Current = BuildAnnouncement();
        _announcer.Start();

        AddLog("系统", $"教室端已启动，监听 {LocalAddress}");
        AddLog("系统", $"UDP 发现已开启，教师端可自动搜索到「{ClassroomName}」");

        if (!string.IsNullOrEmpty(SelectedVoice))
        {
            AddLog("系统", $"系统 TTS 语音：{SelectedVoice}");
        }
    }

    private ClassroomAnnouncement BuildAnnouncement() => new()
    {
        Id = $"{Environment.MachineName}-{_server.Port}",
        Name = ClassroomName,
        Host = NetworkUtility.GetLocalIPv4(),
        Port = _server.Port,
        ProtocolVersion = ShoutProtocol.Version,
        AppVersion = typeof(ClassroomViewModel).Assembly.GetName().Version?.ToString(3),
    };

    // ======================== 服务端事件 ========================

    private void OnSessionOpened(object? sender, TeacherSession session)
    {
        lock (_sessions)
        {
            _sessions[session.Id] = session;
            _textCounts[session.Id] = 0;
            _audioCounts[session.Id] = 0;
        }

        session.TextShoutReceived += (_, message) => OnTextShout(session, message);
        session.AudioStarted += (_, message) => OnAudioStarted(session, message);
        session.AudioChunkReceived += (_, payload) => OnAudioChunk(LanOwnerKey(session), payload);
        session.AudioEnded += (_, message) => _ = OnAudioEndedAsync(message);
        session.StopRequested += (_, message) => Post(() => StopEverything($"教师端请求停止：{message.Reason}"));
        session.Closed += (_, reason) => OnSessionClosed(session, reason);

        Post(() =>
        {
            AddLog("连接", $"教师端接入：{session.RemoteEndPoint}");
            RefreshTeachers();
        });
    }

    private void OnSessionClosed(TeacherSession session, string reason)
    {
        lock (_sessions)
        {
            _sessions.Remove(session.Id);

            // 计数表与 _sessions 同生共死。以前只清 _sessions，
            // 而每次重连都是一个新的 session.Id —— 计数表只增不减，
            // 一台整天有人连来连去的教室端，这个字典会一直长。
            _textCounts.Remove(session.Id);
            _audioCounts.Remove(session.Id);
        }

        Post(() =>
        {
            AddLog("连接", $"教师端离开：{reason}");
            RefreshTeachers();
        });
    }

    private void OnTextShout(TeacherSession session, TextShoutMessage message)
    {
        lock (_textCounts)
        {
            _textCounts[session.Id] = _textCounts.GetValueOrDefault(session.Id) + 1;
        }

        Post(() =>
        {
            RefreshTeachers();
            PresentTextShout(session.ClientName, message);
        });
    }

    /// <summary>
    /// 把一条文字喊话呈现出来（朗读 + 更新界面）。
    ///
    /// 局域网直连与公网中继两条路径共用这里 —— 它们只是"消息怎么来"不同，
    /// "收到之后做什么"必须完全一致，否则两条链路的行为会慢慢分叉。
    /// </summary>
    private void PresentTextShout(string sourceName, TextShoutMessage message)
    {
        AddLog("文字", $"「{sourceName}」说：{message.Text}");

        if (IsMuted)
        {
            AddLog("文字", "已静音，本条不朗读");
            return;
        }

        CurrentSpeaker = sourceName;
        CurrentText = message.Text;
        Stage = ClassroomStage.SpeakingText;

        NotifyOnScreen(sourceName, message.Text, isVoice: false);

        _ = SpeakAsync(message);
    }

    private async Task SpeakAsync(TextShoutMessage message)
    {
        try
        {
            if (message.Interrupt)
            {
                // 新内容打断旧的，符合"喊话"的直觉：后说的覆盖先说的
                _speech.Stop();
            }

            var options = new SpeechRequestOptions(
                message.Rate != 0 ? message.Rate : Rate,
                message.Volume is > 0 and <= 100 ? message.Volume : Volume,
                message.VoiceName ?? SelectedVoice);

            await _speech.SpeakAsync(message.Text, options).ConfigureAwait(false);

            await PostAsync(() =>
            {
                if (Stage == ClassroomStage.SpeakingText)
                {
                    Stage = ClassroomStage.Idle;
                }
            });
        }
        catch (Exception ex)
        {
            Post(() => AddLog("系统", $"朗读失败：{ex.Message}"));
        }
    }

    private void OnAudioStarted(TeacherSession session, AudioStartMessage message)
    {
        // 用 _sessions 这一把锁：_sessions / _textCounts / _audioCounts 总是被一起访问，
        // 各用各的锁等于没有同步 —— 刷新教师列表那一侧读的就是未同步的数据。
        lock (_sessions)
        {
            _audioCounts[session.Id] = _audioCounts.GetValueOrDefault(session.Id) + 1;
        }

        Post(() =>
        {
            RefreshTeachers();
            PresentAudioStart(
                LanOwnerKey(session),
                session.ClientName,
                new AudioFormat(message.SampleRate, message.Channels, message.BitsPerSample));
        });
    }

    /// <summary>局域网来源的归属键。用会话 Id 而不是姓名：同名老师会互相串台。</summary>
    private static string LanOwnerKey(TeacherSession session) => "lan:" + session.Id;

    /// <summary>中继来源的归属键。中继侧拿不到会话 Id，只能用服务器确认过的姓名。</summary>
    private static string RelayOwnerKey(RelayEnvelope envelope) => "relay:" + (envelope.From ?? "教师端");

    /// <summary>开始播放一路语音。局域网与中继两条路径共用。</summary>
    private void PresentAudioStart(string ownerKey, string sourceName, AudioFormat format)
    {
        // 格式来自网络对端，先确认它合法再往下走。
        // NAudio 的 WaveFormat 构造函数会校验参数并抛异常，而这里是 UI 线程 ——
        // 对端发一个 Channels = 0 就能把整个教室端进程打掉。
        if (!format.IsSupported)
        {
            AddLog("语音", $"「{sourceName}」的音频格式不受支持（{format}），本次语音已忽略");
            return;
        }

        if (IsMuted)
        {
            AddLog("语音", "已静音，本条不播放");
            return;
        }

        // 语音优先：打断正在进行的朗读
        _speech.Stop();
        _player.Stop();

        lock (_audioStateLock)
        {
            _audioOwner = ownerKey;
            _currentAudioFormat = format;
        }

        try
        {
            _player.Start(format);
        }
        catch (Exception ex) when (ex is NotSupportedException or NAudio.MmException or InvalidOperationException)
        {
            // 声卡被占用、格式被驱动拒绝之类：记一笔就好，不该让教室端退出
            lock (_audioStateLock)
            {
                _audioOwner = null;
            }

            AddLog("语音", $"无法开始播放：{ex.Message}");
            return;
        }

        CurrentSpeaker = sourceName;
        CurrentText = "语音喊话中…";
        AudioSeconds = 0;
        AudioLevel = 0;
        Stage = ClassroomStage.PlayingAudio;
        AddLog("语音", $"「{sourceName}」开始语音喊话（{format}）");

        NotifyOnScreen(sourceName, "（语音喊话，正在教室播放）", isVoice: true);
    }

    private void OnAudioChunk(string ownerKey, ReadOnlyMemory<byte> payload)
    {
        if (IsMuted || payload.IsEmpty)
        {
            return;
        }

        AudioFormat format;
        lock (_audioStateLock)
        {
            // 分片必须属于当前正在播的那一路。
            // 否则甲老师刚开始的语音里会混进乙老师还在路上的分片 ——
            // 教室里听起来是两个人叠着说，时长也会按错误的格式算出来。
            if (!string.Equals(_audioOwner, ownerKey, StringComparison.Ordinal))
            {
                return;
            }

            format = _currentAudioFormat;
        }

        _player.Write(payload.Span);

        var level = PcmLevel.Compute(payload.Span);
        Post(() =>
        {
            AudioLevel = level;

            // 按这一路语音的实际采样率累计时长，不能用默认格式
            AudioSeconds += format.DurationMsOf(payload.Length) / 1000d;
        });
    }

    private async Task OnAudioEndedAsync(AudioEndMessage message) => await EndAudioAsync().ConfigureAwait(false);

    /// <summary>结束当前语音播放。局域网与中继两条路径共用。</summary>
    private async Task EndAudioAsync()
    {
        await _player.CompleteAsync().ConfigureAwait(false);

        lock (_audioStateLock)
        {
            _audioOwner = null;
        }

        await PostAsync(() =>
        {
            if (Stage == ClassroomStage.PlayingAudio)
            {
                Stage = ClassroomStage.Idle;
                AudioLevel = 0;
                CurrentText = string.Empty;
            }

            AddLog("语音", $"语音喊话结束，时长 {AudioSeconds:F1} 秒");
        });
    }

    // ======================== 命令 ========================

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void StopEverything() => StopEverything("已手动停止");

    private void StopEverything(string reason)
    {
        _speech.Stop();
        _player.Stop();

        lock (_audioStateLock)
        {
            _audioOwner = null;
        }

        Stage = ClassroomStage.Idle;
        AudioLevel = 0;
        CurrentText = string.Empty;
        CurrentSpeaker = string.Empty;
        AddLog("系统", reason);
    }

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    [RelayCommand]
    private void TestSpeech()
    {
        if (IsMuted)
        {
            AddLog("系统", "已静音，测试朗读被跳过");
            return;
        }

        var text = $"我是{ClassroomName}，系统语音测试正常。";
        CurrentSpeaker = "本机测试";
        CurrentText = text;
        Stage = ClassroomStage.SpeakingText;
        _ = SpeakAsync(new TextShoutMessage { Text = text, Rate = Rate, Volume = Volume });
    }

    /// <summary>组装当前运行态，既用于握手回包，也用于主动推送。</summary>
    private StatusMessage BuildStatus() => new()
    {
        State = Stage.ToString().ToLowerInvariant(),
        ClassroomName = ClassroomName,
        Muted = IsMuted,
        Volume = Volume,
    };

    /// <summary>把最新的状态推给所有教师端，让教师端界面能实时反映教室端的静音、音量等。</summary>
    private void NotifyTeachers()
    {
        TeacherSession[] sessions;
        lock (_sessions)
        {
            sessions = _sessions.Values.ToArray();
        }

        var status = BuildStatus();

        // 局域网直连的教师端
        foreach (var session in sessions)
        {
            _ = session.SendAsync(status);
        }

        // 走公网中继的教师端：服务器会把它转发给所有已绑定的会话
        if (_relay is { IsConnected: true })
        {
            _ = _relay.ReportStatusAsync(IsMuted, Volume, Stage.ToString().ToLowerInvariant());
        }
    }

    private void RefreshTeachers()
    {
        lock (_sessions)
        {
            Teachers.Clear();
            foreach (var session in _sessions.Values)
            {
                Teachers.Add(new TeacherInfo(
                    session.ClientName,
                    session.RemoteEndPoint,
                    _textCounts.GetValueOrDefault(session.Id),
                    _audioCounts.GetValueOrDefault(session.Id)));
            }
        }

        OnPropertyChanged(nameof(HasTeachers));
        OnPropertyChanged(nameof(TeacherCountText));
        OnPropertyChanged(nameof(IdleHint));

        if (Stage == ClassroomStage.Idle)
        {
            StatusText = Teachers.Count == 0 ? "等待教师端连接" : "已就绪";
        }
    }

    private void AddLog(string kind, string message)
    {
        Logs.Insert(0, new LogEntry(DateTime.Now, kind, message));

        // 日志只保留最近 200 条，长时间运行不会撑爆内存
        while (Logs.Count > 200)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
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

    private static Task PostAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    // ======================== 跨局域网中继 ========================

    /// <summary>中继服务器地址，形如 https://relay.example.com 或 http://192.168.1.10:8080。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectRelayCommand))]
    private string _relayServerUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelayStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ConnectRelayCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectRelayCommand))]
    private bool _isRelayConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectRelayCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectRelayCommand))]
    private bool _isRelayBusy;

    /// <summary>本教室的全局唯一标识。首次启动生成后永久保留，是它在服务器上的身份。</summary>
    [ObservableProperty]
    private string _relayUuid = string.Empty;

    /// <summary>注册口令。教师端绑定本教室时要同时提供 UUID 与它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelaySecret))]
    [NotifyPropertyChangedFor(nameof(SecretDisplay))]
    private string? _relaySecret;

    [ObservableProperty]
    private string? _relayError;

    public bool HasRelaySecret => !string.IsNullOrWhiteSpace(RelaySecret);

    /// <summary>口令展示串。没有口令时给出可操作的提示，而不是留一片空白让人猜。</summary>
    public string SecretDisplay => HasRelaySecret ? RelaySecret! : "尚未注册 —— 填入服务器地址并连接后自动生成";

    public bool HasRelayError => !string.IsNullOrWhiteSpace(RelayError);

    public string RelayStatusText => IsRelayConnected ? "已连接服务器" : "未连接服务器";

    partial void OnRelayErrorChanged(string? value) => OnPropertyChanged(nameof(HasRelayError));

    /// <summary>界面请求把某段文本放进剪贴板。视图模型不直接碰剪贴板，避免依赖 UI 层。</summary>
    public event Action<string>? CopyRequested;

    /// <summary>视图在复制成功后回调，让操作在日志里留痕，用户才有"确实复制了"的反馈。</summary>
    public void NotifyCopied(string what) => AddLog("系统", $"已复制到剪贴板：{what}");

    private bool CanConnectRelay => !IsRelayBusy && !IsRelayConnected && !string.IsNullOrWhiteSpace(RelayServerUrl);

    private bool CanDisconnectRelay => !IsRelayBusy && IsRelayConnected;

    /// <summary>保存服务器地址并连接。首次连接会在服务器上注册本教室并取得口令。</summary>
    [RelayCommand(CanExecute = nameof(CanConnectRelay))]
    private async Task ConnectRelayAsync()
    {
        IsRelayBusy = true;
        RelayError = null;

        try
        {
            if (!TryNormalizeServerUrl(RelayServerUrl, out var normalized, out var urlError))
            {
                RelayError = urlError;
                return;
            }

            RelayServerUrl = normalized;
            _relaySettings.ServerUrl = normalized;
            _relaySettings.ClassroomName = ClassroomName;
            _relaySettings.EnsureUuid();
            RelayUuid = _relaySettings.Uuid;

            if (!LocalSettings.SaveClassroom(_relaySettings))
            {
                AddLog("服务器", "配置保存失败，本次连接继续，但重启后需要重新填写。");
            }

            _relay = new ClassroomRelayClient(_http, _relaySettings);
            _relay.ShoutReceived += OnRelayShoutReceived;
            _relay.ConnectionChanged += connected => Post(() => IsRelayConnected = connected);
            _relay.Log += message => Post(() => AddLog("服务器", message));

            var ok = await _relay.RegisterAsync(message => Post(() => AddLog("服务器", message))).ConfigureAwait(true);
            if (!ok)
            {
                RelayError = "连接失败。请检查服务器地址与网络，以及本机是否已保存过口令。";
                await _relay.DisposeAsync().ConfigureAwait(true);
                _relay = null;
                return;
            }

            // 服务器可能在首次注册时生成了口令，必须立刻落盘
            _relaySettings.Secret = _relay.Secret ?? _relaySettings.Secret;
            RelaySecret = _relaySettings.Secret;
            LocalSettings.SaveClassroom(_relaySettings);

            _relay.StartPolling();
            IsRelayConnected = true;

            if (_relay.WasNewlyRegistered)
            {
                AddLog("服务器", $"本教室已注册。UUID：{RelayUuid}");
                AddLog("服务器", $"口令：{RelaySecret} —— 请抄给老师，教师端绑定时要填这两项。");
            }

            _ = _relay.ReportStatusAsync(IsMuted, Volume, Stage.ToString().ToLowerInvariant());
        }
        catch (Exception ex)
        {
            RelayError = $"连接出错：{ex.Message}";
        }
        finally
        {
            IsRelayBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectRelay))]
    private async Task DisconnectRelayAsync()
    {
        IsRelayBusy = true;

        try
        {
            if (_relay is not null)
            {
                await _relay.DisposeAsync().ConfigureAwait(true);
                _relay = null;
            }

            IsRelayConnected = false;
            AddLog("服务器", "已断开与中继服务器的连接。局域网直连不受影响。");
        }
        finally
        {
            IsRelayBusy = false;
        }
    }

    [RelayCommand]
    private void CopyRelayUuid() => CopyRequested?.Invoke(RelayUuid);

    [RelayCommand]
    private void CopyRelaySecret()
    {
        if (HasRelaySecret)
        {
            CopyRequested?.Invoke(RelaySecret!);
        }
    }

    /// <summary>
    /// 重新生成 UUID。
    /// 这等于把本机变成"另一间教室"：服务器上的旧记录与已绑定的教师端都会失效，
    /// 必须重新注册并重新分发口令，所以界面要给出明确确认。
    /// </summary>
    [RelayCommand]
    private async Task RegenerateUuidAsync()
    {
        if (IsRelayConnected)
        {
            await DisconnectRelayAsync().ConfigureAwait(true);
        }

        _relaySettings.Uuid = Guid.NewGuid().ToString("D");
        _relaySettings.Secret = null;
        LocalSettings.SaveClassroom(_relaySettings);

        RelayUuid = _relaySettings.Uuid;
        RelaySecret = null;
        RelayError = null;

        AddLog("服务器", "已生成新的 UUID 并清除口令，请重新连接服务器完成注册。");
    }

    /// <summary>把中继投递的事件转成与局域网一致的呈现流程。</summary>
    private void OnRelayShoutReceived(RelayEnvelope envelope)
    {
        Post(() =>
        {
            switch (envelope.Kind)
            {
                case RelayKinds.TextShout:
                    PresentTextShout(envelope.From ?? "教师端", new TextShoutMessage
                    {
                        Text = envelope.Text ?? string.Empty,
                        Rate = envelope.Rate,
                        Volume = envelope.Volume,
                        Interrupt = envelope.Interrupt,
                    });
                    break;

                case RelayKinds.AudioStart:
                    PresentAudioStart(
                        RelayOwnerKey(envelope),
                        envelope.From ?? "教师端",
                        new AudioFormat(envelope.SampleRate, envelope.Channels, envelope.BitsPerSample));
                    break;

                case RelayKinds.Audio:
                    if (!string.IsNullOrEmpty(envelope.AudioBase64))
                    {
                        OnAudioChunk(RelayOwnerKey(envelope), Convert.FromBase64String(envelope.AudioBase64));
                    }

                    break;

                case RelayKinds.AudioEnd:
                    _ = EndAudioAsync();
                    break;

                case RelayKinds.Stop:
                    StopEverything($"教师端请求停止：{envelope.Reason}");
                    break;
            }
        });
    }

    /// <summary>规范化并校验服务器地址。补全协议头，去掉末尾斜杠。</summary>
    private static bool TryNormalizeServerUrl(string input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "请填写服务器地址。";
            return false;
        }

        // 允许只填 192.168.1.10:8080，自动补 http://
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "地址格式不对，应形如 https://relay.example.com 或 http://192.168.1.10:8080";
            return false;
        }

        normalized = text.TrimEnd('/');
        return true;
    }

    // ======================== 屏幕边缘弹窗 ========================

    /// <summary>是否在收到喊话时于屏幕边缘弹窗提示。</summary>
    [ObservableProperty]
    private bool _notificationEnabled;

    /// <summary>弹窗位置。</summary>
    [ObservableProperty]
    private CornerOption? _selectedCorner;

    /// <summary>置顶档位。</summary>
    [ObservableProperty]
    private TopmostOption? _selectedTopmost;

    /// <summary>自动消失秒数；0 表示不自动消失，需要手动点掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationDurationText))]
    private int _notificationDuration;

    public IReadOnlyList<CornerOption> CornerOptions { get; } =
    [
        new(NotificationCorner.TopLeft, "左上角"),
        new(NotificationCorner.TopRight, "右上角"),
        new(NotificationCorner.BottomLeft, "左下角"),
        new(NotificationCorner.BottomRight, "右下角"),
    ];

    public IReadOnlyList<TopmostOption> TopmostOptions { get; } =
    [
        new(TopmostMode.None, "不置顶"),
        new(TopmostMode.Normal, "普通置顶"),
        new(TopmostMode.Forced, "UIA 置顶（强制）"),
    ];

    public string NotificationDurationText => NotificationDuration <= 0
        ? "不自动消失（点击关闭）"
        : $"{NotificationDuration} 秒后自动消失";

    /// <summary>置顶档位的说明，直接写在界面上，避免用户猜"UIA 置顶"到底做到了什么。</summary>
    public string TopmostModeHint => SelectedTopmost?.Value switch
    {
        TopmostMode.None => "弹窗可能被其他窗口盖住。",
        TopmostMode.Normal => "设置系统置顶。若别的程序也抢置顶，可能被压下去。",
        TopmostMode.Forced => "在系统置顶之上周期性重申，能抢过多数置顶窗口。"
                              + "但要盖住开始菜单、任务管理器这类更高窗口段的系统窗口，"
                              + "需要 UIAccess 令牌，而 Windows 要求该程序必须数字签名并安装在安全目录 —— "
                              + "当前为未签名的便携版本，因此做不到那一层。",
        _ => string.Empty,
    };

    partial void OnNotificationEnabledChanged(bool value)
    {
        _notificationSettings.Enabled = value;

        if (!value)
        {
            _notificationPresenter.Hide();
        }

        PersistNotificationSettings();
    }

    partial void OnSelectedCornerChanged(CornerOption? value)
    {
        if (value is null)
        {
            return;
        }

        _notificationSettings.Corner = value.Value;
        PersistNotificationSettings();
    }

    partial void OnSelectedTopmostChanged(TopmostOption? value)
    {
        if (value is null)
        {
            return;
        }

        _notificationSettings.Topmost = value.Value;
        OnPropertyChanged(nameof(TopmostModeHint));
        PersistNotificationSettings();
    }

    partial void OnNotificationDurationChanged(int value)
    {
        _notificationSettings.DurationSeconds = value;
        PersistNotificationSettings();
    }

    private void PersistNotificationSettings()
    {
        if (!_notificationSettings.Save())
        {
            AddLog("系统", "弹窗设置保存失败，本次修改在重启后会丢失。");
        }

        // 立即生效：窗口可能正开着，位置或档位应当当场变化
        _notificationPresenter.Refresh();
    }

    /// <summary>预览弹窗效果，省得为了看效果真的喊一次话。</summary>
    [RelayCommand]
    private void PreviewNotification()
    {
        if (!NotificationEnabled)
        {
            AddLog("系统", "弹窗已关闭，预览被跳过。");
            return;
        }

        _notificationPresenter.ShowPreview();
    }

    [RelayCommand]
    private void HideNotification() => _notificationPresenter.Hide();

    /// <summary>收到喊话时弹一条提示。</summary>
    private void NotifyOnScreen(string sourceName, string text, bool isVoice)
    {
        if (!NotificationEnabled)
        {
            return;
        }

        _notificationPresenter.Show(new NotificationContent(sourceName, text, isVoice));
    }

    public async ValueTask DisposeAsync()
    {
        _notificationPresenter.Dispose();

        if (_relay is not null)
        {
            await _relay.DisposeAsync().ConfigureAwait(false);
            _relay = null;
        }

        _http.Dispose();

        await _announcer.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);

        _speech.Dispose();
        _player.Dispose();
    }
}
