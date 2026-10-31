using ClassShout.Core.Audio;

namespace ClassShout.Teacher.Services;

/// <summary>
/// 平台差异的注入点。
///
/// 为什么用这种最朴素的方式而不是 DI 容器：整个方案只有"麦克风采集"一处平台差异，
/// 为此引入一个容器和一套注册流程不划算。各平台头在启动时塞一个工厂进来即可，
/// 共享 UI 层保持对具体平台的零引用。
/// </summary>
public static class TeacherPlatform
{
    private static Func<ClassShout.Core.Remote.TeacherRelayClient, bool>? _relayReceiver;
    private static Action? _stopRelayReceiver;
    public static void RegisterRelayReceiver(Func<ClassShout.Core.Remote.TeacherRelayClient, bool> start, Action stop)
    {
        _relayReceiver = start;
        _stopRelayReceiver = stop;
    }
    public static bool HostRelayReceiver(ClassShout.Core.Remote.TeacherRelayClient client) => _relayReceiver?.Invoke(client) ?? false;
    public static void StopRelayReceiver() => _stopRelayReceiver?.Invoke();
    private static Func<IAudioRecorder>? _recorderFactory;

    /// <summary>本机名称，用于在教室端显示"谁在喊话"。</summary>
    public static string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>是否已经由平台头注册过录音实现。</summary>
    public static bool HasRecorder => _recorderFactory is not null;

    /// <summary>由平台头调用，注册麦克风采集实现。</summary>
    public static void RegisterAudioRecorder(Func<IAudioRecorder> factory) => _recorderFactory = factory;

    /// <summary>
    /// 应用进入后台。
    ///
    /// Android 上切到后台、锁屏、被系统回收时都不会通知 UI 层，
    /// 于是录音会一直跑着占住麦克风 —— 用户回到前台看到的还是"正在录"，
    /// 而这段时间里采集到的音频早就被当成一次正常喊话发出去了。
    /// 平台头在 OnStop 里调到，共享 UI 层自己决定怎么收尾。
    /// </summary>
    public static event Action? Backgrounded;

    /// <summary>由平台头调用，通知"应用进入后台"。</summary>
    public static void NotifyBackgrounded() => Backgrounded?.Invoke();

    /// <summary>
    /// 收到一条分享链接（用户点了 <c>classshout://claim?...</c>）。
    ///
    /// 平台头在"应用刚被这条链接启动"和"应用已在前台、又点了一条链接"两种情况下都要调到 ——
    /// 后者尤其容易漏：Android 上这时不会新建 Activity，只走 OnNewIntent，
    /// 而 Windows 上会新起一个进程（单实例逻辑再把它转给已有窗口）。
    /// </summary>
    public static event Action<string>? ShareLinkReceived;

    /// <summary>由平台头调用，把一条分享链接交给界面层。</summary>
    public static void NotifyShareLink(string link) => ShareLinkReceived?.Invoke(link);

    /// <summary>
    /// 从命令行参数里挑出分享链接。
    ///
    /// 系统把协议链接作为普通参数交给进程，所以这里只是"哪个参数长得像链接"。
    /// 单独做成公开方法是为了能测：Windows 上"点链接没反应"最常见的两个原因
    /// （参数被引号包着、或者系统额外塞了别的参数）都在这里被处理掉。
    /// </summary>
    public static string? FindShareLink(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var raw in args)
        {
            // 系统给的参数可能带引号（路径里含空格时尤其常见），先剥掉再判
            var arg = raw?.Trim().Trim('"') ?? string.Empty;

            if (arg.StartsWith($"{Core.Remote.ShareLink.Scheme}://", StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }

        return null;
    }

    // ======================== 系统通知（收到教室回复时） ========================

    private static Action<string, string>? _notifier;

    /// <summary>本平台能不能弹系统通知。不能时界面只留应用内提示。</summary>
    public static bool HasNotifier => _notifier is not null;

    /// <summary>
    /// 由平台头调用，注册"弹一条系统通知"的实现。
    ///
    /// 为什么放在平台层：安卓要用 NotificationManager（还要一条渠道 + 通知权限），
    /// Windows 要走托盘气泡，Linux 上是 notify-send —— 三件事没有一处是共通的，
    /// 而"什么时候该通知"（收到哪条回复）是视图模型的事。分开之后，
    /// 视图模型只说"提醒一下老师"，不关心底下是哪种弹法。
    /// </summary>
    public static void RegisterNotifier(Action<string, string> notifier) => _notifier = notifier;

    /// <summary>
    /// 弹一条系统通知。
    ///
    /// 失败不抛出：通知发不出去只是"少提醒一次"，而它由收到回复触发 ——
    /// 为了一次通知失败把消息处理链路打断，就成了"少一条回复"。
    /// </summary>
    public static void Notify(string title, string body)
    {
        try
        {
            _notifier?.Invoke(title, body);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 通知失败就算了：应用内还有一条提示
        }
    }

    // ======================== 播放一段 WAV（语音回复） ========================

    private static Action<byte[]>? _wavPlayer;

    /// <summary>本平台能不能直接播一段 WAV。</summary>
    public static bool HasWavPlayer => _wavPlayer is not null;

    /// <summary>由平台头调用，注册"把这段 WAV 放出来"的实现。</summary>
    public static void RegisterWavPlayer(Action<byte[]> player) => _wavPlayer = player;

    /// <summary>
    /// 播一段 WAV。
    ///
    /// 与通知同理放在平台层：桌面端是 NAudio，安卓端是 AudioTrack（喂 PCM），
    /// Linux 上是 aplay —— 上层只关心"这段语音能不能听"。
    /// </summary>
    public static void PlayWav(byte[] wav)
    {
        if (_wavPlayer is null)
        {
            throw new NotSupportedException("这个平台还没有注册 WAV 播放实现。");
        }

        _wavPlayer(wav);
    }

    /// <summary>创建一个录音器实例。</summary>
    public static IAudioRecorder CreateAudioRecorder()
        => _recorderFactory?.Invoke()
           ?? throw new InvalidOperationException(
               "尚未注册麦克风采集实现。平台头需要在启动时调用 TeacherPlatform.RegisterAudioRecorder。");

    // ======================== 选文件（导入名单） ========================

    private static Func<Task<RosterFilePickResult?>>? _rosterFilePicker;

    /// <summary>本平台能不能弹出"选文件"。桌面上是文件选择器，安卓上是 SAF。</summary>
    public static bool HasRosterFilePicker => _rosterFilePicker is not null;

    /// <summary>由平台头调用，注册"选一个名单文件"的实现。</summary>
    public static void RegisterRosterFilePicker(Func<Task<RosterFilePickResult?>> picker)
        => _rosterFilePicker = picker;

    /// <summary>
    /// 让老师挑一个名单文件。返回 null 表示用户取消了 —— 取消不是错误，界面不该弹提示。
    ///
    /// 为什么放在平台层：桌面端要用窗口的 StorageProvider，安卓端要走 SAF 的
    /// ActivityResult —— 这两件事都只有平台头做得了，而"选中之后怎么读这份文件"
    /// 是共享逻辑（见 <see cref="Core.Remote.RosterFile"/>）。
    /// </summary>
    public static async Task<RosterFilePickResult?> PickRosterFileAsync()
    {
        if (_rosterFilePicker is null)
        {
            return null;
        }

        return await _rosterFilePicker().ConfigureAwait(true);
    }
}

/// <summary>选中的名单文件：名字（决定怎么读）与内容。</summary>
/// <param name="FileName">文件名，例如 <c>三年二班.xlsx</c>。</param>
/// <param name="Content">内容流。</param>
public readonly record struct RosterFilePickResult(string FileName, Stream Content);
