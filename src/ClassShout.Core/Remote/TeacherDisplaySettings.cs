using ClassShout.Core.Protocol;

namespace ClassShout.Core.Remote;

/// <summary>
/// 教师端上次用过的那套展示参数。
///
/// 记住它是因为：老师一旦习惯了"特大字 + 常驻"（比如讲评试卷时），
/// 每节课都要重新选一遍是不能接受的。
///
/// 注意它和教室端那三项默认值的区别：这里是**这位老师上次用的**，
/// 存在他自己的设备上；教室端那份是"没人指定时用哪一档"，存在教室里那台机器上。
/// 两边各管各的 —— 一个班的默认不该被某位老师上一次的选择改掉。
/// </summary>
public sealed class TeacherDisplaySettings
{
    /// <summary>展示方式，取值见 <see cref="ShoutDisplayModes"/>。</summary>
    public string Display { get; set; } = ShoutDisplayModes.Window;

    /// <summary>字号档位，取值见 <see cref="ShoutFontSizes"/>。</summary>
    public string FontSize { get; set; } = ShoutFontSizes.Default;

    /// <summary>停留时长（毫秒），取值见 <see cref="ShoutHoldDurations"/>。</summary>
    public int HoldMs { get; set; } = ShoutHoldDurations.Default;

    /// <summary>是否朗读。默认朗读 —— 不勾是例外，不是常规。</summary>
    public bool Speak { get; set; } = true;

    /// <summary>把可能过时/手改坏的值收敛回可用的档位。</summary>
    public TeacherDisplaySettings Normalized() => new()
    {
        Display = ShoutDisplayModes.IsValid(Display) ? Display : ShoutDisplayModes.Window,
        FontSize = ShoutFontSizes.IsValid(FontSize) ? FontSize : ShoutFontSizes.Default,
        HoldMs = ShoutHoldDurations.IsSpecified(HoldMs) ? HoldMs : ShoutHoldDurations.Default,
        Speak = Speak,
    };
}
