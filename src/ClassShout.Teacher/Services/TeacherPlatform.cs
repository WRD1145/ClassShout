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

    /// <summary>创建一个录音器实例。</summary>
    public static IAudioRecorder CreateAudioRecorder()
        => _recorderFactory?.Invoke()
           ?? throw new InvalidOperationException(
               "尚未注册麦克风采集实现。平台头需要在启动时调用 TeacherPlatform.RegisterAudioRecorder。");
}
