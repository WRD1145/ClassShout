using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using ClassShout.Core.Remote;
using ClassShout.Teacher.Services;

namespace ClassShout.Teacher.Android.Services;

/// <summary>托管同一条中继轮询；页面退出不停止接收，进程重建后恢复原会话游标。</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeRemoteMessaging)]
public sealed class ClassroomReceiverService : Service
{
    public sealed class ReceiverState
    {
        public TeacherReceiverSession? Session { get; set; }
    }
    private const string StateFile = "android-receiver.json";
    private const int ForegroundId = 4202;
    private static TeacherRelayClient? _client;
    private static HttpClient? _ownedHttp;
    private static bool _restored;
    private TeacherRelayClient? _observed;

    public static void Register(Context context)
    {
        var appContext = context.ApplicationContext ?? context;
        TeacherPlatform.RegisterRelayReceiver(client => Attach(appContext, client), () => Stop(appContext));
    }

    private static bool Attach(Context context, TeacherRelayClient client)
    {
        // 连接动作发生在前台，由此启动服务，避免 Android 12+ 禁止后台随意启动服务。
        Stop(context);
        _client = client;
        _restored = false;
        SaveSession();
        try
        {
            var intent = new Intent(context, typeof(ClassroomReceiverService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ClassShout", $"前台接收服务启动失败：{ex.Message}");
            _client = null;
            LocalSettings.Save(StateFile, new ReceiverState());
            TeacherPlatform.Notify("后台接收未启动", "请保持应用在前台，并检查系统后台运行权限。");
            return false;
        }
    }

    private static void Stop(Context context)
    {
        var previous = _client;
        var ownedHttp = _ownedHttp;
        var owned = _restored;
        _client = null;
        _ownedHttp = null;
        _restored = false;
        LocalSettings.Save(StateFile, new ReceiverState());
        context.StopService(new Intent(context, typeof(ClassroomReceiverService)));
        // UI 持有的客户端由外壳统一释放，服务只释放自己恢复的那份。
        if (owned && previous is not null) _ = DisposeRestoredAsync(previous, ownedHttp);
    }

    private static async Task DisposeRestoredAsync(TeacherRelayClient client, HttpClient? http)
    {
        try { await client.DisposeAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("ClassShout", $"关闭接收会话失败：{ex.Message}"); }
        finally { http?.Dispose(); }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidNotifier.EnsureChannel(this);
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, AndroidNotifier.ChannelId) : new Notification.Builder(this);
        if (launch is not null)
            builder.SetContentIntent(PendingIntent.GetActivity(this, 0, launch,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
        var notification = builder.SetContentTitle("ClassShout")
            .SetContentText("ClassShout 正在接收教室消息")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true).SetOnlyAlertOnce(true).Build();
        // Android 14+ 明确声明远程消息类型；无需录音权限，也没有 dataSync 的每日时限。
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(ForegroundId, notification, ForegroundService.TypeRemoteMessaging);
        else StartForeground(ForegroundId, notification);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (_client is null)
        {
            var saved = LocalSettings.Load(StateFile, () => new ReceiverState()).Session;
            if (saved is null) { StopSelf(); return StartCommandResult.NotSticky; }
            var settings = LocalSettings.LoadTeacher();
            // 退出登录或换服务器后，禁止恢复旧账号的接收令牌。
            if (!string.Equals(saved.ServerUrl.TrimEnd('/'), settings.ServerUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                StopSelf();
                return StartCommandResult.NotSticky;
            }
            TlsPolicy.AllowUntrusted = settings.AllowUntrustedTls;
            _ownedHttp = TlsPolicy.CreateClient(TimeSpan.FromSeconds(60));
            _client = new TeacherRelayClient(_ownedHttp, settings);
            _restored = true;
            Observe(_client);
            _client.RestoreReceiverSession(saved);
            global::Android.Util.Log.Info("ClassShout", "已恢复前台接收会话与轮询游标。");
        }
        else
        {
            Observe(_client);
            _client.StartPolling();
        }
        SaveSession();
        global::Android.Util.Log.Info("ClassShout", "前台接收服务已运行，使用唯一中继轮询。");
        return StartCommandResult.Sticky;
    }

    private void Observe(TeacherRelayClient client)
    {
        if (ReferenceEquals(_observed, client)) return;
        if (_observed is not null)
        {
            _observed.EventReceived -= OnEvent;
            _observed.Log -= OnLog;
        }
        _observed = client;
        client.EventReceived += OnEvent;
        client.Log += OnLog;
    }

    private void OnLog(string message)
    {
        global::Android.Util.Log.Info("ClassShout", message);
        if (ReferenceEquals(_client, _observed) && _observed is { IsBound: false })
            new Handler(Looper.MainLooper!).Post(() =>
            {
                AndroidNotifier.Show(this, "接收会话已失效", "请重新打开教师端并绑定教室。");
                Stop(this);
            });
    }

    private void OnEvent(RelayEnvelope envelope)
    {
        SaveSession();
        // 页面还在时沿用外壳的通知逻辑；仅在独立恢复会话时由服务直接通知。
        if (_restored && envelope.Kind == RelayKinds.Reply)
            AndroidNotifier.Show(this, $"{envelope.From ?? "教室"}回复", envelope.Text ?? "收到一条教室回复");
        if (_restored && envelope.Kind == RelayKinds.Status && envelope.State == "scheduledShout")
            AndroidNotifier.Show(this, "定时喊话", envelope.Text ?? "定时任务已处理。");
    }

    private static void SaveSession()
    {
        var session = _client?.ExportReceiverSession();
        if (session is not null && !LocalSettings.Save(StateFile, new ReceiverState { Session = session }))
            global::Android.Util.Log.Warn("ClassShout", "接收会话保存失败，进程被回收后将无法恢复。");
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        if (_observed is not null)
        {
            _observed.EventReceived -= OnEvent;
            _observed.Log -= OnLog;
        }
        _observed = null;
        base.OnDestroy();
    }
}
