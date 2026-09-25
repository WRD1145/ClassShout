using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
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

    /// <summary>
    /// 语音已经放完，正在把它的识别结果当字幕摆在大字区。
    ///
    /// 单独一档而不是复用 SpeakingText：它没有声音在响，
    /// 界面上那句「正在朗读」会是在说谎，顶栏的状态也会一直停在"正在朗读"。
    /// </summary>
    ShowingTranscript,

    /// <summary>正在显示一条图片喊话（可能带一句说明文字）。</summary>
    ShowingImage,
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
    private readonly EdgeTtsSynthesizer _speech;
    private readonly ClassroomSpeechSettings _speechSettings;
    private readonly IAudioPlayer _player = ClassroomPlatform.CreatePlayer();
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

    /// <summary>
    /// 每开始呈现一条新内容就自增。
    ///
    /// 朗读与语音播放在这里都会排队、都是异步收尾，所以"读完了"这件事必须能认出
    /// 自己是不是最新那一条。否则前一条读完时会抢着把界面打回待机 ——
    /// 而队列里其实还有一条正在读，界面上就成了"假 Idle"：
    /// 大字区显示待机，喇叭却还在响。
    /// </summary>
    private long _presentationTicket;

    // —— 跨局域网中继 ——
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>把喊话投给本机 ClassIsland 联动插件用的投递器。</summary>
    private readonly ClassIslandNotifier _classIslandNotifier;
    private readonly ClassroomRelaySettings _relaySettings;
    private ClassroomRelayClient? _relay;

    // —— 屏幕边缘弹窗 ——
    private readonly ClassroomNotificationSettings _notificationSettings;
    private readonly NotificationPresenter _notificationPresenter;

    // —— 语音转文字 ——
    //
    // 它配在教室端而不是教师端：音频本来就落在这里，识别也在这里做，
    // 教室里那块屏幕才能把老师说的话摆成字幕。
    // 老师那台手机上再识别一遍毫无意义 —— 那边只有自己的麦克风，
    // 而教室里真正放出来的是什么、有没有听清，只有教室端知道。
    private readonly SttSettings _sttSettings;
    private readonly SttClient _stt;

    /// <summary>正在攒的这一路语音的原始 PCM。加锁是因为写入在接收线程、取走在 UI 线程。</summary>
    private readonly Lock _transcribeLock = new();
    private readonly MemoryStream _transcribeBuffer = new();

    /// <summary>这一路音频的格式。识别服务要靠它解释这段 PCM，用错格式识别出来就是乱码。</summary>
    private AudioFormat _transcribeFormat = AudioFormat.Default;

    /// <summary>这一路是否已经因为超长被截断过，用于只记一次日志。</summary>
    private bool _transcribeTruncated;

    /// <summary>
    /// 单次转写的时长上限。
    ///
    /// 不是为了省钱，是为了内存：识别接口收的是整个音频文件，
    /// 攒在内存里的 PCM 会一直涨。按音频自己的格式换算成字节数，
    /// 而不是写死"16 kHz 下多少字节" —— 采样率是网络对端说了算的。
    /// </summary>
    private const int TranscribeLimitMs = 5 * 60 * 1000;

    /// <summary>字幕停留多久后收回待机。够学生读完一句话，又不至于一直占着屏幕。</summary>
    private const int TranscriptHoldMs = 60 * 1000;

    /// <summary>字幕收起用的计时器。换一条喊话就把上一个取消掉。</summary>
    private CancellationTokenSource? _transcriptHide;

    /// <summary>当前这条文字喊话什么时候从屏幕上收走。常驻或没有停留下限时为 null。</summary>
    private CancellationTokenSource? _textClear;

    /// <summary>
    /// 当前这条喊话是不是"用弹窗显示"（而不是占满大字区）。
    ///
    /// 需要单独记一个标志，是因为朗读引擎会自己汇报"开始说了"，而那个事件
    /// 习惯性地把 Stage 设成 SpeakingText —— 那正好会把大字区点亮，
    /// 于是选了弹窗的人发现屏幕还是被一句话占满了。
    /// </summary>
    private bool _textHiddenInPopup;

    public ClassroomViewModel(int port = ShoutProtocol.DefaultTcpPort, int discoveryPort = ShoutProtocol.DefaultDiscoveryPort)
    {
        _server = new ClassroomServer(port);
        _announcer = new ClassroomAnnouncer(discoveryPort);
        // 系统语音始终建起来：它不依赖外网，是保底引擎，
        // 也是 Edge 连不上时的回落目标。
        _speechSettings = LocalSettings.LoadSpeech();
        _speech = new EdgeTtsSynthesizer(new EdgeTtsClient(_http), ClassroomPlatform.CreateSystemSpeech())
        {
            Engine = _speechSettings.Engine,
            EdgeVoice = _speechSettings.EdgeVoice,
        };

        // 本机没有系统朗读（Linux 上没装 spd-say / espeak）时必须说出来：
        // 否则老师在"系统语音"引擎下会看到界面一切正常、教室里却毫无声音，
        // 而唯一的线索是这个平台没有它。
        if (!ClassroomPlatform.HasSystemSpeech)
        {
            Logs.Insert(0, new LogEntry(DateTime.Now, "朗读",
                "本机没有可用的系统朗读（Linux 上需要 spd-say 或 espeak-ng）。建议改用 Edge 在线语音。"));
        }

        // 读出本机身份：UUID 首次启动生成一次后永久保留，是这台教室在服务器上的身份
        _relaySettings = LocalSettings.LoadClassroom();
        _relaySettings.EnsureUuid();

        // 注意方向：是把已保存的名字读进字段，不是把字段写进已存配置。
        //
        // 原来是 _relaySettings.ClassroomName = ClassroomName，方向反了：
        // 字段的初始值是"三年二班"，于是每次启动都把用户改过的教室名
        // 覆盖回这个默认值，然后立刻保存。改名的入口还没做，
        // 就算做了也一样会被下一次启动抹掉。
        if (!string.IsNullOrWhiteSpace(_relaySettings.ClassroomName))
        {
            _classroomName = _relaySettings.ClassroomName;
        }
        else
        {
            // 首次启动：把默认名落盘，让"这台教室叫什么"从第一刻起就有据可查
            _relaySettings.ClassroomName = _classroomName;
        }

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
                // 用弹窗展示的那条不该点亮大字区 —— 选了弹窗的人要的就是"别占屏"。
                if (!_textHiddenInPopup)
                {
                    Stage = ClassroomStage.SpeakingText;
                }
            }
            else if (Stage == ClassroomStage.SpeakingText && !_textHiddenInPopup)
            {
                Stage = ClassroomStage.Idle;
            }
        });

        foreach (var voice in _speech.GetVoices())
        {
            Voices.Add(voice);
        }

        _selectedEngine = EngineOptions.FirstOrDefault(o => o.Value == _speechSettings.Engine) ?? EngineOptions[0];
        _speech.Engine = _selectedEngine.Value;

        SelectedVoice = _speech.GetDefaultChineseVoice() ?? Voices.FirstOrDefault();

        LocalAddress = $"{NetworkUtility.GetLocalIPv4()}:{port}";

        // 弹窗配置与呈现器。呈现器必须在这里建（构造函数跑在 UI 线程上），
        // 它内部持有 Avalonia 窗口，换线程创建会拿到 null 的 Dispatcher。
        _notificationSettings = ClassroomNotificationSettings.Load();
        _notificationPresenter = new NotificationPresenter(_notificationSettings);
        _classIslandNotifier = new ClassIslandNotifier();

        // 语音转文字：密钥由管理员在这台教室电脑上填一次，只存本机。
        // 默认关闭 —— 它需要密钥，不能默认替学校打开。
        _sttSettings = LocalSettings.LoadStt();
        _stt = new SttClient(_http);

        NotificationEnabled = _notificationSettings.Enabled;
        NotificationDuration = _notificationSettings.DurationSeconds;
        SelectedCorner = CornerOptions.FirstOrDefault(o => o.Value == _notificationSettings.Corner) ?? CornerOptions[1];
        SelectedTopmost = TopmostOptions.FirstOrDefault(o => o.Value == _notificationSettings.Topmost) ?? TopmostOptions[2];
        SelectedChannel = ChannelOptions.FirstOrDefault(o => o.Value == _notificationSettings.Channel) ?? ChannelOptions[0];

        // 开机自启：状态以启动文件夹里的快捷方式为准，并顺手修正指向已失效的那一个 ——
        // 程序被移动或换目录之后，旧快捷方式指向不存在的路径，开机时会静默失败，
        // 而教室里没人会注意到"今天没自动打开"。
        _autoStart = StartupShortcut.IsEnabled;
        HealAutoStartShortcut();

        // 分项保护的勾选状态（哪几项要 PIN）
        LoadProtectedOptions();
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

    /// <summary>当前这条喊话的字号档位（见 <see cref="ShoutFontSizes"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTextFontSize))]
    private string _currentFontSize = ShoutFontSizes.Default;

    /// <summary>大字区正文的像素字号。档位到像素的映射由 Core 统一决定，两处界面共用。</summary>
    public double CurrentTextFontSize => ShoutFontSizes.ToPixels(CurrentFontSize, popup: false);

    /// <summary>
    /// 教室端给"发送方没指定"准备的那套兜底值。
    ///
    /// 它来自设置窗口里的三项默认值；教师端每次喊话都能覆盖，
    /// 所以这里只是兜底，不是限制。
    /// </summary>
    public ShoutDisplayDefaults DisplayDefaults => new(
        _notificationSettings.DefaultDisplay,
        _notificationSettings.DefaultFontSize,
        _notificationSettings.DefaultHoldMs);

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
        ClassroomStage.ShowingTranscript => "语音已转写成文字",
        _ => "等待教师端连接",
    };

    /// <summary>
    /// 待机时的引导语。
    ///
    /// 已经把服务器接上时就不要再教"同一局域网"了 —— 那样等于对着一个已经能跨网接收的教室
    /// 说它只能收同网段的喊话，值班老师会照着这句话去排查一个根本不存在的问题。
    /// </summary>
    public string IdleHint
    {
        get
        {
            if (Teachers.Count > 0)
            {
                return "教师端已连接，随时可以开始喊话";
            }

            return IsRelayConnected
                ? "已接上中继服务器，不在同一网络的老师也能喊到本教室"
                : "教师端与本机处于同一局域网时，打开应用即可自动搜索到本教室；"
                  + "跨网络使用需在设置里接上中继服务器";
        }
    }

    /// <summary>系统语音的可选列表（SAPI）。</summary>
    public ObservableCollection<string> Voices { get; } = [];

    /// <summary>在线引擎下可选的音色列表。</summary>
    public ObservableCollection<EdgeVoiceInfo> EdgeVoices { get; } = [];

    /// <summary>引擎下拉项。系统语音排在前面：它是保底选项。</summary>
    public IReadOnlyList<SpeechEngineOption> EngineOptions { get; } =
    [
        new(SpeechEngine.System, "系统语音（离线可用）"),
        new(SpeechEngine.Edge, "Edge 在线语音（更自然，需要外网）"),
    ];

    private SpeechEngineOption _selectedEngine = null!;

    public SpeechEngineOption SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (value is null || ReferenceEquals(_selectedEngine, value))
            {
                return;
            }

            _selectedEngine = value;
            _speech.Engine = value.Value;
            _speechSettings.Engine = value.Value;
            LocalSettings.SaveSpeech(_speechSettings);

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEdgeEngine));
            OnPropertyChanged(nameof(SpeechEngineStatus));

            AddLog("朗读", value.Value == SpeechEngine.Edge
                ? "已切换到 Edge 在线语音；连不上时会自动回落到系统语音。"
                : "已切换到系统语音。");

            if (value.Value == SpeechEngine.Edge && EdgeVoices.Count == 0)
            {
                _ = RefreshEdgeVoicesAsync();
            }
        }
    }

    /// <summary>当前是不是选了在线引擎。界面据此切换音色列表。</summary>
    public bool IsEdgeEngine => _speech.Engine == SpeechEngine.Edge;

    /// <summary>
    /// 现在实际在用哪个引擎。
    ///
    /// 取的是合成器记下的结果，而不是"用户选了什么" ——
    /// 用户选了 Edge 但外网不通时，界面必须显示"已回落到系统语音"，
    /// 否则老师只会觉得"今天的音质怎么变差了"，而没有任何线索。
    /// </summary>
    public string SpeechEngineStatus => _speech.LastEngineText;

    private EdgeVoiceInfo? _selectedEdgeVoice;

    public EdgeVoiceInfo? SelectedEdgeVoice
    {
        get => _selectedEdgeVoice;
        set
        {
            if (value is null || ReferenceEquals(_selectedEdgeVoice, value))
            {
                return;
            }

            _selectedEdgeVoice = value;
            _speech.EdgeVoice = value.ShortName;
            _speechSettings.EdgeVoice = value.ShortName;
            LocalSettings.SaveSpeech(_speechSettings);

            OnPropertyChanged();
        }
    }

    /// <summary>拉取在线音色列表。拉不到就说明这台机器连不上在线语音。</summary>
    [RelayCommand]
    private async Task RefreshEdgeVoicesAsync()
    {
        try
        {
            var voices = await _speech.GetEdgeVoicesAsync().ConfigureAwait(true);

            if (voices.Count == 0)
            {
                AddLog("朗读", "拿不到在线音色列表 —— 这台机器可能连不上外网，将继续使用系统语音。");
                OnPropertyChanged(nameof(SpeechEngineStatus));
                return;
            }

            // 中文音色排前面：这是中文课堂，把英文音色列在最前面没有意义
            var ordered = voices
                .OrderByDescending(v => v.Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => v.Locale, StringComparer.Ordinal)
                .ToList();

            EdgeVoices.Clear();
            foreach (var voice in ordered)
            {
                EdgeVoices.Add(voice);
            }

            SelectedEdgeVoice = EdgeVoices.FirstOrDefault(v => v.ShortName == _speechSettings.EdgeVoice)
                                ?? EdgeVoices.FirstOrDefault();

            AddLog("朗读", $"已载入 {EdgeVoices.Count} 个在线音色，当前：{SelectedEdgeVoice?.ShortName ?? "默认"}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AddLog("朗读", $"载入在线音色失败：{ex.Message}");
        }
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public ObservableCollection<TeacherInfo> Teachers { get; } = [];

    public bool IsIdle => Stage == ClassroomStage.Idle;

    public bool IsSpeakingText => Stage == ClassroomStage.SpeakingText;

    public bool IsPlayingAudio => Stage == ClassroomStage.PlayingAudio;

    /// <summary>大字区现在显示的是不是一张图片。</summary>
    public bool IsShowingImage => Stage == ClassroomStage.ShowingImage;

    /// <summary>大字区现在有没有字要显示。文字喊话与语音转写的字幕共用同一块区域。</summary>
    public bool ShowTextStage => Stage is ClassroomStage.SpeakingText or ClassroomStage.ShowingTranscript;

    /// <summary>大字区顶上那枚 chip 的文字。同一条通道，两种来源要说清是哪一种。</summary>
    public string StageChipText => Stage == ClassroomStage.ShowingTranscript ? "语音已转写" : "正在朗读";

    public bool HasTeachers => Teachers.Count > 0;

    /// <summary>
    /// 顶栏那个连接状态 chip 的文字。
    ///
    /// 它原本只数局域网 TCP 会话，于是**只要走服务器链路就永远显示"未连接"** ——
    /// 哪怕教室端正通过中继收着喊话。这属于界面在说谎，比不显示更糟。
    /// 现在把两条链路的已知状态都说出来，并且不假装知道不知道的事：
    /// 局域网会话是教室端亲自握过手的，可以数；而"有没有老师绑在这台服务器上"
    /// 服务器目前不通知教室端，所以这里只说"已连服务器"，不编造人数。
    /// </summary>
    public string TeacherCountText
    {
        get
        {
            var lan = Teachers.Count;
            var parts = new List<string>(2);

            if (lan > 0)
            {
                parts.Add($"{lan} 个教师端在线");
            }

            if (IsRelayConnected)
            {
                parts.Add("已连服务器");
            }

            return parts.Count == 0 ? "未连接" : string.Join(" · ", parts);
        }
    }

    /// <summary>chip 的悬停说明，把"这两个词各代表什么"讲清楚。</summary>
    public string TeacherCountHint =>
        "局域网直连的教师端数量，以及本教室是否已接上中继服务器。"
        + "「已连服务器」表示跨局域网那条路已经通了，可以接收不在同一网络的老师发来的喊话。";

    /// <summary>仅在播放语音时才刷新波形，避免待机时白白重绘。</summary>
    public bool ShouldMeterAudio => Stage == ClassroomStage.PlayingAudio;

    partial void OnStageChanged(ClassroomStage value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsSpeakingText));
        OnPropertyChanged(nameof(IsPlayingAudio));
        OnPropertyChanged(nameof(IsShowingImage));
        OnPropertyChanged(nameof(ShowTextStage));
        OnPropertyChanged(nameof(StageChipText));
        OnPropertyChanged(nameof(ShouldMeterAudio));
        OnPropertyChanged(nameof(StageHeadline));

        StatusText = value switch
        {
            ClassroomStage.SpeakingText => "正在朗读文字喊话",
            ClassroomStage.PlayingAudio => "正在播放语音喊话",
            ClassroomStage.ShowingTranscript => "正在显示语音转写的文字",
            ClassroomStage.ShowingImage => "正在显示图片喊话",
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

    // ======================== 设置口令（可选的本机 PIN） ========================

    /// <summary>用户刚把开关拨到"开"，但还没输入 PIN。</summary>
    private bool _settingsLockPending;

    /// <summary>
    /// 是否要求进入设置前输入 PIN。
    ///
    /// 打开开关时不能直接启用：没有 PIN 的锁等于没锁。
    /// 这里只记下意愿，等用户在下面输入并保存 PIN 之后才真正生效 ——
    /// 所以读的时候要把"待生效"也算上，否则开关会自己弹回去。
    /// </summary>
    public bool SettingsLockEnabled
    {
        get => SettingsLock.IsEnabled || _settingsLockPending;
        set
        {
            if (value == SettingsLockEnabled)
            {
                return;
            }

            if (value)
            {
                _settingsLockPending = true;
                SettingsLockError = $"请输入一个 {SettingsLock.MinPinLength}~32 位数字 PIN，然后点「保存 PIN」。";
            }
            else
            {
                SettingsLock.Disable();
                _settingsLockPending = false;
                NewPin = string.Empty;
                SettingsLockError = string.Empty;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsLockHint));
        }
    }

    [ObservableProperty]
    private string _newPin = string.Empty;

    [ObservableProperty]
    private string _settingsLockError = string.Empty;

    /// <summary>设置窗口标题下那行说明：这道锁现在是开着还是关着。</summary>
    public string SettingsLockHint => SettingsLock.IsEnabled
        ? "已开启设置口令 · 关闭本窗口后重新上锁"
        : "设置口令未开启";

    [RelayCommand]
    private void SavePin()
    {
        var (ok, error) = SettingsLock.SetPin(NewPin);

        if (!ok)
        {
            SettingsLockError = error ?? "PIN 未生效。";
            return;
        }

        _settingsLockPending = false;
        NewPin = string.Empty;
        SettingsLockError = "PIN 已保存，设置口令已开启。下次进入设置时需要输入它。";

        OnPropertyChanged(nameof(SettingsLockEnabled));
        OnPropertyChanged(nameof(SettingsLockHint));
    }

    // ======================== 分项保护 ========================
    //
    // 和"进入设置要 PIN"是两种用法，可以同时开：前者管"别让人翻设置"，
    // 后者管"别的都能改，但密钥、教室身份、退出这些不许随手动"。
    //
    // 已解锁的项只记在内存里，关掉设置窗口就清空 —— 与整体锁同一个道理：
    // 落盘就等于永久解锁，那道锁也就没意义了。

    private readonly HashSet<string> _unlockedAreas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这一项现在是不是锁着（受保护且还没解锁）。</summary>
    public bool IsAreaLocked(string area)
        => SettingsLock.IsAreaProtected(area) && !_unlockedAreas.Contains(area);

    public bool IsSpeechLocked => IsAreaLocked(ProtectedAreas.Speech);

    public bool IsSttLocked => IsAreaLocked(ProtectedAreas.Stt);

    public bool IsRelayLocked => IsAreaLocked(ProtectedAreas.Relay);

    public bool IsDisplayLocked => IsAreaLocked(ProtectedAreas.Display);

    public bool IsClassroomInfoLocked => IsAreaLocked(ProtectedAreas.Classroom);

    public bool IsBackgroundLocked => IsAreaLocked(ProtectedAreas.Background);

    /// <summary>退出程序需不需要 PIN。托盘菜单与关闭时的退出路径都要看它。</summary>
    public bool IsExitProtected => SettingsLock.IsExitProtected;

    /// <summary>受保护项的勾选列表。</summary>
    public ObservableCollection<ProtectedAreaOption> ProtectedOptions { get; } = [];

    /// <summary>"退出程序需要 PIN"的开关。</summary>
    public bool ProtectExit
    {
        get => SettingsLock.Load().ProtectExit;
        set
        {
            var settings = SettingsLock.Load();
            if (settings.ProtectExit == value)
            {
                return;
            }

            settings.ProtectExit = value;
            LocalSettings.Save("settings-lock.json", settings);

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExitProtected));
        }
    }

    private void LoadProtectedOptions()
    {
        ProtectedOptions.Clear();

        var settings = SettingsLock.Load();

        foreach (var area in ProtectedAreas.All)
        {
            var isProtected = settings.ProtectedAreas.Any(
                item => string.Equals(item, area, StringComparison.OrdinalIgnoreCase));

            var option = new ProtectedAreaOption(area, isProtected);
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProtectedAreaOption.IsProtected))
                {
                    SaveProtectedAreas();
                }
            };

            ProtectedOptions.Add(option);
        }
    }

    private void SaveProtectedAreas()
    {
        var settings = SettingsLock.Load();
        settings.ProtectedAreas = ProtectedOptions.Where(o => o.IsProtected).Select(o => o.Area).ToList();
        LocalSettings.Save("settings-lock.json", settings);

        NotifyAreaLocksChanged();
    }

    /// <summary>PIN 验过之后把受保护项全部解锁（整体锁进入设置时用）。</summary>
    public void UnlockAllAreas()
    {
        foreach (var area in ProtectedAreas.All)
        {
            _unlockedAreas.Add(area);
        }

        NotifyAreaLocksChanged();
    }

    /// <summary>重新锁上所有分项。关掉设置窗口时调用。</summary>
    public void LockAreasAgain()
    {
        _unlockedAreas.Clear();
        NotifyAreaLocksChanged();
    }

    /// <summary>受保护项里有没有任何一项正锁着（界面据此决定要不要显示解锁入口）。</summary>
    public bool HasLockedAreas =>
        IsSpeechLocked || IsSttLocked || IsRelayLocked
        || IsDisplayLocked || IsClassroomInfoLocked || IsBackgroundLocked;

    [ObservableProperty]
    private string _unlockPin = string.Empty;

    [ObservableProperty]
    private string _unlockError = string.Empty;

    /// <summary>
    /// 在设置里解锁受保护项。
    ///
    /// 刻意做成"输入框 + 按钮"而不是弹一个对话框：解锁只是一次性动作，
    /// 而设置窗口里本来就有输入 PIN 的地方（设口令那一段），多一层模态窗反而绕。
    /// </summary>
    [RelayCommand]
    private void UnlockProtected()
    {
        if (!SettingsLock.IsEnabled)
        {
            UnlockError = "还没有设置 PIN。请先在「设置口令」里设一个。";
            return;
        }

        if (!SettingsLock.Verify(UnlockPin))
        {
            UnlockError = "PIN 不正确。";
            return;
        }

        UnlockPin = string.Empty;
        UnlockError = string.Empty;
        UnlockAllAreas();

        AddLog("系统", "已解锁受保护的设置项（关掉本窗口后会重新上锁）。");
    }

    private void NotifyAreaLocksChanged()
    {
        OnPropertyChanged(nameof(IsSpeechLocked));
        OnPropertyChanged(nameof(IsSttLocked));
        OnPropertyChanged(nameof(IsRelayLocked));
        OnPropertyChanged(nameof(IsDisplayLocked));
        OnPropertyChanged(nameof(IsClassroomInfoLocked));
        OnPropertyChanged(nameof(IsBackgroundLocked));
        OnPropertyChanged(nameof(IsExitProtected));
        OnPropertyChanged(nameof(HasLockedAreas));
    }

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
        session.ImageStarted += (_, message) => OnImageStarted(session, message);
        session.ImageChunkReceived += (_, payload) => OnImageChunk(LanOwnerKey(session), payload);
        session.ImageEnded += (_, message) => OnImageEnded(session, message);
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
        // 计数不在这里做。原来是在这里先加一，然后才在 UI 线程上判断是否静音 ——
        // 于是静音期间教师列表会显示"喊过 3 条"，而那 3 条一条都没播出去，
        // 老师看到的是"我喊了、教室有反应"，实际教室里什么都没有。
        Post(() =>
        {
            PresentTextShout(session.ClientName, message, session.Id);
            RefreshTeachers();
        });
    }

    /// <summary>
    /// 把一条文字喊话呈现出来（朗读 + 更新界面）。
    ///
    /// 局域网直连与公网中继两条路径共用这里 —— 它们只是"消息怎么来"不同，
    /// "收到之后做什么"必须完全一致，否则两条链路的行为会慢慢分叉。
    /// </summary>
    private void PresentTextShout(string sourceName, TextShoutMessage message, string? countKey = null)
    {
        AddLog("文字", $"「{sourceName}」说：{message.Text}");

        if (IsMuted)
        {
            AddLog("文字", "已静音，本条不朗读");
            return;
        }

        if (countKey is not null)
        {
            lock (_sessions)
            {
                _textCounts[countKey] = _textCounts.GetValueOrDefault(countKey) + 1;
            }
        }

        // 发送方可以指定怎么显示、多大字、停多久、要不要读；没指定的用教室端的默认值。
        var plan = ShoutDisplayPlan.Resolve(
            message.Display,
            message.FontSize,
            message.HoldMs,
            message.Speak,
            DisplayDefaults);

        var ticket = ++_presentationTicket;

        // 一次只出一路声音：文字喊话要把正在播的语音停掉。
        // 原来这里不停播放器，而语音那条路径会停朗读 —— 方向是单边的，
        // 于是语音播放期间来的文字喊话会和音响里的声音叠在一起响，
        // 教室里听着就是两个人在同时说话。
        _speech.Stop();
        StopAudioPlayback();

        CurrentSpeaker = sourceName;
        CurrentText = message.Text;
        CurrentFontSize = plan.FontSize;

        // 弹窗模式下大字区不参与：屏幕上只在角落出现一张卡片。
        // 这里仍然把说话人和文字填上，是为了教师端与日志能看到"最后一条是什么"，
        // 而 Stage 留在 Idle —— 否则整块屏会被一句话占满，那正是选弹窗的人不想要的。
        _textHiddenInPopup = plan.IsPopup;
        Stage = plan.IsPopup ? ClassroomStage.Idle : ClassroomStage.SpeakingText;

        NotifyOnScreen(sourceName, message.Text, ShoutNoticeKind.Text, plan);

        if (plan.Speak)
        {
            _ = SpeakAsync(message, ticket, plan);
        }
        else
        {
            AddLog("文字", "本条不朗读，只显示。");
        }

        ScheduleTextClear(ticket, plan);
    }

    /// <summary>
    /// 到点把这条内容从屏幕上收走。
    ///
    /// 三种情况不动手：
    ///   · 常驻（停留时长为 0）—— 那正是"别收走"的意思；
    ///   · 已经不是最新那条（期间又来了新的喊话）；
    ///   · 还在朗读 —— 朗读没完就清屏，教室里会出现"话还在说、字已经没了"。
    ///     这时不清，交给朗读收尾那一步（见 <see cref="SpeakAsync"/>）。
    /// </summary>
    private void ScheduleTextClear(long ticket, ShoutDisplayPlan plan)
    {
        _textClear?.Cancel();
        _textClear?.Dispose();
        _textClear = null;

        // 每开一条新的，先把上一条留下的"停留已经到点"标记清掉
        _holdElapsedDuringSpeech = false;

        if (plan.IsForever || plan.Hold is not { } hold)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _textClear = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(hold, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await PostAsync(() =>
            {
                if (cts.IsCancellationRequested || ticket != _presentationTicket)
                {
                    return;
                }

                // 还在朗读就先不动手：话还在说、字已经没了，教室里看着像出了故障。
                // 但要把"停留已经到点"记下来，等朗读收尾时立刻清屏 —— 否则这条会一直留着。
                if (IsSpeaking)
                {
                    _holdElapsedDuringSpeech = true;
                    return;
                }

                if (Stage is ClassroomStage.SpeakingText or ClassroomStage.ShowingImage)
                {
                    var wasImage = Stage == ClassroomStage.ShowingImage;

                    Stage = ClassroomStage.Idle;
                    CurrentText = string.Empty;
                    CurrentSpeaker = string.Empty;

                    if (wasImage)
                    {
                        // 图片要显式释放：Bitmap 背后是 Skia 的位图，
                        // 换下一张时不释放会一直占着内存 —— 教室端是常驻几周不重启的。
                        CurrentImage?.Dispose();
                        CurrentImage = null;
                    }
                }
            });
        });
    }

    /// <summary>停留时长已经在朗读期间到点了 —— 朗读一结束就该清屏，不再等。</summary>
    private bool _holdElapsedDuringSpeech;

    /// <summary>停掉播放器并清掉语音归属。之所以连归属一起清，是因为清完才代表"这一路结束了"。</summary>
    private void StopAudioPlayback()
    {
        lock (_audioStateLock)
        {
            _audioOwner = null;
            _player.Stop();
        }
    }

    // ======================== 图片喊话 ========================

    /// <summary>正在接收的那张图。</summary>
    private readonly Lock _imageLock = new();
    private MemoryStream? _imageBuffer;
    private string? _imageId;
    private string? _imageOwner;

    /// <summary>这张图的声明部分（说明文字与展示参数）。它随 imageStart 到达，收尾时要用。</summary>
    private ImageStartMessage? _imageStart;

    /// <summary>大字区正在显示的那张图。为空表示当前不是图片喊话。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImageCaption))]
    private Bitmap? _currentImage;

    /// <summary>这条图片喊话带没带说明文字。</summary>
    public bool HasImageCaption => !string.IsNullOrWhiteSpace(CurrentText) && Stage == ClassroomStage.ShowingImage;

    /// <summary>
    /// 图片喊话开始（带说明文字与总字节数）。
    ///
    /// 中继链路上这条消息里带的是 <see cref="RelayEnvelope"/>，所以这里的重载
    /// 由中继那边调用；两条链路最终都落到同一个 <c>PresentImage</c>。
    /// </summary>
    private void OnImageStarted(TeacherSession session, ImageStartMessage message)
    {
        // 声明里的大小直接决定我们分配多少内存，所以先卡上限、再照它开缓冲。
        if (message.TotalBytes is <= 0 or > ShoutProtocol.MaxImageBytes)
        {
            Post(() => AddLog("图片",
                $"「{session.ClientName}」发来的图片大小不合法（{message.TotalBytes} 字节），已忽略。"));
            return;
        }

        lock (_imageLock)
        {
            _imageBuffer?.Dispose();
            _imageBuffer = new MemoryStream(message.TotalBytes);
            _imageId = message.Id;
            _imageOwner = LanOwnerKey(session);
            _imageStart = message;
        }

        Post(() => AddLog("图片", $"「{session.ClientName}」发来一张图片（{message.TotalBytes / 1024} KB）"));
    }

    private void OnImageChunk(string ownerKey, ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        lock (_imageLock)
        {
            // 只收属于当前这张图的分片：上一个教师端还在路上的尾巴混进来，
            // 拼出来的是一张花屏，而且不会有任何报错。
            if (_imageBuffer is null || !string.Equals(_imageOwner, ownerKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_imageBuffer.Length + payload.Length > ShoutProtocol.MaxImageBytes)
            {
                return;
            }

            _imageBuffer.Write(payload.Span);
        }
    }

    private void OnImageEnded(TeacherSession session, ImageEndMessage message)
    {
        byte[]? bytes = null;
        ImageStartMessage? start;

        lock (_imageLock)
        {
            start = _imageStart;

            if (_imageBuffer is not null &&
                string.Equals(_imageId, message.Id, StringComparison.Ordinal) &&
                string.Equals(_imageOwner, LanOwnerKey(session), StringComparison.Ordinal))
            {
                bytes = _imageBuffer.ToArray();
                _imageBuffer.Dispose();
                _imageBuffer = null;
                _imageId = null;
                _imageOwner = null;
                _imageStart = null;
            }
        }

        if (bytes is null || bytes.Length == 0 || start is null)
        {
            Post(() => AddLog("图片", "图片没有收全，已忽略。"));
            return;
        }

        // 说明文字与展示参数来自 imageStart —— 它们不属于"结束"这条消息
        Post(() => PresentImage(
            session.ClientName,
            bytes,
            start.Display,
            start.FontSize,
            start.HoldMs,
            start.Text,
            start.Speak));
    }

    /// <summary>
    /// 把收到的图片摆到教室里。
    ///
    /// 解码放在 UI 线程上做：<see cref="Bitmap"/> 背后是 Skia 的位图对象，
    /// 而这个对象马上就要交给界面渲染 —— 在别的线程建、再跨线程交给控件，
    /// 出问题的表现是偶发花屏或崩溃，而那种问题在教室里根本没法排查。
    /// </summary>
    private void PresentImage(
        string sourceName,
        byte[] bytes,
        string? display,
        string? fontSize,
        int holdMs,
        string? caption,
        bool speak)
    {
        var plan = ShoutDisplayPlan.Resolve(display, fontSize, holdMs, speak, DisplayDefaults);

        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            // 解码失败只记日志：教室里那台机器上没人能处理"这张图坏了"这件事，
            // 而弹一个错误框只会盖住正在讲的内容。
            AddLog("图片", $"图片解码失败，已忽略：{ex.Message}");
            return;
        }

        var ticket = ++_presentationTicket;

        // 一次只出一路内容：图片来了就把正在读的、正在放的都停下来
        _speech.Stop();
        StopAudioPlayback();

        CurrentImage?.Dispose();
        CurrentImage = bitmap;

        CurrentSpeaker = sourceName;
        CurrentText = caption ?? string.Empty;
        CurrentFontSize = plan.FontSize;

        _textHiddenInPopup = plan.IsPopup;
        Stage = plan.IsPopup ? ClassroomStage.Idle : ClassroomStage.ShowingImage;

        NotifyOnScreen(
            sourceName,
            string.IsNullOrWhiteSpace(caption) ? "（图片）" : caption,
            ShoutNoticeKind.Image,
            plan,
            bitmap);

        // 朗读针对的是那句说明文字；没有文字就没什么可读的
        if (plan.Speak && !string.IsNullOrWhiteSpace(caption))
        {
            _ = SpeakAsync(new TextShoutMessage { Text = caption, Rate = Rate, Volume = Volume }, ticket, plan);
        }

        ScheduleTextClear(ticket, plan);
    }

    private async Task SpeakAsync(TextShoutMessage message, long ticket, ShoutDisplayPlan plan)
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
                // 只有"最新那一条"读完才有资格把界面打回待机。
                // 队列里前一条读完时 Stage 同样是 SpeakingText，
                // 只看 Stage 的话它会抢着回落，而后一条其实还在读。
                if (ticket != _presentationTicket)
                {
                    return;
                }

                // 常驻：读完了也不收，一直留到下一句喊话。
                if (plan.IsForever && !plan.IsPopup)
                {
                    OnPropertyChanged(nameof(SpeechEngineStatus));
                    return;
                }

                // 停留时间还没到：让那个计时器去收，现在收了就等于停留时长没设。
                if (!plan.IsPopup && plan.Hold is not null && !_holdElapsedDuringSpeech)
                {
                    OnPropertyChanged(nameof(SpeechEngineStatus));
                    return;
                }

                Stage = ClassroomStage.Idle;
                CurrentText = string.Empty;
                CurrentSpeaker = string.Empty;
                AudioLevel = 0;
                _textHiddenInPopup = false;

                // 这一条读完才知道实际用的是哪个引擎（可能已经回落到系统语音），
                // 所以状态显示要在这里刷新，而不是在开始朗读时。
                OnPropertyChanged(nameof(SpeechEngineStatus));
            });
        }
        catch (Exception ex)
        {
            Post(() => AddLog("系统", $"朗读失败：{ex.Message}"));
        }
    }

    private void OnAudioStarted(TeacherSession session, AudioStartMessage message)
    {
        // 计数不在这里做：必须在"确认要播"之后（见 PresentAudioStart）。
        // 静音期间先加一，教师列表就会显示"喊过 N 次"，而教室里一次都没响过。
        Post(() =>
        {
            PresentAudioStart(
                LanOwnerKey(session),
                session.ClientName,
                new AudioFormat(message.SampleRate, message.Channels, message.BitsPerSample),
                session.Id);

            RefreshTeachers();
        });
    }

    /// <summary>局域网来源的归属键。用会话 Id 而不是姓名：同名老师会互相串台。</summary>
    private static string LanOwnerKey(TeacherSession session) => "lan:" + session.Id;

    /// <summary>中继来源的归属键。中继侧拿不到会话 Id，只能用服务器确认过的姓名。</summary>
    private static string RelayOwnerKey(RelayEnvelope envelope) => "relay:" + (envelope.From ?? "教师端");

    /// <summary>开始播放一路语音。局域网与中继两条路径共用。</summary>
    private void PresentAudioStart(string ownerKey, string sourceName, AudioFormat format, string? countKey = null)
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

        if (countKey is not null)
        {
            // 用 _sessions 这一把锁：_sessions / _textCounts / _audioCounts 总是一起被访问，
            // 各用各的锁等于没有同步 —— 刷新教师列表那一侧读的就是未同步的数据。
            lock (_sessions)
            {
                _audioCounts[countKey] = _audioCounts.GetValueOrDefault(countKey) + 1;
            }
        }

        // 一次只出一路声音：语音优先，打断正在进行的朗读
        _speech.Stop();
        _player.Stop();

        lock (_audioStateLock)
        {
            _audioOwner = ownerKey;
            _currentAudioFormat = format;
        }

        // 这一路要转写的音频从头开始攒：上一路没转成功的残留绝不能混进这一句，
        // 否则识别出来的是"上一位老师的话尾 + 这一位的开头"。
        lock (_transcribeLock)
        {
            _transcribeBuffer.SetLength(0);
            _transcribeFormat = format;
            _transcribeTruncated = false;
        }

        // 也算一次"开始呈现"：上一条朗读迟到的收尾不该把语音界面打回待机
        _presentationTicket++;

        // 启动失败的类型是平台专有的（Windows 上是 NAudio 的 MmException，
        // Linux 上是进程启动失败），所以判断收在平台层里，
        // VM 只问"成没成、为什么"——免得这里长出一串条件编译。
        if (!ClassroomPlatform.TryStartPlayer(_player, format, out var startError))
        {
            lock (_audioStateLock)
            {
                _audioOwner = null;
            }

            AddLog("语音", $"无法开始播放：{startError}");
            return;
        }

        CurrentSpeaker = sourceName;
        CurrentText = "语音喊话中…";
        AudioSeconds = 0;
        AudioLevel = 0;
        Stage = ClassroomStage.PlayingAudio;
        AddLog("语音", $"「{sourceName}」开始语音喊话（{format}）");

        NotifyOnScreen(sourceName, "（语音喊话，正在教室播放）", ShoutNoticeKind.Voice);
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

        // 顺手攒一份送去识别。
        //
        // 只在 _audioOwner 确实属于这一路时才会走到这里 —— 上面那道归属检查
        // 顺带保证了"静音期间收到的分片不进缓冲"：静音时 PresentAudioStart
        // 根本没设过 owner，分片全在检查处被丢掉了。
        lock (_transcribeLock)
        {
            if (_transcribeBuffer.Length + payload.Length <= _transcribeFormat.BytesForDuration(TranscribeLimitMs))
            {
                _transcribeBuffer.Write(payload.Span);
            }
            else
            {
                _transcribeTruncated = true;
            }
        }

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

            // 收尾之后才把这一路攒下的音频交出去识别。
            //
            // 顺序很重要：转写是网络请求，可能几秒才有结果，
            // 所以它绝不参与"播放结束了没有"这件事，也不会拖慢下一条喊话 ——
            // StartTranscription 只负责把音频取走就返回。
            StartTranscription(CurrentSpeaker, _presentationTicket);
        });
    }

    /// <summary>
    /// 把刚刚播完的这一路语音送去识别。
    ///
    /// 刻意 fire-and-forget：喊话本身已经放完了，字幕只是**附加**的一行。
    /// 网速慢、密钥填错、识别服务挂了，都不该让教室端的界面卡住或者弹一个
    /// 没人会处理的错误框 —— 那些只写进日志，教室里没人需要为一个附加功能停下来。
    /// </summary>
    private void StartTranscription(string sourceName, long ticket)
    {
        // 没配好就一点开销都不产生：连缓冲都不该攒。
        // 缓冲本身是无条件攒的（配置随时可能在设置里被打开），
        // 但只有走到这里才做网络请求。
        if (!_sttSettings.IsUsable)
        {
            return;
        }

        byte[] pcm;
        AudioFormat format;
        bool truncated;

        lock (_transcribeLock)
        {
            pcm = _transcribeBuffer.ToArray();
            _transcribeBuffer.SetLength(0);
            format = _transcribeFormat;
            truncated = _transcribeTruncated;
            _transcribeTruncated = false;
        }

        // 太短的一段识别不出有意义的内容，却要花一次请求；
        // 半秒以下基本就是老师误触了按键。
        if (pcm.Length < format.BytesForDuration(500))
        {
            return;
        }

        if (truncated)
        {
            AddLog("语音", "这段语音较长，只转写前 5 分钟。");
        }

        _ = TranscribeAsync(pcm, format, sourceName, ticket);
    }

    /// <summary>真正去调识别服务，并把文字摆到教室的大字区。</summary>
    private async Task TranscribeAsync(byte[] pcm, AudioFormat format, string sourceName, long ticket)
    {
        // ConfigureAwait(true)：下面要改的都是界面状态，必须在 UI 线程上。
        // 这一步的起点就是 UI 线程（StartTranscription 由 Post 的回调调用）。
        var result = await _stt.TranscribeAsync(pcm, format, _sttSettings).ConfigureAwait(true);

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Text))
        {
            AddLog("语音", $"语音转文字失败：{result.Error}");
            return;
        }

        AddLog("语音", $"「{sourceName}」的语音转文字：{result.Text}");

        // 识别要几秒才有结果，这期间完全可能已经来了新的一条喊话。
        // 那就只留下日志，不再去动大字区 ——
        // 否则新老师正在说的话会被上一位的迟到字幕顶掉，教室里看到的是错的人说的错的话。
        if (ticket != _presentationTicket || IsMuted)
        {
            return;
        }

        CurrentSpeaker = sourceName;
        CurrentText = result.Text;
        Stage = ClassroomStage.ShowingTranscript;

        // 顺带把识别结果也投给 ClassIsland：那边原来只显示一句"语音消息"，
        // 现在能看见老师到底说了什么 —— 教室里没听清的人多一处能看的地方。
        NotifyClassIsland(sourceName, result.Text, ShoutNoticeKind.VoiceTranscript);

        ScheduleTranscriptHide(ticket);
    }

    /// <summary>
    /// 字幕摆一会儿就收回待机。
    ///
    /// 不做这一步的话，Stage 会永远停在"有字"上：顶栏一直说"正在显示…"，
    /// 教室里那块屏幕也再回不到待机画面 —— 而喊话早就结束了。
    /// 收回只认两件事：还是同一条喊话（ticket 没变），且现在显示的确实是字幕
    /// （期间来了文字喊话或新语音就不能动它）。
    /// </summary>
    private void ScheduleTranscriptHide(long ticket)
    {
        _transcriptHide?.Cancel();
        _transcriptHide?.Dispose();

        var cts = new CancellationTokenSource();
        _transcriptHide = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TranscriptHoldMs, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await PostAsync(() =>
            {
                if (cts.IsCancellationRequested || ticket != _presentationTicket)
                {
                    return;
                }

                if (Stage != ClassroomStage.ShowingTranscript)
                {
                    return;
                }

                Stage = ClassroomStage.Idle;
                CurrentText = string.Empty;
                CurrentSpeaker = string.Empty;
            });
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

        lock (_transcribeLock)
        {
            _transcribeBuffer.SetLength(0);
        }

        // 这次手动停止也算一次"开始呈现"：正在路上的一次识别回来时，
        // ticket 已经变了，它就不会再把刚被清掉的字幕重新贴回屏幕上。
        _presentationTicket++;
        _transcriptHide?.Cancel();

        Stage = ClassroomStage.Idle;
        AudioLevel = 0;
        CurrentText = string.Empty;
        CurrentSpeaker = string.Empty;

        // 图片也要清掉并释放，否则"停止"之后画面还留着上一张
        CurrentImage?.Dispose();
        CurrentImage = null;

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
        var ticket = ++_presentationTicket;

        // 试听也要参与"最新那一条"的判定，否则它读完会把正在读的喊话界面打回待机
        _speech.Stop();
        StopAudioPlayback();

        CurrentSpeaker = "本机测试";
        CurrentText = text;

        // 试听走的是教室端自己的默认展示参数：它是本机功能，没有"发送方"来指定。
        var plan = ShoutDisplayPlan.Resolve(null, null, ShoutHoldDurations.Unspecified, speak: true, DisplayDefaults);

        CurrentFontSize = plan.FontSize;
        _textHiddenInPopup = plan.IsPopup;
        Stage = plan.IsPopup ? ClassroomStage.Idle : ClassroomStage.SpeakingText;

        _ = SpeakAsync(new TextShoutMessage { Text = text, Rate = Rate, Volume = Volume }, ticket, plan);
    }

    /// <summary>组装当前运行态，既用于握手回包，也用于主动推送。</summary>
    private StatusMessage BuildStatus() => new()
    {
        State = Stage.ToString().ToLowerInvariant(),
        ClassroomName = ClassroomName,
        Muted = IsMuted,
        Volume = Volume,

        // 顺带报一下本版本会哪些新能力（图片、展示参数）。
        // 教师端拿它决定界面上的选项能不能用 —— 让人选好了却发不到对面，比一开始就禁掉糟得多。
        Capabilities = ClassroomCapabilities.Current,
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

    // 顶栏 chip 也要跟着变 —— 否则连上服务器之后，那个 chip 仍然显示"未连接"
    [NotifyPropertyChangedFor(nameof(TeacherCountText))]

    // 待机引导语同理：接上服务器之后它不该还在教"同一局域网"
    [NotifyPropertyChangedFor(nameof(IdleHint))]
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
    /// 本教室在服务器上的身份（UUID 与口令）只显示、可复制，**不提供重新生成**。
    ///
    /// 原来这里有一个"重新生成 UUID"的按钮。它看起来只是"重置一下"，
    /// 实际含义却是"这台电脑从此变成另一间教室"：服务器上的旧记录作废，
    /// 已经绑定的教师端全部失效，口令要重新抄一遍发给每一位任课老师。
    /// 教室里那台机器是给值班老师和学生共用的，一个误触就能让整间教室从所有老师的
    /// 列表里消失，而恢复它需要把口令一家一家重发 —— 这个代价和"重置"这个词
    /// 给人的感觉完全不成比例。真要重来（比如这台机器换教室了），
    /// 管理员在服务器的控制台上做，那里看得见影响范围，也能一次性通知到人。
    /// </summary>

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

                        // 展示参数原样带过去。中继这条路上它们和服务器的其他字段一样
                        // 只是转发，所以这里缺什么就用什么默认值。
                        Display = envelope.Display,
                        FontSize = envelope.FontSize,
                        HoldMs = envelope.HoldMs,
                        Speak = envelope.Speak,
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

                case RelayKinds.ImageStart:
                    OnRelayImageStart(envelope);
                    break;

                case RelayKinds.Image:
                    if (!string.IsNullOrEmpty(envelope.ImageBase64))
                    {
                        OnImageChunk(RelayOwnerKey(envelope), Convert.FromBase64String(envelope.ImageBase64));
                    }

                    break;

                case RelayKinds.ImageEnd:
                    OnRelayImageEnd(envelope);
                    break;

                case RelayKinds.Stop:
                    StopEverything($"教师端请求停止：{envelope.Reason}");
                    break;
            }
        });
    }

    /// <summary>
    /// 中继链路上的图片：声明与收尾。
    ///
    /// 分片本身走 <see cref="OnImageChunk"/>，与局域网共用同一套拼装逻辑 ——
    /// "图片怎么攒起来"这件事不该因为走哪条链路而有两份实现。
    /// </summary>
    private void OnRelayImageStart(RelayEnvelope envelope)
    {
        var ownerKey = RelayOwnerKey(envelope);

        if (envelope.ImageTotalBytes is <= 0 or > ShoutProtocol.MaxImageBytes)
        {
            Post(() => AddLog("图片", $"「{envelope.From}」发来的图片大小不合法，已忽略。"));
            return;
        }

        lock (_imageLock)
        {
            _imageBuffer?.Dispose();
            _imageBuffer = new MemoryStream(envelope.ImageTotalBytes);
            _imageId = envelope.ImageId;
            _imageOwner = ownerKey;
            _imageStart = new ImageStartMessage
            {
                Id = envelope.ImageId ?? string.Empty,
                Text = envelope.Text ?? string.Empty,
                ContentType = envelope.ImageContentType ?? "image/jpeg",
                TotalBytes = envelope.ImageTotalBytes,
                Width = envelope.ImageWidth,
                Height = envelope.ImageHeight,
                Display = envelope.Display,
                FontSize = envelope.FontSize,
                HoldMs = envelope.HoldMs,
                Speak = envelope.Speak,
            };
        }

        AddLog("图片", $"「{envelope.From}」发来一张图片（{envelope.ImageTotalBytes / 1024} KB）");
    }

    private void OnRelayImageEnd(RelayEnvelope envelope)
    {
        var ownerKey = RelayOwnerKey(envelope);
        byte[]? bytes = null;
        ImageStartMessage? start;

        lock (_imageLock)
        {
            start = _imageStart;

            if (_imageBuffer is not null &&
                string.Equals(_imageId, envelope.ImageId, StringComparison.Ordinal) &&
                string.Equals(_imageOwner, ownerKey, StringComparison.Ordinal))
            {
                bytes = _imageBuffer.ToArray();
                _imageBuffer.Dispose();
                _imageBuffer = null;
                _imageId = null;
                _imageOwner = null;
                _imageStart = null;
            }
        }

        if (bytes is null || bytes.Length == 0 || start is null)
        {
            AddLog("图片", "图片没有收全，已忽略。");
            return;
        }

        PresentImage(
            envelope.From ?? "教师端",
            bytes,
            start.Display,
            start.FontSize,
            start.HoldMs,
            start.Text,
            start.Speak);
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

    /// <summary>
    /// 是否已设置开机自启。
    ///
    /// 初值直接取自"启动文件夹里有没有那个快捷方式"，而不是另存一个布尔值：
    /// 老师手动删掉快捷方式是常见操作，程序若还记着"已开启"就会显示一个假状态。
    /// </summary>
    [ObservableProperty]
    private bool _autoStart;

    /// <summary>Linux 上不讲"启动文件夹"这一套，部署走 systemd 单元（见 README）。</summary>
    public bool SupportsAutoStart => StartupShortcut.IsSupported;

    /// <summary>防止"设置失败 → 回滚开关 → 又触发一次设置"这种来回。</summary>
    private bool _updatingAutoStart;

    /// <summary>同上，用于置顶档位的占位项兜底拨回。</summary>
    private bool _updatingTopmost;

    /// <summary>启动文件夹的位置，直接写在界面上，方便运维自己去看。</summary>
    public string AutoStartLocation => StartupShortcut.StartupFolder;

    /// <summary>喊话提示显示在哪里。</summary>
    [ObservableProperty]
    private ShoutChannelOption? _selectedChannel;

    public IReadOnlyList<ShoutChannelOption> ChannelOptions { get; } =
    [
        new(ShoutNotificationChannel.ClassShout, "只看 ClassShout 弹窗"),
        new(ShoutNotificationChannel.ClassIsland, "只看 ClassIsland 提醒"),
        new(ShoutNotificationChannel.Both, "两处都显示"),
    ];

    /// <summary>
    /// 选了带 ClassIsland 的方式但没有投递成功时给一句说明。
    ///
    /// 老师选了"ClassIsland 提醒"却什么都没看到，第一个疑问必然是"是不是坏了"——
    /// 而最常见的原因只是没装插件。与其让他去猜，不如这里直接说清楚。
    /// </summary>
    public string ChannelHint => SelectedChannel?.Value switch
    {
        ShoutNotificationChannel.ClassIsland =>
            "需要教室端装 ClassShout 的 ClassIsland 联动插件；没装的话喊话将不再有任何提示。",
        ShoutNotificationChannel.Both =>
            "需要装了联动插件才会在 ClassIsland 里显示；没装时仍会弹 ClassShout 自己的弹窗。",
        _ => "喊话只在 ClassShout 自己的弹窗里显示。",
    };

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

        // 这一档原本叫「UIA 置顶（强制）」，但它实际上做的是周期性重申置顶，
        // 并不是 UIAccess —— 名字承诺了做不到的事。改名说清它到底做了什么。
        new(TopmostMode.Forced, "强制置顶（周期性重申）"),

        // 真正的那一档列出来但置灰：它需要签名 + 安全目录安装，当前构建满足不了。
        new(null, "UIA 置顶（暂不可用）",
            "需要程序数字签名并安装在安全目录，当前为未签名的便携版本，做不到。"),
    ];

    public string NotificationDurationText => NotificationDuration <= 0
        ? "不自动消失（点击关闭）"
        : $"{NotificationDuration} 秒后自动消失";

    // ======================== 默认展示参数 ========================
    //
    // 这三项是"发送方没指定时用哪一档"。教师端每次喊话都能覆盖它们，
    // 所以这里填的是兜底值 —— 平时怎么讲就怎么设，个别时候再单独调。

    // 三组选项直接取自 Core：教师端选"这次怎么显示"、教室端选"默认怎么显示"，
    // 两处的可选项必须完全一样，各写一份迟早对不上，而且不会报错。
    public IReadOnlyList<ShoutDisplayOption> DisplayOptions => ShoutDisplayChoices.Displays;

    public IReadOnlyList<ShoutFontSizeOption> FontSizeOptions => ShoutDisplayChoices.FontSizes;

    public IReadOnlyList<ShoutHoldOption> HoldOptions => ShoutDisplayChoices.Holds;

    /// <summary>默认展示方式。改动即时落盘并影响下一条喊话。</summary>
    public ShoutDisplayOption SelectedDefaultDisplay
    {
        get => DisplayOptions.FirstOrDefault(o => o.Value == _notificationSettings.DefaultDisplay) ?? DisplayOptions[0];
        set
        {
            if (value is null || _notificationSettings.DefaultDisplay == value.Value)
            {
                return;
            }

            _notificationSettings.DefaultDisplay = value.Value;
            _notificationSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayDefaultsHint));
        }
    }

    public ShoutFontSizeOption SelectedDefaultFontSize
    {
        get => FontSizeOptions.FirstOrDefault(o => o.Value == _notificationSettings.DefaultFontSize) ?? FontSizeOptions[1];
        set
        {
            if (value is null || _notificationSettings.DefaultFontSize == value.Value)
            {
                return;
            }

            _notificationSettings.DefaultFontSize = value.Value;
            _notificationSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayDefaultsHint));
        }
    }

    public ShoutHoldOption SelectedDefaultHold
    {
        get => HoldOptions.FirstOrDefault(o => o.Value == _notificationSettings.DefaultHoldMs) ?? HoldOptions[1];
        set
        {
            if (value is null || _notificationSettings.DefaultHoldMs == value.Value)
            {
                return;
            }

            _notificationSettings.DefaultHoldMs = value.Value;
            _notificationSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayDefaultsHint));
        }
    }

    /// <summary>把三项默认值说成一句话，放在设置卡片顶上给人核对。</summary>
    public string DisplayDefaultsHint =>
        $"当前默认：{SelectedDefaultDisplay.Label} · {SelectedDefaultFontSize.Label}字 · "
        + $"{HoldOptions.FirstOrDefault(o => o.Value == _notificationSettings.DefaultHoldMs)?.Label ?? "20 秒"}";

    /// <summary>
    /// 已开启自启、但快捷方式指向的不是当前这份程序时，改写它。
    ///
    /// 只在指向不符时才写：每次启动都重写一遍虽然也算幂等，但没必要去动用户的启动文件夹。
    /// </summary>
    private void HealAutoStartShortcut()
    {
        if (!StartupShortcut.IsEnabled)
        {
            return;
        }

        var current = Environment.ProcessPath;
        var target = StartupShortcut.Describe();

        if (current is null || string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var error = StartupShortcut.Enable();

        AddLog("系统", error is null
            ? "开机自启的快捷方式原本指向别处，已更新为当前程序。"
            : $"开机自启的快捷方式指向别处，且更新失败：{error}");
    }

    /// <summary>置顶档位的说明，直接写在界面上，避免用户猜各档到底做到了什么。</summary>
    public string TopmostModeHint => SelectedTopmost?.Value switch
    {
        TopmostMode.None => "弹窗可能被其他窗口盖住。",
        TopmostMode.Normal => "设置系统置顶。若别的程序也抢置顶，可能被压下去。",
        TopmostMode.Forced => "在系统置顶之上周期性重申，能抢过多数置顶窗口。"
                              + "但盖不住开始菜单、任务管理器这类更高窗口段的系统窗口 —— "
                              + "那一层需要 UIAccess 令牌，见下一条。",
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

    partial void OnSelectedChannelChanged(ShoutChannelOption? value)
    {
        if (value is null)
        {
            return;
        }

        _notificationSettings.Channel = value.Value;
        OnPropertyChanged(nameof(ChannelHint));
        PersistNotificationSettings();
    }

    partial void OnSelectedTopmostChanged(TopmostOption? value)
    {
        if (_updatingTopmost)
        {
            return;
        }

        if (value?.Value is not { } mode)
        {
            // 占位项（Value 为 null）被选中了。正常情况下 ComboBoxItem 已置灰、根本选不动，
            // 这里只是兜底：拨回设置里真正生效的那一档，别让界面停在一个不生效的值上。
            _updatingTopmost = true;
            try
            {
                SelectedTopmost = TopmostOptions.FirstOrDefault(o => o.Value == _notificationSettings.Topmost);
            }
            finally
            {
                _updatingTopmost = false;
            }

            return;
        }

        _notificationSettings.Topmost = mode;
        OnPropertyChanged(nameof(TopmostModeHint));
        PersistNotificationSettings();
    }

    partial void OnNotificationDurationChanged(int value)
    {
        _notificationSettings.DurationSeconds = value;
        PersistNotificationSettings();
    }

    /// <summary>
    /// 开关开机自启。实际动作就是把快捷方式建出来 / 删掉，失败要把开关拨回去 ——
    /// 留着"开着但没生效"的界面状态，比功能不可用更糟。
    /// </summary>
    partial void OnAutoStartChanged(bool value)
    {
        if (_updatingAutoStart)
        {
            return;
        }

        var error = value ? StartupShortcut.Enable() : StartupShortcut.Disable();

        if (error is not null)
        {
            AddLog("系统", $"设置开机自启失败：{error}");

            _updatingAutoStart = true;
            try
            {
                AutoStart = StartupShortcut.IsEnabled;
            }
            finally
            {
                _updatingAutoStart = false;
            }

            return;
        }

        AddLog("系统", value
            ? $"已设置开机自启：每次登录 Windows 都会自动打开教室端（{StartupShortcut.ShortcutPath}）。"
            : "已取消开机自启。");
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

    // ======================== 语音转文字（密钥自填） ========================
    //
    // 这几个属性只给设置窗口用。写回磁盘用 LocalSettings.SaveStt ——
    // 它落在 classroom-stt.json：这份配置属于**教室端**，因为音频到这里才算真正放出来。
    //
    // 每一项都立刻落盘（设置窗口写着"改动即时生效并自动保存"）。
    // 用属性而不是 [ObservableProperty] 字段，是为了在 setter 里顺手保存和刷新状态行。

    /// <summary>是否启用语音转文字。</summary>
    public bool SttEnabled
    {
        get => _sttSettings.Enabled;
        set
        {
            if (_sttSettings.Enabled == value)
            {
                return;
            }

            _sttSettings.Enabled = value;
            LocalSettings.SaveStt(_sttSettings);

            OnPropertyChanged();
            OnPropertyChanged(nameof(SttStatus));

            AddLog("语音", value ? "已启用语音转文字。" : "已关闭语音转文字。");
        }
    }

    /// <summary>接口地址。默认 OpenAI，也可填任何兼容 /v1/audio/transcriptions 的服务。</summary>
    public string SttBaseUrl
    {
        get => _sttSettings.BaseUrl;
        set
        {
            if (_sttSettings.BaseUrl == value)
            {
                return;
            }

            _sttSettings.BaseUrl = value;
            LocalSettings.SaveStt(_sttSettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttStatus));
        }
    }

    /// <summary>密钥。只存本机，日志里不出现。</summary>
    public string SttApiKey
    {
        get => _sttSettings.ApiKey ?? string.Empty;
        set
        {
            if (_sttSettings.ApiKey == value)
            {
                return;
            }

            _sttSettings.ApiKey = value;
            LocalSettings.SaveStt(_sttSettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttStatus));
        }
    }

    /// <summary>模型名。默认 whisper-1。</summary>
    public string SttModel
    {
        get => _sttSettings.Model;
        set
        {
            if (_sttSettings.Model == value)
            {
                return;
            }

            _sttSettings.Model = value;
            LocalSettings.SaveStt(_sttSettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttStatus));
        }
    }

    /// <summary>提示语言。留空由服务自行判断，中文课堂填 zh 通常更准。</summary>
    public string SttLanguage
    {
        get => _sttSettings.Language ?? string.Empty;
        set
        {
            if (_sttSettings.Language == value)
            {
                return;
            }

            _sttSettings.Language = value;
            LocalSettings.SaveStt(_sttSettings);
            OnPropertyChanged();
        }
    }

    /// <summary>配置状态一行说明，直接显示给管理员看。</summary>
    public string SttStatus => _sttSettings switch
    {
        { Enabled: false } => "未启用",
        { ApiKey: null or "" } => "已开启，但还没填密钥",
        _ => $"已启用 · {_sttSettings.Model}",
    };

    /// <summary>收到喊话时弹一条提示。</summary>
    private void NotifyOnScreen(
        string sourceName,
        string text,
        ShoutNoticeKind kind,
        ShoutDisplayPlan? plan = null,
        Bitmap? image = null)
    {
        if (!NotificationEnabled)
        {
            return;
        }

        // 两条通道各自独立：一条不通不影响另一条。
        // 教室那台电脑上常常同时挂着 ClassIsland，老师可以选只用自己的弹窗、
        // 只用 ClassIsland 的提醒，或者两处都显示。
        //
        // 发送方选了"只在大字区显示"时（Display=window），教室端自己的弹窗就不参与了 ——
        // 那正是"窗口"和"弹窗"两个选项的区别。没带展示参数的老喊话（例如语音）
        // 仍然按老规矩弹。
        var showOwnPopup = plan is not { IsWindow: true };

        if (showOwnPopup && SelectedChannel?.Value is not ShoutNotificationChannel.ClassIsland)
        {
            _notificationPresenter.Show(new NotificationContent(sourceName, text, kind is ShoutNoticeKind.Voice)
            {
                FontSize = plan?.FontSize ?? ShoutFontSizes.Default,
                HoldMs = plan?.HoldMs ?? ShoutHoldDurations.Unspecified,
                Image = image,
            });
        }

        NotifyClassIsland(sourceName, text, kind);
    }

    /// <summary>
    /// 只投 ClassIsland，不碰教室端自己的弹窗。
    ///
    /// 转写结果走这条路：它比喊话本身晚几秒到，而弹窗是"现在有人在说话"的提示，
    /// 隔了几秒再给同一句话弹第二个窗，只会让人以为又有人喊了一遍。
    /// </summary>
    private void NotifyClassIsland(string sourceName, string text, ShoutNoticeKind kind)
    {
        if (!NotificationEnabled)
        {
            return;
        }

        if (SelectedChannel?.Value is ShoutNotificationChannel.ClassIsland or ShoutNotificationChannel.Both)
        {
            _ = NotifyClassIslandAsync(sourceName, text, kind);
        }
    }

    /// <summary>
    /// 把喊话投给本机的 ClassIsland 联动插件。
    ///
    /// 刻意 fire-and-forget 且吞掉失败：插件没装、ClassIsland 没运行都是**正常情况**，
    /// 而这条投递只是"顺便再通知一处"—— 它绝不能让喊话本身慢下来或失败。
    /// 失败只在头几次和每 20 次记一条，免得教室端日志被刷满、真正的异常反而被埋掉。
    /// </summary>
    private async Task NotifyClassIslandAsync(string sourceName, string text, ShoutNoticeKind kind)
    {
        var content = ClassIslandNotice.ContentFor(kind, text);

        if (!await _classIslandNotifier.TryNotifyAsync(sourceName, content, kind).ConfigureAwait(true))
        {
            if (_classIslandNotifier.ShouldLogFailure())
            {
                AddLog("提示", "投递到 ClassIsland 失败：可能没装联动插件，或 ClassIsland 没在运行。");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 这几样刻意放在任何 await 之前。
        //
        // 退出路径上的调用方是有界等待的（见 App 的 ShutdownRequested），
        // 一旦等待超时，这个方法的后续部分就不会再执行了 ——
        // 而语音合成器和播放器原来排在最后，正好是最容易被跳过的那两个。
        // 播放设备不释放会一直占着声卡，教室端下次启动就可能没声音；
        // 这种"退出之后才发作"的故障最难查。
        //
        // 它们本身也不需要等待：Dispose 是同步的，而且各自内部已经把
        // 与在跑任务的竞态处理掉了（例如合成器会先等在读的那一条读完）。
        _notificationPresenter.Dispose();
        _classIslandNotifier.Dispose();
        _speech.Dispose();
        _player.Dispose();
        _http.Dispose();

        // 字幕计时器也要收掉：它内部持有一个 Task，
        // 退出时留着它没有任何好处，而它醒来后还会去碰已经关掉的界面。
        _transcriptHide?.Cancel();
        _transcriptHide?.Dispose();
        _transcriptHide = null;

        // 下面这些要等，属于"尽力而为"的部分
        if (_relay is not null)
        {
            await _relay.DisposeAsync().ConfigureAwait(false);
            _relay = null;
        }

        await _announcer.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
