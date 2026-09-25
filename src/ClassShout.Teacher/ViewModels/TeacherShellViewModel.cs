using ClassShout.Core.Audio;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Threading;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>服务器健康检查的解析选项。与 AccountClient 用同一套（Web 默认：camelCase + 大小写不敏感）。</summary>
internal static class RelayJsonOptions
{
    public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web);
}

/// <summary>底部导航的页面。</summary>
public enum TeacherPage
{
    Text,

    Voice,

    /// <summary>学生名单：导入、查看、改名、删除。</summary>
    Students,

    /// <summary>快速呼叫：组件拼装 + 选学生 + 模板。</summary>
    Call,

    Devices,
}

/// <summary>一条运行日志。</summary>
public sealed record TeacherLogEntry(DateTime Time, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>
/// 发现列表里的一项，带上给界面直接用的显示串和"连接"命令。
/// 命令挂在条目自己身上，XAML 模板里就不必写
/// <c>$parent[ItemsControl].((vm:TeacherShellViewModel)DataContext)</c> 这类父级转换绑定。
/// </summary>
public sealed class ClassroomListItem
{
    public ClassroomListItem(ClassroomAnnouncement announcement, Func<ClassroomListItem, Task> connect)
    {
        Announcement = announcement;
        ConnectCommand = new AsyncRelayCommand(() => connect(this));
    }

    public ClassroomAnnouncement Announcement { get; }

    public string Name => string.IsNullOrWhiteSpace(Announcement.Name) ? "未命名教室" : Announcement.Name;

    public string Subtitle => $"{Announcement.Host}:{Announcement.Port}";

    public string VersionText => string.IsNullOrWhiteSpace(Announcement.AppVersion)
        ? string.Empty
        : $"v{Announcement.AppVersion}";

    public IAsyncRelayCommand ConnectCommand { get; }
}

/// <summary>
/// 教师端外壳视图模型：连接管理、教室发现、服务器绑定、页面切换、提示条。
///
/// 有两条互斥链路：局域网直连与公网中继。<see cref="ShoutTransportRouter"/> 负责切换，
/// 文字页与语音页只持有路由器，因此切换链路时不需要重建它们。
/// </summary>
public partial class TeacherShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ShoutChannel _channel = new();
    private readonly ClassroomDiscovery _discovery = new();
    private readonly ShoutTransportRouter _transport = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ShoutQueue _shoutQueue = new();
    private readonly TeacherRelaySettings _relaySettings;

    /// <summary>一次发给多个班级时用的发送器。它自己维护临时绑定，不占"当前绑定"那条连接。</summary>
    private readonly ClassroomBroadcaster _broadcaster;

    private readonly AccountClient _account;

    private TeacherRelayClient? _relay;
    private CancellationTokenSource? _snackbarCts;

    public TeacherShellViewModel()
    {
        _relaySettings = LocalSettings.LoadTeacher();
        ServerUrl = _relaySettings.ServerUrl ?? string.Empty;

        // 已保存的地址单独存一份：登录、注册、绑定教室都以它为准，
        // 输入框里改到一半的内容不会影响任何一次网络请求。
        SavedServerUrl = _relaySettings.ServerUrl ?? string.Empty;
        BindUuid = _relaySettings.LastUuid ?? string.Empty;

        // 默认走局域网；连上服务器并绑定后再切过去
        _transport.Active = _channel;

        Text = new TextShoutViewModel(_transport)
        {
            // 队列在外壳这一层建：文字页与语音页共享同一个"正在发什么"的顺序，
            // 各自排各自的会打乱先后。
            Queue = _shoutQueue,
        };

        _shoutQueue.Changed += () => Post(Text.RefreshQueueStatus);
        _shoutQueue.Sent += (text, ok) => Post(() => Text.OnQueueSent(text, ok));
        Voice = new VoiceShoutViewModel(_transport);

        // 多班喊话：选中多个班级时逐个经中继发送，结果回到这里提示。
        _broadcaster = new ClassroomBroadcaster(_http, _relaySettings);
        _broadcaster.Log += message => Post(() => AddLog(message));
        Text.Broadcaster = _broadcaster;

        // 文字页那条局域网直连的链路也按目标班级取来源
        Text.ShoutNameFor = NameFor;
        Text.BroadcastFinished += summary => Post(() =>
        {
            AddLog(summary);
            ShowSnackbar(summary);
        });

        _channel.Log += message => Post(() => AddLog(message));
        _channel.ConnectionChanged += connected => Post(() => OnConnectionChanged(connected));
        _channel.MessageReceived += message => Post(() => HandleControlMessage(message));

        TeacherName = TeacherPlatform.DeviceName;

        _account = new AccountClient(_http, _relaySettings);

        // 已保存的教室要在构造里就读进来：老师打开应用看到的第一件事，
        // 应该是"我上次用的那间教室在这儿"，而不是一个空列表。
        RefreshSavedClassrooms();

        // 科目那几行是"每间已保存的教室一行"，所以必须跟着上面那一步走。
        // 只在账号变化时刷新的话，未登录的老师打开应用会看到一张空的科目表 ——
        // 而"没登录"恰恰是最需要按班级填科目的情况（局域网直连那条路）。
        RefreshSubjectRows();

        // 学生名单：本机资料，读进来就能用
        _rosterSettings = LocalSettings.LoadRosters();
        RefreshRosterStudents();

        // 快速呼叫：模板与"选谁"都存本机；发出去仍然走当前那条链路
        Call = new CallShoutViewModel(
            LocalSettings.LoadCalls(),
            _rosterSettings,
            // 教师名字组件取**当前这间教室**的科目："数学张老师"还是"信息技术张老师"
            // 取决于这条呼叫发给谁
            () => NameFor(_relaySettings.LastUuid),
            SendCallMessagesAsync);

        // 定时通知：读盘、接管发送、开始按秒检查
        _scheduleSettings = LocalSettings.LoadSchedule();
        _scheduler = new ShoutScheduler(_scheduleSettings) { SendAsync = SendScheduledAsync };
        _serverSchedule = new ServerScheduleClient(_http, _relaySettings);
        _scheduler.Handled += result => Post(() =>
        {
            RefreshScheduledShouts();
            LocalSettings.SaveSchedule(_scheduleSettings);

            if (result.Message is { Length: > 0 } message)
            {
                AddLog(message);
                ShowSnackbar(message);
            }
        });

        RefreshScheduledShouts();
        _scheduler.Start();

        // 用本地令牌尝试恢复登录；失败也只是回到未登录，不阻塞界面。
        // 恢复成功之后会把服务器上的定时拉回来（见 OnAccountChanged）。
        _ = RestoreSessionAsync();

        // 从分享链接启动（或者应用在前台时又点了一条链接）时自动兑现。
        // 挂在构造里而不是页面的事件上：链接可能在任何一个页面打开时到达。
        TeacherPlatform.ShareLinkReceived += link => Post(() =>
        {
            ShareLinkInput = link;
            ActivePage = TeacherPage.Devices;

            // 登录态可能还在恢复中（RestoreSessionAsync 是异步的），
            // 所以兑现失败时把原因如实显示出来，老师重试一次就好 ——
            // 比"先自己判断登录没登录、再决定要不要处理"简单得多，也不会漏掉链接。
            _ = ClaimShareAsync();
        });
    }

    public TextShoutViewModel Text { get; }

    public VoiceShoutViewModel Voice { get; }

    /// <summary>快速呼叫页：组件拼装 + 选学生 + 模板。</summary>
    public CallShoutViewModel Call { get; }

    // ======================== 连接状态 ========================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnecting;

    [ObservableProperty]
    private string _classroomName = "未连接";

    [ObservableProperty]
    private string _teacherName = string.Empty;

    [ObservableProperty]
    private bool _isClassroomMuted;

    // ======================== 导航 ========================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPage), nameof(IsVoicePage), nameof(IsStudentsPage), nameof(IsCallPage), nameof(IsDevicesPage))]
    private TeacherPage _activePage = TeacherPage.Text;

    public bool IsTextPage => ActivePage == TeacherPage.Text;

    public bool IsVoicePage => ActivePage == TeacherPage.Voice;

    public bool IsStudentsPage => ActivePage == TeacherPage.Students;

    public bool IsCallPage => ActivePage == TeacherPage.Call;

    public bool IsDevicesPage => ActivePage == TeacherPage.Devices;

    // ======================== 教室发现 ========================

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _manualAddress = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasScanned;

    public ObservableCollection<ClassroomListItem> Classrooms { get; } = [];

    public ObservableCollection<TeacherLogEntry> Logs { get; } = [];

    public bool HasClassrooms => Classrooms.Count > 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string ConnectionStatusText => IsConnecting
        ? "连接中…"
        : IsConnected ? ClassroomName : "未连接";

    // ======================== 提示条 ========================

    [ObservableProperty]
    private string? _snackbarMessage;

    public bool IsSnackbarVisible => !string.IsNullOrWhiteSpace(SnackbarMessage);

    partial void OnSnackbarMessageChanged(string? value) => OnPropertyChanged(nameof(IsSnackbarVisible));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsConnectedChanged(bool value)
    {
        Text.IsConnected = value;
        Voice.IsConnected = value;
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    /// <summary>弹出一条自动消失的提示。</summary>
    public void ShowSnackbar(string message, int seconds = 3)
    {
        SnackbarMessage = message;

        _snackbarCts?.Cancel();
        _snackbarCts?.Dispose();
        var cts = new CancellationTokenSource();
        _snackbarCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false);
                Post(() =>
                {
                    if (ReferenceEquals(_snackbarCts, cts))
                    {
                        SnackbarMessage = null;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 被新的提示顶掉属于正常流程
            }
        });
    }

    // ======================== 定时通知 ========================
    //
    // 两条路，界面上要说清是哪一条：
    //   · **服务器定时**（登录之后默认）—— 任务存在服务器上，到点由服务器自己发，
    //     老师关掉手机、甚至关机过周末，教室里照样响；
    //   · **本机定时**（没登录、或服务器拒了）—— 只有在应用运行时才会到点发送。
    // 无论哪一条，错过太久（超过三分钟）都不补发：
    // "下课前五分钟提醒交作业"在过期之后已经没有意义了。

    private readonly TeacherScheduleSettings _scheduleSettings;
    private readonly ShoutScheduler _scheduler;
    private readonly ServerScheduleClient _serverSchedule;

    /// <summary>服务器上那几条（含刚处理完的历史），只是显示用。</summary>
    private IReadOnlyList<ScheduledShoutDto> _serverSchedules = [];

    /// <summary>录音电平（0~1），界面画电平条用。</summary>
    [ObservableProperty]
    private float _clipLevel;

    private DispatcherTimer? _clipTimer;

    public ObservableCollection<ScheduledShoutItem> ScheduledShouts { get; } = [];

    /// <summary>要发的那一天。</summary>
    [ObservableProperty]
    private DateTimeOffset? _scheduleDate = DateTimeOffset.Now.AddHours(1);

    /// <summary>要发的那一刻。</summary>
    [ObservableProperty]
    private TimeSpan? _scheduleTime = DateTimeOffset.Now.AddHours(1).TimeOfDay;

    public bool HasScheduledShouts => ScheduledShouts.Count > 0;

    public string ScheduleHintText
    {
        get
        {
            var count = ScheduledShouts.Count;
            var head = count == 0
                ? "设好时间，到点自动发出去。"
                : $"还有 {count} 条没发。";

            return _serverSchedule.IsAvailable
                ? head + "登录状态下会交给服务器发送 —— 关掉手机也会到点发。错过的（超过三分钟）不补发。"
                : head + "还没登录，现在排的由本机发送：只有在应用运行时才会到点发；错过的（超过三分钟）不补发。";
        }
    }

    /// <summary>把当前输入的这一条排进定时队列。</summary>
    [RelayCommand]
    private async Task ScheduleShoutAsync()
    {
        var text = Text.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowSnackbar("先写点要说的内容，再排定时。");
            return;
        }

        if (Text.HasImage)
        {
            // 图片多发意味着同一张图对着每个班各传一遍，而定时任务可能存好几天 ——
            // 那些字节要一直留在配置文件里。先只支持文字，等真有需要再说。
            ShowSnackbar("定时暂不支持带图片的喊话，请先移除图片。");
            return;
        }

        if (ResolveSendAt() is not { } sendAt)
        {
            return;
        }

        var item = new ScheduledShout
        {
            SendAt = sendAt,
            Text = text,
            Display = Text.SelectedDisplay?.Value,
            FontSize = Text.SelectedFontSize?.Value,
            HoldMs = Text.SelectedHold?.Value ?? ShoutHoldDurations.Unspecified,
            Speak = Text.Speak,
            TargetUuids = Text.Targets.Where(t => t.IsSelected).Select(t => t.Uuid).ToList(),
        };

        await ScheduleItemAsync(item).ConfigureAwait(true);
    }

    /// <summary>
    /// 排一条：优先交给服务器，服务器用不了才退回本机。
    ///
    /// 这个优先级是刻意的：服务器定时"关掉手机也会发"，而本机定时要求应用开着。
    /// 只要有可能，就该让老师得到更可靠的那一种 —— 而且要在界面上说清楚
    /// 这一条是交给谁了，否则他会按"关掉也会发"去预期，然后错过一整条提醒。
    /// </summary>
    private async Task ScheduleItemAsync(ScheduledShout item)
    {
        if (_serverSchedule.IsAvailable && item.TargetUuids.Count > 0)
        {
            var (ok, _, error) = await _serverSchedule
                .CreateTextAsync(new ScheduleShoutRequest(
                    item.Text,
                    item.SendAt.ToUniversalTime(),
                    item.TargetUuids,
                    item.Display,
                    item.FontSize,
                    item.HoldMs,
                    item.Speak))
                .ConfigureAwait(true);

            if (ok)
            {
                AddLog($"已把 {item.TimeText} 的定时交给服务器：「{item.SummaryText}」");
                ShowSnackbar($"已交给服务器：{item.TimeText} 自动发送（关掉手机也会发）。");
                await RefreshServerSchedulesAsync().ConfigureAwait(true);
                return;
            }

            // 服务器拒了就退回本机，并把原因说出来 —— 悄悄降级会让老师
            // 误以为"关掉手机也会发"
            AddLog($"交给服务器失败（{error}），改为本机定时。");
            ShowSnackbar($"服务器没有接受（{error}），已改为本机定时：要开着应用才会发。");
        }

        _scheduleSettings.Add(item);
        LocalSettings.SaveSchedule(_scheduleSettings);
        RefreshScheduledShouts();

        AddLog($"已排定 {item.TimeText} 发送：「{item.SummaryText}」");
        ShowSnackbar($"已排定 {item.TimeText} 自动发送（本机定时，要开着应用）。");
    }

    /// <summary>把界面上的日期与时间读成一个时刻；不合法时提示并返回 null。</summary>
    private DateTimeOffset? ResolveSendAt()
    {
        if (ScheduleDate is not { } date || ScheduleTime is not { } time)
        {
            ShowSnackbar("请选择要发送的日期与时间。");
            return null;
        }

        var sendAt = new DateTimeOffset(date.Date.Add(time), DateTimeOffset.Now.Offset);

        if (sendAt <= DateTimeOffset.Now)
        {
            ShowSnackbar("这个时间已经过去了，请选一个将来的时间。");
            return null;
        }

        return sendAt;
    }

    [RelayCommand]
    private async Task CancelScheduleAsync(ScheduledShoutItem? item)
    {
        if (item is null)
        {
            return;
        }

        // 交给服务器的那几条要在服务器上取消：只删本地显示的话，
        // 到点它照样会响，而界面上已经看不见它了。
        if (item.Server is { } server)
        {
            var error = await _serverSchedule.CancelAsync(server.Id).ConfigureAwait(true);
            if (error is not null)
            {
                ShowSnackbar($"服务器上的这条没能取消：{error}");
                return;
            }

            await RefreshServerSchedulesAsync().ConfigureAwait(true);
            AddLog($"已取消服务器上 {server.SendAt.ToLocalTime():MM-dd HH:mm} 的定时。");
            return;
        }

        if (item.Record is not { } record)
        {
            return;
        }

        record.Cancelled = true;
        VoiceClipStore.Delete(record.AudioFile);
        _scheduleSettings.MoveToHistory(record);
        LocalSettings.SaveSchedule(_scheduleSettings);
        RefreshScheduledShouts();

        AddLog($"已取消 {record.TimeText} 的定时喊话。");
    }

    /// <summary>把待发列表读进界面。</summary>
    private void RefreshScheduledShouts()
    {
        ScheduledShouts.Clear();

        // 服务器上那些排进去的也列在这里：老师只想知道"我排了什么"，
        // 分成两个列表会让他两头找。
        foreach (var dto in _serverSchedules.Where(s => s.Status == ServerScheduleStatus.Pending))
        {
            ScheduledShouts.Add(ScheduledShoutItem.FromServer(dto, CancelScheduleCommand));
        }

        foreach (var record in _scheduleSettings.Items.OrderBy(i => i.SendAt))
        {
            ScheduledShouts.Add(new ScheduledShoutItem(record, item => _ = CancelScheduleAsync(item)));
        }

        OnPropertyChanged(nameof(HasScheduledShouts));
        OnPropertyChanged(nameof(ScheduleHintText));
    }

    /// <summary>问一次服务器上有哪些定时。</summary>
    [RelayCommand]
    private async Task RefreshServerSchedulesAsync()
    {
        if (!_serverSchedule.IsAvailable)
        {
            _serverSchedules = [];
            RefreshScheduledShouts();
            return;
        }

        _serverSchedules = await _serverSchedule.ListAsync().ConfigureAwait(true);
        RefreshScheduledShouts();
    }

    // ======================== 定时语音（录一段、存下来、到点放） ========================

    private readonly VoiceClipRecorder _clipRecorder = new();

    /// <summary>正在为定时任务录一段语音。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleVoiceButtonText))]
    private bool _isRecordingClip;

    /// <summary>已录时长（秒），界面上显示成 mm:ss。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipElapsedText))]
    private double _clipElapsed;

    /// <summary>录完但还没排进定时的那一段。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingClip))]
    [NotifyPropertyChangedFor(nameof(PendingClipText))]
    private VoiceClip? _pendingClip;

    /// <summary>录音区的提示/错误。</summary>
    [ObservableProperty]
    private string _clipStatus = string.Empty;

    public bool HasPendingClip => PendingClip is { IsEmpty: false };

    public bool IsRecordingClipVisible => TeacherPlatform.HasRecorder;

    public string ScheduleVoiceButtonText => IsRecordingClip ? "停止录音" : "录一段语音";

    public string ClipElapsedText => TimeSpan.FromSeconds(ClipElapsed).ToString(@"mm\:ss");

    public string PendingClipText => PendingClip is { } clip
        ? $"已录 {clip.Seconds:0.#} 秒，可以排进下面的时间。"
        : string.Empty;

    /// <summary>录音上限（秒），界面上写出来。</summary>
    public string ClipLimitText => $"最长 {ServerScheduledShout.MaxVoiceSeconds} 秒。";

    /// <summary>点一下开始录，再点一下停止。</summary>
    [RelayCommand]
    private async Task ToggleClipRecordingAsync()
    {
        if (IsRecordingClip)
        {
            await StopClipRecordingAsync().ConfigureAwait(true);
            return;
        }

        ClipStatus = string.Empty;
        PendingClip = null;
        ClipElapsed = 0;

        _clipRecorder.LevelChanged += OnClipLevel;
        _clipRecorder.ReachedLimit += OnClipReachedLimit;
        _clipRecorder.Failed += OnClipFailed;

        if (!await _clipRecorder.StartAsync().ConfigureAwait(true))
        {
            DetachClipRecorder();
            return;
        }

        IsRecordingClip = true;
        ClipStatus = "正在录，再点一下停止。";
        _clipTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _clipTimer.Tick -= OnClipTick;
        _clipTimer.Tick += OnClipTick;
        _clipTimer.Start();
    }

    [RelayCommand]
    private async Task StopClipRecordingAsync()
    {
        _clipTimer?.Stop();
        DetachClipRecorder();

        var clip = await _clipRecorder.StopAsync().ConfigureAwait(true);

        IsRecordingClip = false;
        ClipElapsed = clip?.Seconds ?? ClipElapsed;

        if (clip is not { IsEmpty: false } recorded)
        {
            PendingClip = null;
            ClipStatus = "什么都没录到，再试一次。";
            return;
        }

        PendingClip = recorded;
        ClipStatus = $"录好了：{recorded.Seconds:0.#} 秒。选好时间，点「把这段语音排进定时」。";
    }

    /// <summary>
    /// 把录好的这段语音排进定时。
    ///
    /// 优先交给服务器（那样关掉手机也会发），但音频要先落一份到本机 ——
    /// 交不出去时它就退回本机定时，而本机定时到点得能读出声来。
    /// </summary>
    [RelayCommand]
    private async Task ScheduleVoiceClipAsync()
    {
        if (PendingClip is not { IsEmpty: false } clip)
        {
            ShowSnackbar("先录一段语音。");
            return;
        }

        if (ResolveSendAt() is not { } sendAt)
        {
            return;
        }

        var item = new ScheduledShout
        {
            SendAt = sendAt,
            Kind = ScheduledShoutKinds.Voice,
            AudioSeconds = clip.Seconds,
            Display = Text.SelectedDisplay?.Value,
            FontSize = Text.SelectedFontSize?.Value,
            HoldMs = Text.SelectedHold?.Value ?? ShoutHoldDurations.Unspecified,
            Speak = Text.Speak,
            TargetUuids = Text.Targets.Where(t => t.IsSelected).Select(t => t.Uuid).ToList(),
        };

        if (_serverSchedule.IsAvailable && item.TargetUuids.Count > 0)
        {
            var (ok, _, error) = await _serverSchedule
                .CreateVoiceAsync(sendAt.ToUniversalTime(), item.TargetUuids, new VoiceRecording(clip.ToWav(), clip.Seconds))
                .ConfigureAwait(true);

            if (ok)
            {
                PendingClip = null;
                ClipStatus = string.Empty;
                AddLog($"已把 {item.TimeText} 的定时语音交给服务器（{clip.Seconds:0.#} 秒）。");
                ShowSnackbar($"已交给服务器：{item.TimeText} 播放这段语音（关掉手机也会发）。");
                await RefreshServerSchedulesAsync().ConfigureAwait(true);
                return;
            }

            AddLog($"定时语音交给服务器失败（{error}），改为本机定时。");
            ShowSnackbar($"服务器没有接受（{error}），已改为本机定时：要开着应用才会发。");
        }

        item.AudioFile = VoiceClipStore.Save(item.Id, clip);

        if (item.AudioFile is null)
        {
            ShowSnackbar("这段语音没能存到本机，排不了定时。");
            return;
        }

        _scheduleSettings.Add(item);
        LocalSettings.SaveSchedule(_scheduleSettings);
        RefreshScheduledShouts();

        PendingClip = null;
        ClipStatus = string.Empty;

        AddLog($"已排定 {item.TimeText} 播放一段 {clip.Seconds:0.#} 秒的语音（本机定时）。");
        ShowSnackbar($"已排定 {item.TimeText} 播放（本机定时，要开着应用）。");
    }

    /// <summary>丢掉录好的那一段。</summary>
    [RelayCommand]
    private void DiscardClip()
    {
        PendingClip = null;
        ClipElapsed = 0;
        ClipStatus = string.Empty;
    }

    private void OnClipTick(object? sender, EventArgs e) => ClipElapsed = _clipRecorder.Seconds;

    private void OnClipLevel(float level) => Post(() => ClipLevel = level);

    private void OnClipReachedLimit() => Post(() =>
    {
        ClipStatus = $"到 {ServerScheduledShout.MaxVoiceSeconds} 秒上限了，自动停止。";
        _ = StopClipRecordingAsync();
    });

    private void OnClipFailed(string message) => Post(() =>
    {
        ClipStatus = message;
        _ = StopClipRecordingAsync();
    });

    private void DetachClipRecorder()
    {
        _clipRecorder.LevelChanged -= OnClipLevel;
        _clipRecorder.ReachedLimit -= OnClipReachedLimit;
        _clipRecorder.Failed -= OnClipFailed;
    }

    /// <summary>
    /// 真正把一条定时任务发出去。
    ///
    /// 目标在创建时就固定了：老师上午排的任务，下午早就把绑定的班级换过好几轮，
    /// 而他要的是"发给当时选的那几个班"。
    /// </summary>
    private async Task<(bool Ok, string? Message)> SendScheduledAsync(ScheduledShout item)
    {
        // 语音定时：把存下来的那段 PCM 按实时节奏放出去。
        // 走的是与"按住说话"完全相同的那条音频通路 —— 教室端看到的数据流
        // 一模一样，不引入只在定时语音上才会出现的第二种输入形态。
        if (ScheduledShoutKinds.IsVoice(item.Kind))
        {
            return await SendScheduledVoiceAsync(item).ConfigureAwait(true);
        }

        var message = new TextShoutMessage
        {
            Text = item.Text,
            Rate = Text.Rate,
            Volume = Text.Volume,
            Display = item.Display,
            FontSize = item.FontSize,
            HoldMs = item.HoldMs,
            Speak = item.Speak,
        };

        var targets = item.TargetUuids
            .Select(uuid => _relaySettings.RecentClassrooms
                .FirstOrDefault(record => string.Equals(record.Uuid, uuid, StringComparison.OrdinalIgnoreCase)))
            .OfType<BoundClassroom>()
            .ToList();

        // 目标都还在（没被移除）且不止一间 → 逐个经中继发；
        // 否则退回"当前链路" —— 这是老师按下发送按钮时会走的那条路。
        if (targets.Count > 1)
        {
            var results = await _broadcaster
                .SendTextAsync(targets, message, target => NameFor(target.Uuid))
                .ConfigureAwait(true);
            var sent = results.Count(r => r.Ok);

            return sent > 0
                ? (true, $"定时喊话已发给 {sent} 个班级。")
                : (false, "定时喊话一条都没发出去。");
        }

        var ok = await _channel.SendTextAsync(message).ConfigureAwait(true);
        return ok ? (true, "定时喊话已发出。") : (false, "定时喊话没能发出（当前没有可用的链路）。");
    }

    /// <summary>
    /// 本机定时里那条语音：读回存好的 PCM，按实时节奏送出去。
    ///
    /// 刻意不"一口气全塞进去"：教室端是按数据流播放的，一股脑塞进去要么撑爆缓冲，
    /// 要么让播放器用一种平时不会走的路径 —— 而那种路径只在定时语音上被用到，
    /// 真机上出问题最难查。
    /// </summary>
    private async Task<(bool Ok, string? Message)> SendScheduledVoiceAsync(ScheduledShout item)
    {
        var clip = VoiceClipStore.Load(item.AudioFile);

        if (clip is not { Pcm.Length: > 0 } voice)
        {
            // 音频丢了就说清楚：这条不可能再发出去，留在列表里也没意义
            return (false, "语音文件已经丢失，这条定时发不出去。");
        }

        if (!_channel.IsConnected)
        {
            return (false, "当前没有可用的链路。");
        }

        await _channel.BeginAudioAsync(voice.Format).ConfigureAwait(true);

        var chunkSize = voice.Format.BytesForDuration(100);
        if (chunkSize <= 0)
        {
            return (false, "这段语音的格式不合法。");
        }

        try
        {
            for (var offset = 0; offset < voice.Pcm.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, voice.Pcm.Length - offset);
                await _channel.SendAudioAsync(voice.Pcm.AsMemory(offset, length)).ConfigureAwait(true);

                if (offset + length < voice.Pcm.Length)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            await _channel.EndAudioAsync().ConfigureAwait(true);
        }

        // 发完就把文件删掉：这条已经用过了，留着只会占地方
        VoiceClipStore.Delete(item.AudioFile);

        return (true, $"定时语音已发出（{voice.Seconds:0.#} 秒）。");
    }

    // ======================== 分享链接一键绑定 ========================

    /// <summary>用户粘贴进来的分享链接（或令牌）。</summary>
    [ObservableProperty]
    private string _shareLinkInput = string.Empty;

    [ObservableProperty]
    private string _shareError = string.Empty;

    public bool HasShareError => !string.IsNullOrWhiteSpace(ShareError);

    partial void OnShareErrorChanged(string value) => OnPropertyChanged(nameof(HasShareError));

    /// <summary>
    /// 用一条分享链接把自己绑定到那几个班。
    ///
    /// 兑现的动作发生在服务器上（把这几间授权给当前账号），教师端只是发起请求，
    /// 然后把结果放进"已保存的教室" —— 于是老师接下来要做的还是熟悉的那一步：
    /// 在列表里点一下。这里不直接替他切换绑定，是因为链接里可能有好几个班，
    /// 而这节课要喊哪一间只有他知道。
    /// </summary>
    [RelayCommand]
    private async Task ClaimShareAsync()
    {
        ShareError = string.Empty;

        var input = ShareLinkInput?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            ShareError = "请把管理员给你的分享链接粘进来。";
            return;
        }

        var token = ShareLink.TryExtractToken(input, out var serverFromLink);

        if (token is null)
        {
            ShareError = "这看起来不是一条分享链接。完整链接形如 https://你的服务器/share/xxxx。";
            return;
        }

        if (!IsSignedIn)
        {
            ShareError = "请先在上面的账号卡片里登录，再使用分享链接 —— 这样服务器才知道这几个班给了谁。";
            return;
        }

        // 链接里带的服务器地址优先于本机配置：分享来自哪台服务器，就该去哪台兑现。
        var server = serverFromLink ?? SavedServerUrl;

        if (string.IsNullOrWhiteSpace(server))
        {
            ShareError = "这条链接里没有服务器地址，请先在上面那张「中继服务器」卡片里填好地址。";
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{server.TrimEnd('/')}{string.Format(RelayPaths.ShareClaim, token)}");

            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _relaySettings.AuthToken);

            using var response = await _http.SendAsync(request).ConfigureAwait(true);
            var result = await response.Content
                .ReadFromJsonAsync<ShareClaimResponse>(RelayJsonOptions.Value)
                .ConfigureAwait(true);

            if (result is not { Ok: true })
            {
                ShareError = result?.Error ?? $"兑现失败（HTTP {(int)response.StatusCode}）。";
                return;
            }

            // 服务器已经把这几个班授权给账号了；这里把它们记进"已保存的教室"，
            // 口令为空 —— 它们靠的是管理员授权，不需要口令。
            var added = 0;

            foreach (var classroom in result.Classrooms ?? [])
            {
                TeacherRelaySettings.Remember(_relaySettings.RecentClassrooms, new BoundClassroom(
                    classroom.Uuid,
                    classroom.Name,
                    DateTimeOffset.Now,
                    server,
                    Secret: null));

                added++;
            }

            // 链接可能来自另一台服务器：把地址也存下来，否则接下来绑定会打到旧服务器上。
            if (serverFromLink is not null &&
                !string.Equals(serverFromLink, SavedServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                PersistServerUrl(serverFromLink);
            }

            LocalSettings.SaveTeacher(_relaySettings);
            RefreshSavedClassrooms();
            await RefreshAuthorizedAsync().ConfigureAwait(true);

            ShareLinkInput = string.Empty;
            AddLog($"分享链接已兑现：{result.Granted} 个班级新授权，共 {added} 个班级可用。");
            ShowSnackbar($"已加入 {added} 个班级，在下面点一下即可绑定。");
            RefreshAuthorizedCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ShareError = $"连不上服务器：{ex.Message}";
        }
    }

    // ======================== 学生名单 ========================
    //
    // 名单存在本机：它是老师自己的备课资料（从教务系统导出、或在 Excel 里手打的一份表），
    // 不该被传到服务器上去 —— 服务器只需要知道"某间教室收到了什么"，
    // 没有任何理由持有一整份学生名册。

    private readonly TeacherRosterSettings _rosterSettings;

    /// <summary>当前名单里的学生，界面直接绑它。</summary>
    public ObservableCollection<StudentRow> RosterStudents { get; } = [];

    /// <summary>所有名单（一位老师通常教好几个班）。</summary>
    public ObservableCollection<StudentRoster> Rosters { get; } = [];

    /// <summary>导入时粘贴的那一大段文本。</summary>
    [ObservableProperty]
    private string _rosterImportText = string.Empty;

    /// <summary>这份名单叫什么，例如"三年二班"。</summary>
    [ObservableProperty]
    private string _rosterImportName = string.Empty;

    [ObservableProperty]
    private string _rosterHint = string.Empty;

    /// <summary>选中的名单。</summary>
    public StudentRoster? ActiveRoster
    {
        get => _rosterSettings.Active;
        set
        {
            if (value is null || _rosterSettings.ActiveRosterId == value.Id)
            {
                return;
            }

            _rosterSettings.ActiveRosterId = value.Id;
            LocalSettings.SaveRosters(_rosterSettings);

            OnPropertyChanged();
            RefreshRosterStudents();
        }
    }

    public bool HasRosters => Rosters.Count > 0;

    public bool HasRosterStudents => RosterStudents.Count > 0;

    public string RosterSummaryText => ActiveRoster is { } roster
        ? $"{roster.Name} · {roster.Students.Count} 名学生"
        : "还没有名单";

    /// <summary>把当前名单读进界面。</summary>
    private void RefreshRosterStudents()
    {
        Rosters.Clear();

        foreach (var roster in _rosterSettings.Rosters)
        {
            Rosters.Add(roster);
        }

        RosterStudents.Clear();

        if (ActiveRoster is { } active)
        {
            foreach (var student in active.Students)
            {
                RosterStudents.Add(new StudentRow(student, UseStudent));
            }
        }

        OnPropertyChanged(nameof(ActiveRoster));
        OnPropertyChanged(nameof(HasRosters));
        OnPropertyChanged(nameof(HasRosterStudents));
        OnPropertyChanged(nameof(RosterSummaryText));

        // 名单换了，呼叫页的候选学生也跟着换
        Call?.LoadStudents();
    }

    private bool HasRosterImportText => !string.IsNullOrWhiteSpace(RosterImportText);

    partial void OnRosterImportTextChanged(string value) => ImportRosterCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasRosterImportText))]
    private void ImportRoster()
    {
        var name = string.IsNullOrWhiteSpace(RosterImportName)
            ? $"名单 {DateTime.Now:MM-dd HH:mm}"
            : RosterImportName.Trim();

        var result = RosterCsv.Parse(RosterImportText, name);

        if (!result.Ok || result.Roster is null)
        {
            RosterHint = result.SkippedLines.Count > 0
                ? string.Join(" ", result.SkippedLines)
                : "没有读到任何学生。每行格式：姓名,学号,简写,小组（后三项可留空）。";

            return;
        }

        _rosterSettings.Rosters.Add(result.Roster);
        _rosterSettings.ActiveRosterId = result.Roster.Id;
        LocalSettings.SaveRosters(_rosterSettings);

        RosterImportText = string.Empty;
        RosterImportName = string.Empty;

        var skipped = result.SkippedLines.Count == 0
            ? string.Empty
            : $"（跳过 {result.SkippedLines.Count} 行：{string.Join(" ", result.SkippedLines)}）";

        RosterHint = $"已导入「{result.Roster.Name}」，共 {result.Roster.Students.Count} 名学生。{skipped}";
        AddLog(RosterHint);

        RefreshRosterStudents();
    }

    /// <summary>删除当前名单。</summary>
    [RelayCommand]
    private void DeleteRoster()
    {
        if (ActiveRoster is not { } roster)
        {
            return;
        }

        _rosterSettings.Rosters.Remove(roster);
        _rosterSettings.ActiveRosterId = _rosterSettings.Rosters.FirstOrDefault()?.Id;
        _rosterSettings.SelectedStudentIds.Clear();
        LocalSettings.SaveRosters(_rosterSettings);

        RosterHint = $"已删除名单「{roster.Name}」。";
        AddLog(RosterHint);

        RefreshRosterStudents();
    }

    /// <summary>把一位学生填进文字页 —— 最直接的"呼叫"方式。</summary>
    [RelayCommand]
    private void UseStudent(StudentRow? row)
    {
        if (row is null)
        {
            return;
        }

        ActivePage = TeacherPage.Text;
        Text.Text = row.Student.Label;
    }

    /// <summary>
    /// 把呼叫页拼出来的几句依次发出去，返回成功条数。
    ///
    /// 用的展示参数与「文字」页当前那一套相同 —— 呼叫页不另设一套，
    /// 否则老师会看到"同一个应用里两条路发出去的字号不一样"。
    /// </summary>
    private async Task<int> SendCallMessagesAsync(IReadOnlyList<string> messages)
    {
        var sent = 0;

        foreach (var text in messages)
        {
            var message = new TextShoutMessage
            {
                Text = text,
                Rate = Text.Rate,
                Volume = Text.Volume,
                Display = Text.SelectedDisplay?.Value,
                FontSize = Text.SelectedFontSize?.Value,
                HoldMs = Text.SelectedHold?.Value ?? ShoutHoldDurations.Unspecified,
                Speak = Text.Speak,
            };

            // 一条失败不打断其余：一次叫三位学生，不该因为第一位没发出去就全都不发
            if (await _channel.SendTextAsync(message).ConfigureAwait(true))
            {
                sent++;
                AddLog($"呼叫：{text}");
            }
            else
            {
                AddLog($"呼叫未发出：{text}");
            }
        }

        ShowSnackbar(sent == messages.Count ? $"已呼叫 {sent} 条。" : $"发出 {sent}/{messages.Count} 条。");
        return sent;
    }

    // ======================== 命令 ========================

    [RelayCommand]
    private void Navigate(TeacherPage page) => ActivePage = page;

    // 导航栏的三个入口。
    // 拆成三个无参命令而不是一个带枚举参数的命令：
    // XAML 里 CommandParameter="Text" 需要把字符串转成枚举，容易在编译绑定下出错。
    [RelayCommand]
    private void NavigateText() => ActivePage = TeacherPage.Text;

    [RelayCommand]
    private void NavigateVoice() => ActivePage = TeacherPage.Voice;

    [RelayCommand]
    private void NavigateStudents() => ActivePage = TeacherPage.Students;

    [RelayCommand]
    private void NavigateCall() => ActivePage = TeacherPage.Call;

    [RelayCommand]
    private void NavigateDevices() => ActivePage = TeacherPage.Devices;

    /// <summary>扫描局域网内的教室端。</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        ErrorMessage = null;

        try
        {
            var found = await _discovery.ScanAsync().ConfigureAwait(true);

            Classrooms.Clear();
            foreach (var item in found)
            {
                Classrooms.Add(new ClassroomListItem(item, target => ConnectAsync(target)));
            }

            HasScanned = true;
            OnPropertyChanged(nameof(HasClassrooms));

            if (found.Count == 0)
            {
                ErrorMessage = "没有发现教室端。请确认教室端已启动，且两台设备在同一个 Wi-Fi 下。";
            }
            else
            {
                AddLog($"发现 {found.Count} 个教室端");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>连接到发现的教室端。</summary>
    [RelayCommand]
    private async Task ConnectAsync(ClassroomListItem? item)
    {
        if (item is null || IsConnecting)
        {
            return;
        }

        await ConnectCoreAsync(
            ct => _channel.ConnectAsync(item.Announcement, NameForLan(item.Name), ct),
            $"正在连接「{item.Name}」…",
            item.Name).ConfigureAwait(true);
    }

    /// <summary>用「IP:端口」手动连接。</summary>
    [RelayCommand]
    private async Task ConnectManualAsync()
    {
        ErrorMessage = null;

        if (!TryParseAddress(ManualAddress, out var endpoint))
        {
            ErrorMessage = "地址格式不对，应形如 192.168.1.5:45900";
            return;
        }

        await ConnectCoreAsync(
            ct => _channel.ConnectAsync(endpoint, NameFor(_relaySettings.LastUuid), ct),
            $"正在连接 {endpoint}…",
            endpoint.ToString()).ConfigureAwait(true);
    }

    private async Task ConnectCoreAsync(
        Func<CancellationToken, Task> connect,
        string progressMessage,
        string displayName)
    {
        // 守卫放在这个唯一的汇合点上，而不是各个入口各写一遍。
        //
        // 原来只有"从扫描列表点进去"那条路有守卫，手动填地址那条没有：
        // 手快连点两下，第二次会把第一次刚建立的连接断掉（下面那句 DisconnectAsync），
        // 两条流程交替地往同一个 _channel 上写，最后到底连上没连上、
        // 状态该显示什么，全看时序。放在这里就不必再担心以后新增入口时忘了加。
        if (IsConnecting)
        {
            AddLog("已有连接正在进行，本次请求已忽略。");
            return;
        }

        IsConnecting = true;
        ErrorMessage = null;

        try
        {
            if (_channel.IsConnected)
            {
                await _channel.DisconnectAsync().ConfigureAwait(true);
            }

            AddLog(progressMessage);

            // 局域网内握手很快，但网络不通时默认超时会等很久，这里给 6 秒上限
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await connect(timeout.Token).ConfigureAwait(true);

            ClassroomName = displayName;
            ShowSnackbar($"已连接「{_channel.ClassroomName}」");
            ActivePage = TeacherPage.Text;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "连接超时：请检查地址是否正确、教室端是否已启动、防火墙是否放行。";
            AddLog("连接超时");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"连接失败：{ex.Message}";
            AddLog($"连接失败：{ex.Message}");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _channel.DisconnectAsync().ConfigureAwait(true);
        AddLog("已断开与教室端的连接");
        ShowSnackbar("已断开连接");
    }

    /// <summary>把解析 IP:端口 的逻辑单独拿出来，便于在界面层给出明确提示。</summary>
    private static bool TryParseAddress(string? text, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, ShoutProtocol.DefaultTcpPort);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        var port = ShoutProtocol.DefaultTcpPort;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535))
        {
            return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    // ======================== 账号与登录 ========================

    /// <summary>是否处于注册模式（否则是登录模式）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountSubmitText))]
    [NotifyPropertyChangedFor(nameof(AccountModeHint))]
    [NotifyPropertyChangedFor(nameof(AccountToggleText))]
    private bool _isRegisterMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _loginAccount = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _loginPassword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _registerUsername = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _registerEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _registerDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _registerPassword = string.Empty;

    /// <summary>任教科目，例如"数学"。可留空 —— 留空时喊话来源只报姓名。</summary>
    [ObservableProperty]
    private string _registerSubject = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
    private bool _isAccountBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountError))]
    private string? _accountError;

    public bool HasAccountError => !string.IsNullOrWhiteSpace(AccountError);

    /// <summary>是否已登录。</summary>
    public bool IsSignedIn => _account.IsSignedIn;

    /// <summary>当前登录的老师姓名。喊话来源与教室端弹窗显示的都是它。</summary>
    public string SignedInName => _account.DisplayName;

    /// <summary>账号标签：用户名或邮箱。</summary>
    public string AccountLabel => _account.AccountLabel;

    // ======================== 当前链路 ========================

    /// <summary>
    /// 当前实际在用的链路。
    ///
    /// 优先级是硬性的：同网段时一律走局域网直连 —— 延迟更低、不占公网带宽、
    /// 也不经过任何中间服务器。只有直连不可用时才回落到公网中继。
    /// 界面把这个状态显式显示出来，用户才不会疑惑"我明明填了服务器怎么还连不上"。
    /// </summary>
    public string ActiveLinkText => _transport.Active switch
    {
        ShoutChannel => "局域网直连",
        RelayShoutTransport => "公网中继",
        _ => "未连接",
    };

    /// <summary>是否正在走公网中继（用于界面上的提示）。</summary>
    public bool IsUsingRelay => _transport.Active is RelayShoutTransport;

    private void NotifyLinkChanged()
    {
        OnPropertyChanged(nameof(ActiveLinkText));
        OnPropertyChanged(nameof(IsUsingRelay));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    public string AccountSubmitText => IsRegisterMode ? "注册并登录" : "登录";

    /// <summary>模式切换按钮上的文字。放在视图模型里而不是 XAML 的 StringFormat，绑错了好查。</summary>
    public string AccountToggleText => IsRegisterMode
        ? "已有账号？切换到登录"
        : "没有账号？切换到注册";

    public string AccountModeHint => IsRegisterMode
        ? "用户名与邮箱至少填一个，之后两者都能用来登录。"
        : "用户名或邮箱都可以登录。";

    private bool CanLogin
        => !IsAccountBusy && !IsSignedIn
           && !string.IsNullOrWhiteSpace(LoginAccount)
           && !string.IsNullOrEmpty(LoginPassword);

    private bool CanRegister
        => !IsAccountBusy && !IsSignedIn
           && (!string.IsNullOrWhiteSpace(RegisterUsername) || !string.IsNullOrWhiteSpace(RegisterEmail))
           && !string.IsNullOrWhiteSpace(RegisterDisplayName)
           && !string.IsNullOrEmpty(RegisterPassword);

    private bool CanLogout => !IsAccountBusy && IsSignedIn;

    [RelayCommand]
    private void ToggleAccountMode()
    {
        IsRegisterMode = !IsRegisterMode;
        AccountError = null;
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsAccountBusy = true;
        AccountError = null;

        try
        {
            // 按钮刻意不因"没配服务器"而置灰：置灰没有解释，老师只会以为功能坏了。
            // 让他点得动、然后告诉他该去哪一步，比一个点不亮的按钮有用。
            if (!IsServerConfigured)
            {
                AccountError = "还没有配置服务器地址。请先在上面那张「中继服务器」卡片里填写地址并点「保存地址」。";
                return;
            }

            var (ok, error) = await _account.LoginAsync(LoginAccount, LoginPassword).ConfigureAwait(true);
            if (!ok)
            {
                AccountError = error;
                return;
            }

            LoginPassword = string.Empty;
            OnAccountChanged();
            ShowSnackbar($"欢迎，{SignedInName}");
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync()
    {
        IsAccountBusy = true;
        AccountError = null;

        try
        {
            if (!IsServerConfigured)
            {
                AccountError = "还没有配置服务器地址。请先在上面那张「中继服务器」卡片里填写地址并点「保存地址」。";
                return;
            }

            var (ok, error) = await _account
                .RegisterAsync(RegisterUsername, RegisterEmail, RegisterDisplayName, RegisterPassword, RegisterSubject)
                .ConfigureAwait(true);

            if (!ok)
            {
                AccountError = error;
                return;
            }

            RegisterPassword = string.Empty;
            IsRegisterMode = false;
            OnAccountChanged();
            ShowSnackbar($"注册成功，欢迎 {SignedInName}");
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLogout))]
    private async Task LogoutAsync()
    {
        IsAccountBusy = true;

        try
        {
            // 登出顺带解除教室绑定：绑定是挂在账号上的，登出后不该继续生效
            if (IsServerBound)
            {
                await UnbindServerAsync().ConfigureAwait(true);
            }

            await _account.LogoutAsync().ConfigureAwait(true);

            // 多班发送器里那些临时绑定也是挂在账号上的，一并断掉 ——
            // 不然登出之后还能靠它们把消息发进教室，而界面上已经显示"未登录"了。
            await _broadcaster.ResetAsync().ConfigureAwait(true);

            OnAccountChanged();
            AddLog("已退出登录。");
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    /// <summary>登录状态变化后刷新相关显示，并把老师姓名同步到喊话标识上。</summary>
    private void OnAccountChanged()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignedInName));
        OnPropertyChanged(nameof(AccountLabel));

        // 已保存教室那段的两行说明要看登录状态（靠授权绑的那几间必须先登录）
        OnPropertyChanged(nameof(SavedClassroomsHint));

        // 已登录就用账号里的姓名；未登录回退到设备名，保证局域网直连仍可用
        TeacherName = IsSignedIn ? SignedInName : TeacherPlatform.DeviceName;
        Text.TeacherName = TeacherName;

        // 科目也跟着账号走：换台手机登录，之前填过的科目要自己回来
        RefreshSubjectRows();
        _ = RefreshSubjectsFromServerAsync();

        // 服务器上排着的定时也要拉回来：换台设备登录之后，
        // "我排过什么"必须看得见，否则老师没法取消它
        _ = RefreshServerSchedulesAsync();

        BindServerCommand.NotifyCanExecuteChanged();

        // 登录状态变了，已授权教室列表也跟着变
        _ = RefreshAuthorizedAsync();
    }

    /// <summary>启动时用本地令牌尝试恢复登录状态。</summary>
    private async Task RestoreSessionAsync()
    {
        if (!_account.IsSignedIn)
        {
            return;
        }

        var restored = await _account.TryRestoreSessionAsync().ConfigureAwait(true);
        Post(OnAccountChanged);

        if (!restored && _account.IsSignedIn == false)
        {
            Post(() => AddLog("登录状态已失效，请重新登录。"));
        }
    }

    // ======================== 任教科目（默认 + 按班级） ========================

    /// <summary>
    /// 这位老师在某一间教室里的喊话来源。
    ///
    /// 服务器在**中继**那条路上会自己算一遍（以账号为准，客户端冒充不了），
    /// 而**局域网直连**那条路不经过服务器 —— 名字只能由教师端自己写进喊话里。
    /// 两条路必须算得一样，否则同一个班在两台设备上会看到不同的名字。
    /// </summary>
    private string NameFor(string? classroomUuid) => _relaySettings.ShoutNameFor(classroomUuid);

    /// <summary>
    /// 局域网发现到的教室该用哪份科目。
    ///
    /// 局域网那条路上没有 UUID（发现包只有名字和地址），所以只能按**教室名**
    /// 和已保存的教室对一下 —— 也正是老师认教室的方式。
    /// 对不上就用默认科目：宁可少两个字，也不要给某个班贴错科目。
    /// </summary>
    private string NameForLan(string? classroomName)
    {
        if (string.IsNullOrWhiteSpace(classroomName))
        {
            return NameFor(null);
        }

        var matched = _relaySettings.RecentClassrooms.FirstOrDefault(
            classroom => string.Equals(classroom.Name, classroomName.Trim(), StringComparison.OrdinalIgnoreCase));

        return NameFor(matched?.Uuid);
    }

    /// <summary>默认科目输入框。</summary>
    [ObservableProperty]
    private string _defaultSubject = string.Empty;

    /// <summary>按班级的科目：一行一个已保存的教室。</summary>
    public ObservableCollection<SubjectRow> SubjectRows { get; } = [];

    /// <summary>保存结果的一句话回执。</summary>
    [ObservableProperty]
    private string _subjectStatus = string.Empty;

    public bool HasSubjectRows => SubjectRows.Count > 0;

    /// <summary>没有登录时要说清楚：本地改的只影响这台设备自己贴的名字。</summary>
    public string SubjectHintText => IsSignedIn
        ? "这些科目存在账号里，换台手机登录也会跟着回来。教室里看到的来源就是「科目＋姓名」。"
        : "还没登录：现在改的只存在这台设备上，只影响局域网直连时贴的来源。"
          + "登录之后同一份会存到账号里，换设备也能带过去。";

    private void RefreshSubjectRows()
    {
        // 先把界面上还没保存的内容丢掉：这个列表是"从已保存的教室重建"，
        // 保留半截编辑状态会让"我明明改过"和"列表里没有"同时为真。
        SubjectRows.Clear();

        foreach (var classroom in _relaySettings.RecentClassrooms)
        {
            SubjectRows.Add(new SubjectRow(classroom, SubjectForClassroom(classroom.Uuid)));
        }

        DefaultSubject = _relaySettings.Subject ?? string.Empty;
        OnPropertyChanged(nameof(HasSubjectRows));
        OnPropertyChanged(nameof(SubjectHintText));
    }

    private string SubjectForClassroom(string uuid)
        => TeachingSubjects.For(_relaySettings.Subject, _relaySettings.SubjectByClassroom, uuid) ?? string.Empty;

    /// <summary>把服务器上那份拉回本地缓存（登录、恢复会话之后调用）。</summary>
    private async Task RefreshSubjectsFromServerAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        var dto = await _account.GetSubjectsAsync().ConfigureAwait(true);
        if (dto is not null)
        {
            Post(RefreshSubjectRows);
        }
    }

    [RelayCommand]
    private async Task SaveSubjectsAsync()
    {
        var byClassroom = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in SubjectRows)
        {
            var subject = (row.Subject ?? string.Empty).Trim();
            if (subject.Length > 0)
            {
                byClassroom[row.Uuid] = subject;
            }
        }

        var error = await _account
            .SaveSubjectsAsync(DefaultSubject.Trim() is { Length: > 0 } value ? value : null, byClassroom)
            .ConfigureAwait(true);

        SubjectStatus = error ?? (byClassroom.Count > 0
            ? $"已保存：默认科目「{DefaultSubject.Trim()}」，另有 {byClassroom.Count} 个班单独指定。"
            : "已保存任教科目。");

        AddLog(SubjectStatus);
        RefreshSubjectRows();
        OnPropertyChanged(nameof(SubjectHintText));
    }

    // ======================== 已授权教室（控制台指派） ========================

    public bool HasAuthorizedClassrooms => AuthorizedClassrooms.Count > 0;

    /// <summary>刷新「管理员授权给我的教室」列表。</summary>
    [RelayCommand]
    private async Task RefreshAuthorizedAsync()
    {
        if (_relay is null || !IsSignedIn)
        {
            AuthorizedClassrooms.Clear();
            OnPropertyChanged(nameof(HasAuthorizedClassrooms));
            return;
        }

        var list = await _relay.GetAuthorizedClassroomsAsync().ConfigureAwait(true);

        AuthorizedClassrooms.Clear();
        foreach (var item in list)
        {
            AuthorizedClassrooms.Add(new ClassroomGrantItem(item, BindAuthorizedAsync));
        }

        OnPropertyChanged(nameof(HasAuthorizedClassrooms));
    }

    /// <summary>
    /// 一键绑定被授权的教室。
    /// 不传口令 —— 服务器查到管理员已授权就直接放行，
    /// 这样口令不必在老师之间转发，权限始终收在服务器上。
    /// </summary>
    private async Task BindAuthorizedAsync(ClassroomGrantItem item)
    {
        IsServerBusy = true;
        ServerError = null;

        try
        {
            if (_relay is null)
            {
                ServerError = "请先填写服务器地址并登录账号。";
                return;
            }

            // 不传口令：服务器查到管理员已授权就直接放行。
            // 绑定成功后同样会记进"已保存的教室"（口令为空，靠授权）。
            var (ok, error) = await ConnectAndBindAsync(item.Uuid, secret: null).ConfigureAwait(true);
            if (!ok)
            {
                ServerError = error;
                return;
            }

            ShowSnackbar($"已绑定教室「{ClassroomName}」");
            ActivePage = TeacherPage.Text;
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    // ======================== 内部 ========================

    /// <summary>局域网链路连上/断开时切换路由目标。连上服务器绑定时会覆盖这里的选择。</summary>
    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;

        if (connected)
        {
            // 局域网直连可用，优先走它 —— 同网段下延迟更低、也不占用公网带宽
            _transport.Active = _channel;
            ClassroomName = _channel.ClassroomName;
            NotifyLinkChanged();
        }
        else
        {
            if (ReferenceEquals(_transport.Active, _channel))
            {
                // 局域网断了：若还绑着服务器就回落到中继，否则彻底停发
                _transport.Active = _relayTransport;
            }

            if (_relayTransport is null)
            {
                ClassroomName = "未连接";
                IsClassroomMuted = false;
                Voice.ResetOnDisconnect();
            }

            else
            {
                // 回落到中继之后这条链路是通的，IsConnected 必须跟着回来。
                //
                // 原来它就停在方法开头那句 IsConnected = false 上了：
                // 老师从教室走到走廊（局域网断开），界面立刻显示"未连接"、
                // 喊话按钮全部禁用 —— 而服务器那边其实还绑着，
                // 喊话本来完全发得出去。用户看到的是"明明连上了服务器，却什么都按不动"。
                IsConnected = _relayTransport.IsConnected;
            }

            NotifyLinkChanged();

            // 断线时把还没发出去的喊话丢掉：留着它们会在重连后突然一起涌向教室，
            // 那时老师早就忘了自己喊过什么，教室里却一连接着念好几条旧内容。
            _shoutQueue.Clear();
            Text.RefreshQueueStatus();
        }
    }

    private RelayShoutTransport? _relayTransport;

    /// <summary>管理员授权给当前账号的教室。登录后自动刷新。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ClassroomGrantItem> AuthorizedClassrooms { get; } = [];

    private void HandleControlMessage(ShoutMessage message)
    {
        switch (message)
        {
            case StatusMessage status:
                _channel.ApplyStatus(status);
                IsClassroomMuted = status.Muted;
                ClassroomName = string.IsNullOrWhiteSpace(status.ClassroomName)
                    ? ClassroomName
                    : status.ClassroomName;
                break;

            case AckMessage ack:
                AddLog(ack.Ok ? $"教室端已确认：{ack.Detail}" : $"教室端拒绝：{ack.Detail}");
                break;

            case ErrorMessage error:
                ErrorMessage = $"教室端报错：{error.Message}";
                AddLog($"教室端报错：{error.Message}");
                break;
        }
    }

    // ======================== 公网中继（跨局域网） ========================

    /// <summary>
    /// 服务器地址输入框里正在编辑的文字。
    ///
    /// 与 <see cref="SavedServerUrl"/> 分开是有原因的，这个区分本身就是一处修复：
    /// 两者原本是一个值，于是"填地址"这件事只能靠点「绑定教室」来落地，
    /// 而绑定又要求先登录、登录读的却正是那个还没落地的值 ——
    /// 新装的机器上形成死锁：登录提示"请先填写服务器地址"（尽管框里刚填了），
    /// 而让地址生效的唯一按钮永远点不亮。
    ///
    /// 另注：属性上那两个 NotifyCanExecuteChangedFor 不能省。
    /// "填了地址按钮还是灰的"就是这么来的 —— 少了它们，输入时两个命令的 CanExecute
    /// 从不重算，按钮永远停在禁用态，而功能本身其实是好的。
    /// 教训记在这里：断言必须走 CanExecute，直接调 ExecuteAsync 会绕过这道闸门
    /// （DesignPreview 里的 VerifyServerAddressFlow 现在两条都验）。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveServerAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestServerCommand))]
    private string _serverUrl = string.Empty;

    /// <summary>
    /// 已保存、当前生效的服务器地址。登录、注册、绑定教室用的都是它。
    /// 只由 <see cref="SaveServerAddressAsync"/> 改写。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerConfigured))]
    [NotifyPropertyChangedFor(nameof(ServerAddressStatus))]
    [NotifyPropertyChangedFor(nameof(ServerAddressPill))]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    private string _savedServerUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerAddressError))]
    private string? _serverAddressError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerAddressNotice))]
    private string? _serverAddressNotice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveServerAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestServerCommand))]
    private bool _isServerAddressBusy;

    /// <summary>教室 UUID。绑定成功后会被记住，下次只需填口令。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    private string _bindUuid = string.Empty;

    /// <summary>教室口令。刻意不做持久化 —— 口令留在老师脑子里比留在磁盘上安全。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    private string _bindSecret = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerStatusText))]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnbindServerCommand))]
    private bool _isServerBound;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnbindServerCommand))]
    private bool _isServerBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    private string? _serverError;

    /// <summary>
    /// 已经绑定过的教室。点一下切过去，点右边的按钮可以把它从列表里移除。
    /// </summary>
    public ObservableCollection<SavedClassroomItem> SavedClassrooms { get; } = [];

    public bool HasServerError => !string.IsNullOrWhiteSpace(ServerError);

    public bool HasServerAddressError => !string.IsNullOrWhiteSpace(ServerAddressError);

    public bool HasServerAddressNotice => !string.IsNullOrWhiteSpace(ServerAddressNotice);

    public bool HasSavedClassrooms => SavedClassrooms.Count > 0;

    /// <summary>已保存教室那一段的说明文字。</summary>
    public string SavedClassroomsHint
    {
        get
        {
            if (IsSignedIn)
            {
                return "点一下即可切换过去，不用再输口令。";
            }

            // 未登录时：存了口令的教室照样能切（服务器只认口令），
            // 而靠管理员授权的那些必须先登录 —— 授权是挂在账号上的。
            return "点一下即可切换过去。存了口令的教室无需登录；靠管理员授权的教室要先登录。";
        }
    }

    /// <summary>
    /// 是否已经配置过服务器地址。
    ///
    /// 这是"跨局域网"这一整套功能的总开关：没配置时登录、注册都无从谈起，
    /// 绑定教室也不该可点 —— 三个入口都由它把关，提示语也指向上面那张卡片。
    /// </summary>
    public bool IsServerConfigured => !string.IsNullOrWhiteSpace(SavedServerUrl);

    public string ServerAddressStatus => IsServerConfigured
        ? $"当前生效：{SavedServerUrl}"
        : "尚未配置 —— 只用同一局域网内的教室时，不必配置";

    /// <summary>卡片右上角的小徽标：只表达"配没配"，具体地址另起一行写全。</summary>
    public string ServerAddressPill => IsServerConfigured ? "已配置" : "未配置";

    private bool CanSaveServerAddress => !IsServerAddressBusy && !string.IsNullOrWhiteSpace(ServerUrl);

    private bool CanTestServer => !IsServerAddressBusy && !string.IsNullOrWhiteSpace(ServerUrl);

    /// <summary>
    /// 保存服务器地址。
    ///
    /// 这是**独立的一步**：不要求登录，也不要求绑定教室。地址属于"这个账号在哪台服务器上"，
    /// 它的层级比"绑定了哪间教室"更高，也先于登录发生 —— 所以它不该藏在绑定卡片里，
    /// 更不该要靠绑定成功才写得进去。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveServerAddress))]
    private async Task SaveServerAddressAsync()
    {
        IsServerAddressBusy = true;
        ServerAddressError = null;
        ServerAddressNotice = null;

        try
        {
            if (!TryNormalizeServerUrl(ServerUrl, out var normalized, out var urlError))
            {
                ServerAddressError = urlError;
                return;
            }

            ServerUrl = normalized;

            if (string.Equals(normalized, SavedServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                PersistServerUrl(normalized);
                ServerAddressNotice = "地址没有变化。";
                return;
            }

            // 换服务器等于换了一整套账号与绑定关系：账号只存在于某一台服务器上。
            // 不把旧会话拆掉的话，界面会继续显示"已登录 某某"，而那个账号属于上一台服务器 ——
            // 之后所有操作都会以一个不存在的身份发出去。
            var hadSession = IsSignedIn || IsServerBound;

            if (hadSession)
            {
                await TearDownRelayAsync().ConfigureAwait(true);
                await _account.LogoutAsync().ConfigureAwait(true);
                IsServerBound = false;
                ClassroomName = string.Empty;
                NotifyLinkChanged();
                OnAccountChanged();
            }

            PersistServerUrl(normalized);

            ServerAddressNotice = hadSession
                ? "地址已保存。因为换了服务器，之前的登录与教室绑定都已解除。"
                : "地址已保存。接下来就可以登录或注册账号了。";

            AddLog(hadSession
                ? $"服务器地址改为 {normalized}，原有登录与绑定已解除。"
                : $"服务器地址已设为 {normalized}。");
        }
        finally
        {
            IsServerAddressBusy = false;
        }
    }

    /// <summary>
    /// 测试地址是否可达、对面是不是 ClassShout 服务器。
    ///
    /// 在还没有账号、也没有凭据的时候，这是唯一能确认"地址填对了"的办法 ——
    /// 否则老师只能靠"登录失败"去猜是自己填错了、还是口令错了、还是服务器没起来。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestServer))]
    private async Task TestServerAsync()
    {
        IsServerAddressBusy = true;
        ServerAddressError = null;
        ServerAddressNotice = null;

        try
        {
            if (!TryNormalizeServerUrl(ServerUrl, out var normalized, out var urlError))
            {
                ServerAddressError = urlError;
                return;
            }

            using var response = await _http
                .GetAsync($"{normalized}{RelayPaths.Health}", HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
            {
                ServerAddressError = $"能连上，但 {RelayPaths.Health} 返回 HTTP {(int)response.StatusCode}。"
                                     + "请确认这个地址指向的是 ClassShout 中继服务器。";
                return;
            }

            var health = await response.Content
                .ReadFromJsonAsync<RelayHealthDto>(RelayJsonOptions.Value)
                .ConfigureAwait(true);

            if (health is null || !health.Ok)
            {
                ServerAddressError = "服务器有响应，但自检结果不正常。";
                return;
            }

            ServerAddressNotice = $"连接正常：{health.Service} · 协议 {health.Protocol} · "
                                  + $"教室 {health.Classrooms} 间 · 账号 {health.Users} 个";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ServerAddressError = $"连不上：{DescribeException(ex)}";
        }
        finally
        {
            IsServerAddressBusy = false;
        }
    }

    /// <summary>
    /// 把异常说清楚。
    ///
    /// 只写 ex.Message 是不够的：.NET 在 Android 上把 Java 那层异常包了一层，
    /// 外层消息常常只有一句毫无信息量的 "Connection failure"，真正的原因
    /// （例如"明文 HTTP 被系统策略拦下"）在内层。排障最怕这种"有报错、但报错什么也没说"，
    /// 所以这里把内层一并带出来。
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return ReferenceEquals(inner, ex)
            ? $"{ex.GetType().Name}：{ex.Message}"
            : $"{ex.GetType().Name}：{ex.Message}（内层 {inner.GetType().Name}：{inner.Message}）";
    }

    private void PersistServerUrl(string normalized)
    {
        SavedServerUrl = normalized;
        _relaySettings.ServerUrl = normalized;

        if (!LocalSettings.SaveTeacher(_relaySettings))
        {
            ServerAddressError = "地址没能写入本机，重启后会恢复原样。";
        }
    }

    public string ServerStatusText => IsServerBound
        ? $"已绑定「{ClassroomName}」"
        : "未绑定教室";

    private bool CanBindServer
        => !IsServerBusy
           && !IsServerBound
           && IsSignedIn
           && IsServerConfigured
           && !string.IsNullOrWhiteSpace(BindUuid)
           && !string.IsNullOrWhiteSpace(BindSecret);

    private bool CanUnbindServer => !IsServerBusy && IsServerBound;

    /// <summary>连接到中继服务器并用 UUID + 口令绑定教室。</summary>
    [RelayCommand(CanExecute = nameof(CanBindServer))]
    private async Task BindServerAsync()
    {
        IsServerBusy = true;
        ServerError = null;

        try
        {
            if (!TryNormalizeServerUrl(SavedServerUrl, out var normalized, out _))
            {
                ServerError = "还没有配置服务器地址，请先在上面那张「中继服务器」卡片里填写并保存。";
                return;
            }

            // 绑定用的是**已保存**的地址。输入框里若有未保存的改动就明确拦下来 ——
            // 否则老师会以为"我刚改的地址生效了"，实际绑上去的却是上一台服务器。
            if (TryNormalizeServerUrl(ServerUrl, out var typed, out _) &&
                !string.Equals(typed, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ServerError = "服务器地址有未保存的修改，请先点「保存地址」，再回来绑定教室。";
                return;
            }

            var (ok, error) = await ConnectAndBindAsync(BindUuid.Trim(), BindSecret).ConfigureAwait(true);
            if (!ok)
            {
                ServerError = error;
                return;
            }

            // 绑定成功后清掉口令输入框：它已经存进"已保存的教室"里了，
            // 再留在屏幕上只是把它多摆一份在别人眼前。
            BindSecret = string.Empty;

            ShowSnackbar($"已绑定教室「{ClassroomName}」");
            ActivePage = TeacherPage.Text;
        }
        catch (Exception ex)
        {
            ServerError = $"绑定出错：{ex.Message}";
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    /// <summary>
    /// 连上服务器并绑定一间教室。三个入口共用这一条路：
    /// 手输 UUID 加口令、管理员授权的一键绑定、以及从"已保存的教室"里切过去。
    ///
    /// 抽出来不只是为了少写几行 —— 这三条路原来各写一遍，
    /// 于是"绑定成功之后要做什么"（记录、刷新列表、开始轮询、切界面）在每一条里
    /// 都可能漏掉一两件。切换教室这个新入口就是靠它才自动获得同样的收尾。
    /// </summary>
    private async Task<(bool Ok, string? Error)> ConnectAndBindAsync(string uuid, string? secret)
    {
        // 换服务器或换教室前先拆掉旧连接，避免出现两条并存的会话
        await TearDownRelayAsync().ConfigureAwait(true);

        _relay = new TeacherRelayClient(_http, _relaySettings);
        _relay.EventReceived += OnRelayEvent;
        _relay.ConnectionChanged += connected => Post(() => AddLog(connected ? "服务器状态通道已连接。" : "服务器状态通道已断开。"));
        _relay.Log += message => Post(() => AddLog(message));

        var (ok, error) = await _relay.BindAsync(uuid, secret ?? string.Empty, NameFor(uuid)).ConfigureAwait(true);
        if (!ok)
        {
            await TearDownRelayAsync().ConfigureAwait(true);
            return (false, error);
        }

        _relayTransport = new RelayShoutTransport(_relay);
        _transport.Active = _relayTransport;
        IsServerBound = true;
        IsConnected = true;
        ClassroomName = _relay.BoundClassroomName ?? "教室";

        BindUuid = _relay.BoundUuid ?? uuid;
        _relaySettings.LastUuid = BindUuid;

        // 记下这间教室（含服务器地址与口令），下次切回来就不必再找管理员要口令
        LocalSettings.SaveTeacher(_relaySettings);
        RefreshSavedClassrooms();

        _relay.StartPolling();
        return (true, null);
    }

    [RelayCommand(CanExecute = nameof(CanUnbindServer))]
    private async Task UnbindServerAsync()
    {
        IsServerBusy = true;

        try
        {
            await TearDownRelayAsync().ConfigureAwait(true);

            IsServerBound = false;
            IsConnected = _channel.IsConnected;
            ClassroomName = _channel.IsConnected ? _channel.ClassroomName : "未连接";
            RefreshSavedClassrooms();
            AddLog("已解除与教室的绑定。");
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    /// <summary>
    /// 切换到另一间已经绑定过的教室。
    ///
    /// 它和"解除绑定"是两件事：解绑之后什么都不连，而切换是"松手一间、握住另一间"——
    /// 老师下一节课走进另一个班，需要的是后者。
    /// </summary>
    private async Task SwitchClassroomAsync(SavedClassroomItem item)
    {
        IsServerBusy = true;
        ServerError = null;

        try
        {
            // 这间教室可能绑在另一台服务器上（换了学校，或者学校换了服务器）。
            // 地址必须在建立连接之前换好 —— TeacherRelayClient 是照着配置里的地址发请求的。
            if (!string.IsNullOrWhiteSpace(item.ServerUrl) &&
                !string.Equals(item.ServerUrl, SavedServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                PersistServerUrl(item.ServerUrl);
                AddLog($"已切换服务器地址：{item.ServerUrl}");
            }

            if (!IsServerConfigured)
            {
                ServerError = "这间教室没有留下服务器地址，请先在上面那张「中继服务器」卡片里填好。";
                return;
            }

            var (ok, error) = await ConnectAndBindAsync(item.Uuid, item.Secret).ConfigureAwait(true);
            if (!ok)
            {
                // 口令可能已经在教室那台机器上被换掉了。这是切换失败最常见的原因，
                // 所以提示要直接指向"重新填一次口令"，而不是把服务器的原话丢出来。
                ServerError = string.IsNullOrEmpty(item.Secret)
                    ? $"{error}这间教室此前是靠管理员授权绑定的；若授权已被取消，请改用口令绑定。"
                    : $"{error}口令可能已经变了，请在下方的输入框里重新填一次再绑定。";
                return;
            }

            ShowSnackbar($"已切换到教室「{ClassroomName}」");
            ActivePage = TeacherPage.Text;
        }
        catch (Exception ex)
        {
            ServerError = $"切换教室出错：{ex.Message}";
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    /// <summary>把一间教室从保存列表里移除（连同它的口令）。</summary>
    private void RemoveSavedClassroom(SavedClassroomItem item)
    {
        _relaySettings.RecentClassrooms.RemoveAll(
            record => string.Equals(record.Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase));

        LocalSettings.SaveTeacher(_relaySettings);
        RefreshSavedClassrooms();

        // 移除的正好是当前这间时，刻意**不去动连接**：
        // 老师可能正在这间教室里喊话，删掉一条记录不该把话筒也一起拿走。
        AddLog($"已从列表里移除教室「{item.Name}」，它的口令也已一并删除。");
    }

    /// <summary>把已保存的教室读进界面，并标出当前绑的是哪一间。</summary>
    private void RefreshSavedClassrooms()
    {
        SavedClassrooms.Clear();

        foreach (var record in _relaySettings.RecentClassrooms)
        {
            var isCurrent = IsServerBound
                            && string.Equals(record.Uuid, BindUuid, StringComparison.OrdinalIgnoreCase);

            SavedClassrooms.Add(new SavedClassroomItem(record, isCurrent, SwitchClassroomAsync, RemoveSavedClassroom));
        }

        // 文字页的"发给谁"用的就是这批数据，跟着一起刷新
        Text.SyncTargets(_relaySettings.RecentClassrooms, IsServerBound ? BindUuid : null);
        Text.TeacherName = TeacherName;

        OnPropertyChanged(nameof(HasSavedClassrooms));
        OnPropertyChanged(nameof(SavedClassroomsHint));
    }

    private async Task TearDownRelayAsync()
    {
        if (_relay is not null)
        {
            await _relay.DisposeAsync().ConfigureAwait(true);
            _relay = null;
        }

        _relayTransport = null;

        // 回落到局域网；如果局域网也没连，就成了空操作，正是期望的行为
        _transport.Active = _channel.IsConnected ? _channel : null;
        NotifyLinkChanged();
    }

    private void OnRelayEvent(RelayEnvelope envelope)
    {
        Post(() =>
        {
            // 中继不是当前链路时，它的状态事件一律不写界面。
            //
            // 老师可能一边绑着服务器上的 A 教室，一边走进 B 教室用局域网直连 ——
            // 这时中继仍连着、仍在推 A 教室的静音/音量/上下线，
            // 而那些消息会把界面改得和"正在喊话的那间教室"对不上：
            // 明明对着 B 喊，显示的却是 A 的静音状态。
            // 两条链路指向不同教室时，只有当前那条有资格说话。
            if (!ReferenceEquals(_transport.Active, _relayTransport))
            {
                return;
            }

            switch (envelope.Kind)
            {
                case RelayKinds.Status:
                    IsClassroomMuted = envelope.Muted;
                    break;

                case RelayKinds.ClassroomOnline:
                    AddLog($"教室「{envelope.From}」已上线。");
                    break;

                case RelayKinds.ClassroomOffline:
                    AddLog($"教室「{envelope.From}」已离线。");
                    break;
            }
        });
    }

    // ======================== 语音转文字 ========================
    //
    // 这一项已经整块搬到教室端（见 ClassroomViewModel 的同名区域）。
    //
    // 原因是它原来配错了地方：音频是流到教室那台电脑上才放出来的，
    // 而识别放在老师手机上，识别的是老师自己麦克风里的声音 ——
    // 教室里到底放出来什么、有没有听清，手机那边根本不知道。
    // 现在音频在哪落地、就在哪识别，教室的大字区直接当字幕用。
    //
    // 旧配置文件 teacher-stt.json 不再被读取，但也不去删：
    // 老师机器上那份可能还留着别的信息，静默删别人的文件不合适。

    /// <summary>规范化服务器地址：允许只填 host:port，自动补 http:// 并去掉末尾斜杠。</summary>
    private static bool TryNormalizeServerUrl(string input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "请填写中继服务器地址。";
            return false;
        }

        // 已经写了协议但不是 http / https 的，直接拒绝。
        //
        // 这里原本是一律补前缀，于是 "ftp://nope" 会变成 "http://ftp://nope" ——
        // 那居然是个语法合法的 URI（主机名 ftp，路径 //nope），于是被当成有效地址存了下来，
        // 直到登录时才以一个看不懂的错误暴露出来。宁可在这里就说清只支持哪两种。
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                error = "只支持 http:// 与 https:// 两种地址。";
                return false;
            }
        }
        else
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "地址格式不对，应形如 https://relay.example.com 或 http://192.168.1.10:8080";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "地址里没有主机名，应形如 https://relay.example.com 或 http://192.168.1.10:8080";
            return false;
        }

        normalized = text.TrimEnd('/');
        return true;
    }

    private void AddLog(string message)
    {
        Logs.Insert(0, new TeacherLogEntry(DateTime.Now, message));

        while (Logs.Count > 120)
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

    public async ValueTask DisposeAsync()
    {
        _snackbarCts?.Cancel();
        _snackbarCts?.Dispose();

        // 定时器要停掉：它内部持着一个按秒跑的 DispatcherTimer，
        // 应用退出后还留着只会让它在已经关掉的界面上继续排队。
        _scheduler.Dispose();

        Voice.Dispose();

        // 两条链路都要拆：可能只连了其中一个，也可能两个都在
        if (_relay is not null)
        {
            await _relay.DisposeAsync().ConfigureAwait(false);
            _relay = null;
        }

        _http.Dispose();

        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 名单里的一位学生。
///
/// 命令挂在条目自己身上（和别处一样），XAML 模板里就不必写父级转换绑定。
/// </summary>
public sealed class StudentRow
{
    public StudentRow(Student student, Action<StudentRow>? use = null)
    {
        Student = student;
        UseCommand = new RelayCommand(() => use?.Invoke(this));
    }

    public Student Student { get; }

    public string Name => Student.Name;

    /// <summary>喊话里用的完整标识：姓名（学号，简写，小组）。</summary>
    public string Label => Student.Label;

    public string StudentNoText => Student.HasStudentNo ? Student.StudentNo! : "—";

    public string ShortNameText => Student.HasShortName ? Student.ShortName! : "—";

    public string GroupText => Student.HasGroup ? Student.Group! : "—";

    public IRelayCommand UseCommand { get; }
}

/// <summary>
/// 一条「已排定的定时喊话」。
///
/// 命令挂在条目自己身上，XAML 模板里就不必写父级转换绑定。
///
/// 它同时代表两种来源：本机排的（<see cref="ScheduledShout"/>）与交给服务器的
/// （<see cref="ScheduledShoutDto"/>）。界面上合成一个列表 —— 老师只想知道
/// "我排了什么"，分成两处会让他两头找。
/// </summary>
public sealed class ScheduledShoutItem
{
    public ScheduledShoutItem(ScheduledShout record, Action<ScheduledShoutItem> cancel)
    {
        Record = record;
        CancelCommand = new RelayCommand(() => cancel(this));
    }

    private ScheduledShoutItem(ScheduledShoutDto server, IRelayCommand cancel)
    {
        Server = server;
        CancelCommand = cancel;
    }

    /// <summary>由服务器上的一条构造（取消命令用外壳那份，它要发请求）。</summary>
    public static ScheduledShoutItem FromServer(ScheduledShoutDto server, IRelayCommand cancel)
        => new(server, cancel);

    /// <summary>本机那条；服务器上的那条为 null。</summary>
    public ScheduledShout? Record { get; }

    /// <summary>服务器上那条；本机的为 null。</summary>
    public ScheduledShoutDto? Server { get; }

    public string TimeText => Server is { } server
        ? server.SendAt.ToLocalTime().ToString("MM-dd HH:mm")
        : Record!.TimeText;

    public string SummaryText => Server is { } server
        ? (string.Equals(server.Kind, ScheduledShoutKinds.Voice, StringComparison.OrdinalIgnoreCase)
            ? $"语音 {server.AudioSeconds:0.#} 秒"
            : Truncate(server.Text))
        : Record!.SummaryText;

    /// <summary>发给谁：把 UUID 换成教室名，找不到的说明记录已被移除。</summary>
    public string TargetText => Server is { } server
        ? (server.TargetNames.Count switch
        {
            0 => "没有目标班级",
            1 => server.TargetNames[0],
            _ => $"{server.TargetNames.Count} 个班级：{string.Join("、", server.TargetNames)}",
        })
        : Record!.TargetUuids.Count switch
        {
            0 => "当前绑定的教室",
            1 => "1 个班级",
            _ => $"{Record!.TargetUuids.Count} 个班级",
        };

    /// <summary>展示参数的摘要，和即时喊话用的是同一套档位。</summary>
    public string DisplayText => Server is { } server
        ? $"由服务器发送 · {ShoutFontSizes.Label(server.FontSize)}字 · {ShoutHoldDurations.Label(server.HoldMs)}"
        : $"{ShoutFontSizes.Label(Record!.FontSize)}字 · {ShoutHoldDurations.Label(Record!.HoldMs)}"
          + (Record!.Speak ? " · 朗读" : " · 不朗读")
          + (ScheduledShoutKinds.IsVoice(Record!.Kind) ? " · 语音" : string.Empty);

    public IRelayCommand CancelCommand { get; }

    private static string Truncate(string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "（空）" : text.Trim().ReplaceLineEndings(" ");
        return value.Length <= 24 ? value : value[..24] + "…";
    }
}

/// <summary>
/// 「某间教室用什么科目」的一行。
///
/// 内容可写：界面上一行就是一个输入框，留空表示这个班用默认科目。
/// </summary>
public sealed partial class SubjectRow : ObservableObject
{
    public SubjectRow(BoundClassroom classroom, string subject)
    {
        Uuid = classroom.Uuid;
        Name = classroom.Name;
        _subject = subject;
    }

    public string Uuid { get; }

    public string Name { get; }

    /// <summary>这间教室的科目。留空＝用默认科目。</summary>
    [ObservableProperty]
    private string _subject;
}

/// <summary>
/// 一条「保存下来的教室」。
///
/// 命令挂在条目自己身上，XAML 模板里就不必写父级转换绑定 ——
/// 和 <see cref="ClassroomGrantItem"/> 同一个套路。
/// </summary>
public sealed partial class SavedClassroomItem : ObservableObject
{
    public SavedClassroomItem(
        BoundClassroom record,
        bool isCurrent,
        Func<SavedClassroomItem, Task> switchTo,
        Action<SavedClassroomItem> remove)
    {
        Uuid = record.Uuid;
        Name = record.Name;
        ServerUrl = record.ServerUrl ?? string.Empty;
        Secret = record.Secret;
        HasSecret = !string.IsNullOrWhiteSpace(record.Secret);
        LastBoundText = record.LastBoundAt.ToLocalTime().ToString("MM-dd HH:mm");
        IsCurrent = isCurrent;

        SwitchCommand = new AsyncRelayCommand(() => switchTo(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Uuid { get; }

    public string Name { get; }

    /// <summary>绑定它时用的服务器地址。切换时若与当前不同，会先换地址。</summary>
    public string ServerUrl { get; }

    /// <summary>
    /// 存下来的口令，切换时交给绑定流程用。
    ///
    /// 刻意是 internal 而不是 public：界面不该有任何一个绑定去显示它 ——
    /// 这个值出现在屏幕上没有任何用处，只会多一份被旁人看到的机会。
    /// </summary>
    internal string? Secret { get; }

    /// <summary>有没有存下口令。没有就说明这间是靠管理员授权绑定的。</summary>
    public bool HasSecret { get; }

    public string LastBoundText { get; }

    /// <summary>
    /// 是不是当前正绑着的那一间。
    ///
    /// 可写（而不是只在构造时定死）是为了让渲染校验能把它摆出来 ——
    /// 否则那个「使用中」徽标只有真的连上服务器绑一次才会被画到，
    /// 而"排版有没有走样"这种问题恰恰要在图上才看得出来。
    /// </summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>副标题：说清这间教室凭什么能切过去，以及上次是什么时候用的。</summary>
    public string DetailText => $"{(HasSecret ? "已存口令" : "管理员授权")} · 上次绑定 {LastBoundText}";

    public IAsyncRelayCommand SwitchCommand { get; }

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>
/// 一条「管理员授权给我的教室」。
/// 命令挂在条目自己身上，XAML 模板里就不必写父级转换绑定。
/// </summary>
public sealed class ClassroomGrantItem
{
    public ClassroomGrantItem(AuthorizedClassroom source, Func<ClassroomGrantItem, Task> bind)
    {
        Uuid = source.Uuid;
        Name = source.Name;
        IsOnline = source.Online;
        LastSeenText = source.LastSeenAt.ToLocalTime().ToString("MM-dd HH:mm");
        BindCommand = new AsyncRelayCommand(() => bind(this));
    }

    public string Uuid { get; }

    public string Name { get; }

    /// <summary>该教室最近是否在线。离线仍可绑定，只是要等它上线才能喊话。</summary>
    public bool IsOnline { get; }

    public string LastSeenText { get; }

    public IAsyncRelayCommand BindCommand { get; }

    public string StatusText => IsOnline ? "在线" : $"最后在线 {LastSeenText}";
}
