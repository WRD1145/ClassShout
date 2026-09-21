using ClassShout.Core.Remote;

namespace ClassShout.Classroom.Services;

/// <summary>弹窗出现的位置。</summary>
public enum NotificationCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 置顶档位。
///
/// 关于第三档：Windows 存在"窗口段"机制，普通置顶窗口盖不住更高窗口段的东西
/// （开始菜单、任务管理器、通知中心等）。要盖住它们必须持有 UIAccess 令牌，
/// 而 Windows 要求 UIAccess 程序**必须数字签名且安装在安全目录**，
/// 否则清单里的 uiAccess="true" 会让程序直接无法启动。
/// 所以在未签名的构建里，第三档实现为"周期性重申置顶"——
/// 它比普通置顶强（能抢过其他后设的置顶窗口），但盖不住上述系统窗口。
/// 详见 README 的说明。
/// </summary>
public enum TopmostMode
{
    /// <summary>不置顶：可能被其他窗口盖住。</summary>
    None,

    /// <summary>普通置顶：设置 Topmost，被更高窗口段的窗口盖住时无法夺回。</summary>
    Normal,

    /// <summary>强制置顶：在普通置顶之上周期性重申，尽量夺回最前。</summary>
    Forced,
}

/// <summary>下拉选项：把枚举包上中文标签，避免界面直接显示枚举英文名。</summary>
/// <param name="Value">实际取值。</param>
/// <param name="Label">界面显示文本。</param>
public sealed record CornerOption(NotificationCorner Value, string Label);

/// <inheritdoc cref="CornerOption" />
public sealed record TopmostOption(TopmostMode Value, string Label);

/// <summary>教室端弹窗与显示的配置。</summary>
public sealed class ClassroomNotificationSettings
{
    /// <summary>是否启用弹窗。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>弹窗出现的位置。</summary>
    public NotificationCorner Corner { get; set; } = NotificationCorner.TopRight;

    /// <summary>自动消失的秒数。0 表示不自动消失。</summary>
    public int DurationSeconds { get; set; } = 8;

    /// <summary>置顶档位。</summary>
    public TopmostMode Topmost { get; set; } = TopmostMode.Forced;

    /// <summary>弹窗宽度（像素）。</summary>
    public double Width { get; set; } = 400;

    /// <summary>距离屏幕边缘的留白。</summary>
    public double Margin { get; set; } = 20;

    public static ClassroomNotificationSettings Load()
        => LocalSettings.Load("classroom-notification.json", static () => new ClassroomNotificationSettings());

    public bool Save() => LocalSettings.Save("classroom-notification.json", this);
}
