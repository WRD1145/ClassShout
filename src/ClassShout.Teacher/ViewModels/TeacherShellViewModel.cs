using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>底部导航的三个页面。</summary>
public enum TeacherPage
{
    Text,
    Voice,
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
    private readonly TeacherRelaySettings _relaySettings;

    private readonly AccountClient _account;

    private TeacherRelayClient? _relay;
    private CancellationTokenSource? _snackbarCts;

    public TeacherShellViewModel()
    {
        _relaySettings = LocalSettings.LoadTeacher();
        ServerUrl = _relaySettings.ServerUrl ?? string.Empty;
        BindUuid = _relaySettings.LastUuid ?? string.Empty;

        // 默认走局域网；连上服务器并绑定后再切过去
        _transport.Active = _channel;

        Text = new TextShoutViewModel(_transport);
        Voice = new VoiceShoutViewModel(_transport);

        _channel.Log += message => Post(() => AddLog(message));
        _channel.ConnectionChanged += connected => Post(() => OnConnectionChanged(connected));
        _channel.MessageReceived += message => Post(() => HandleControlMessage(message));

        TeacherName = TeacherPlatform.DeviceName;

        _account = new AccountClient(_http, _relaySettings);

        // 用本地令牌尝试恢复登录；失败也只是回到未登录，不阻塞界面
        _ = RestoreSessionAsync();
    }

    public TextShoutViewModel Text { get; }

    public VoiceShoutViewModel Voice { get; }

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
    [NotifyPropertyChangedFor(nameof(IsTextPage), nameof(IsVoicePage), nameof(IsDevicesPage))]
    private TeacherPage _activePage = TeacherPage.Text;

    public bool IsTextPage => ActivePage == TeacherPage.Text;

    public bool IsVoicePage => ActivePage == TeacherPage.Voice;

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
            ct => _channel.ConnectAsync(item.Announcement, TeacherName, ct),
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
            ct => _channel.ConnectAsync(endpoint, TeacherName, ct),
            $"正在连接 {endpoint}…",
            endpoint.ToString()).ConfigureAwait(true);
    }

    private async Task ConnectCoreAsync(
        Func<CancellationToken, Task> connect,
        string progressMessage,
        string displayName)
    {
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
            var (ok, error) = await _account
                .RegisterAsync(RegisterUsername, RegisterEmail, RegisterDisplayName, RegisterPassword)
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

        // 已登录就用账号里的姓名；未登录回退到设备名，保证局域网直连仍可用
        TeacherName = IsSignedIn ? SignedInName : TeacherPlatform.DeviceName;

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

            // 已登录时姓名以账号为准，这里的第三个参数只是兜底值
            var (ok, error) = await _relay
                .BindAsync(item.Uuid, secret: string.Empty, TeacherName)
                .ConfigureAwait(true);

            if (!ok)
            {
                ServerError = error;
                return;
            }

            _relayTransport = new RelayShoutTransport(_relay);
            _transport.Active = _relayTransport;
            IsServerBound = true;
            IsConnected = true;
            ClassroomName = _relay.BoundClassroomName ?? item.Name;
            BindUuid = item.Uuid;
            _relaySettings.LastUuid = item.Uuid;
            LocalSettings.SaveTeacher(_relaySettings);

            _relay.StartPolling();
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

            NotifyLinkChanged();
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

    /// <summary>中继服务器地址。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindServerCommand))]
    private string _serverUrl = string.Empty;

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

    /// <summary>最近绑定过的教室，点一下即可填入 UUID。</summary>
    public ObservableCollection<BoundClassroom> RecentClassrooms { get; } = [];

    public bool HasServerError => !string.IsNullOrWhiteSpace(ServerError);

    public bool HasRecentClassrooms => RecentClassrooms.Count > 0;

    public string ServerStatusText => IsServerBound
        ? $"已绑定「{ClassroomName}」"
        : "未绑定教室";

    private bool CanBindServer
        => !IsServerBusy
           && !IsServerBound
           && IsSignedIn
           && !string.IsNullOrWhiteSpace(ServerUrl)
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
            if (!TryNormalizeServerUrl(ServerUrl, out var normalized, out var urlError))
            {
                ServerError = urlError;
                return;
            }

            ServerUrl = normalized;
            _relaySettings.ServerUrl = normalized;

            // 换服务器或换教室前先拆掉旧连接，避免出现两条并存的会话
            await TearDownRelayAsync().ConfigureAwait(true);

            _relay = new TeacherRelayClient(_http, _relaySettings);
            _relay.EventReceived += OnRelayEvent;
            _relay.ConnectionChanged += connected => Post(() => AddLog(connected ? "服务器状态通道已连接。" : "服务器状态通道已断开。"));
            _relay.Log += message => Post(() => AddLog(message));

            var (ok, error) = await _relay.BindAsync(BindUuid.Trim(), BindSecret, TeacherName).ConfigureAwait(true);
            if (!ok)
            {
                ServerError = error;
                await TearDownRelayAsync().ConfigureAwait(true);
                return;
            }

            _relayTransport = new RelayShoutTransport(_relay);
            _transport.Active = _relayTransport;
            IsServerBound = true;

            ClassroomName = _relay.BoundClassroomName ?? "教室";
            IsConnected = true;
            NotifyLinkChanged();

            // 绑定成功后清掉口令输入框，并记住这台教室
            BindSecret = string.Empty;
            BindUuid = _relay.BoundUuid ?? BindUuid;
            _relaySettings.LastUuid = BindUuid;
            LocalSettings.SaveTeacher(_relaySettings);

            RefreshRecentClassrooms();

            _relay.StartPolling();
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
            AddLog("已解除与教室的绑定。");
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    /// <summary>点击历史记录填入 UUID。</summary>
    [RelayCommand]
    private void UseRecentClassroom(BoundClassroom? item)
    {
        if (item is not null)
        {
            BindUuid = item.Uuid;
            ServerError = null;
        }
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

    private void RefreshRecentClassrooms()
    {
        RecentClassrooms.Clear();
        foreach (var item in _relaySettings.RecentClassrooms)
        {
            RecentClassrooms.Add(item);
        }

        OnPropertyChanged(nameof(HasRecentClassrooms));
    }

    private void OnRelayEvent(RelayEnvelope envelope)
    {
        Post(() =>
        {
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