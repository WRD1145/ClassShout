using ClassShout.Core.Remote;

namespace ClassShout.Classroom.Services;

/// <summary>
/// "本次进入设置已通过验证"这个状态。
///
/// 为什么放在内存里、而且是个静态字段：
///   · 放进配置文件就等于永久解锁 —— 那样这道锁只剩下一个装饰作用；
///   · 它只在"这一次打开设置窗口"期间有效，窗口一关就重新上锁。
///
/// 用户要的是"改设置时不用每一项都验一遍"，不是"验一次就永远放行"。
/// 这两件事差别很大，实现上前者只需要一个进程内的布尔值。
/// </summary>
public static class SettingsAccess
{
    private static bool _unlocked;

    /// <summary>现在能不能直接进设置。没启用 PIN 时永远可以。</summary>
    public static bool IsUnlocked => _unlocked || !SettingsLock.IsEnabled;

    /// <summary>验证通过，本次进入放行。</summary>
    public static void Unlock() => _unlocked = true;

    /// <summary>重新上锁。设置窗口关闭时调用。</summary>
    public static void Lock() => _unlocked = false;
}