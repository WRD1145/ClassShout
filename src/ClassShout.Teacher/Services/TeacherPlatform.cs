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

    /// <summary>创建一个录音器实例。</summary>
    public static IAudioRecorder CreateAudioRecorder()
        => _recorderFactory?.Invoke()
           ?? throw new InvalidOperationException(
               "尚未注册麦克风采集实现。平台头需要在启动时调用 TeacherPlatform.RegisterAudioRecorder。");
}
