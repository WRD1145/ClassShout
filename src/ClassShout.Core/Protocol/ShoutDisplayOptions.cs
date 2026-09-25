namespace ClassShout.Core.Protocol;

/// <summary>
/// 一次喊话在教室里怎么展示。
///
/// 这几个选项都是**发送方**选的：同一条"同学们看黑板"，
/// 班主任可能想让它占满整块屏（窗口），而科任老师只想要一条角落提示（弹窗）。
/// 教室端只提供默认值，用来兜住没选的情况。
/// </summary>
public static class ShoutDisplayModes
{
    /// <summary>教室端主界面的大字区 —— 学生抬头就能看到的那块屏。</summary>
    public const string Window = "window";

    /// <summary>屏幕边缘的悬浮弹窗，不占用主界面。</summary>
    public const string Popup = "popup";

    public static readonly string[] All = [Window, Popup];

    public static bool IsValid(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0;
}

/// <summary>文字大小档位。</summary>
public static class ShoutFontSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string ExtraLarge = "xlarge";

    public const string Default = Medium;

    public static readonly string[] All = [Small, Medium, Large, ExtraLarge];

    public static bool IsValid(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0;

    /// <summary>档位对应的中文说明，界面上直接显示。</summary>
    public static string Label(string? value) => value switch
    {
        Small => "小",
        Large => "大",
        ExtraLarge => "特大",
        _ => "中",
    };

    /// <summary>
    /// 档位对应的像素字号。
    ///
    /// 教室端有两处要显示喊话文字：主界面的大字区（学生抬头看的那块屏）和屏幕边缘的弹窗。
    /// 同一档在弹窗里必须小得多 —— 弹窗只占屏幕一角，用大字区的尺寸会直接溢出屏幕。
    /// 所以映射放在这里、由两个界面共用，"同一档在两处看起来是不是一回事"就不再靠人记。
    /// </summary>
    /// <param name="value">档位。</param>
    /// <param name="popup">true 取弹窗用的小号尺寸，false 取大字区用的尺寸。</param>
    public static double ToPixels(string? value, bool popup) => (value, popup) switch
    {
        (Small, true) => 15,
        (Small, false) => 28,
        (Large, true) => 21,
        (Large, false) => 64,
        (ExtraLarge, true) => 26,
        (ExtraLarge, false) => 92,
        (_, true) => 17,
        (_, false) => 44,
    };
}

/// <summary>文字/图片在教室里停留多久。</summary>
public static class ShoutHoldDurations
{
    public const int TenSeconds = 10_000;
    public const int TwentySeconds = 20_000;
    public const int ThirtySeconds = 30_000;
    public const int OneMinute = 60_000;

    /// <summary>常驻：不自动消失，直到下一条喊话把它顶掉、或者有人手动停止。</summary>
    public const int Forever = 0;

    /// <summary>没有明确指定时用这个值（默认 20 秒，和教室端弹窗的默认停留一致）。</summary>
    public const int Default = TwentySeconds;

    /// <summary>
    /// 表示"这次没指定，用教室端的默认值"。
    ///
    /// 用一个负数而不是 0 来承担这个含义，是因为 0 已经被"常驻"占用了 ——
    /// 两者混起来的话，"没选停留时间"会变成"永久留在屏幕上"，
    /// 而这是教室里最不希望发生的默认行为。
    /// </summary>
    public const int Unspecified = -1;

    public static readonly int[] All = [TenSeconds, TwentySeconds, ThirtySeconds, OneMinute, Forever];

    /// <summary>
    /// 这是不是一个**明确指定**的停留时长。
    ///
    /// <see cref="Unspecified"/>（负数）不算：它表示"这次没选，用教室端的默认值"。
    /// 而 <see cref="Forever"/>（0）算 —— 这两者的区别正是"常驻"和"没选"的区别，
    /// 混起来的话，没选停留时间会变成永久留在屏幕上。
    /// </summary>
    public static bool IsSpecified(int milliseconds)
        => milliseconds == Forever || (milliseconds >= 1000 && milliseconds <= 3600_000);

    /// <summary>把毫秒说成人话，界面上直接显示。</summary>
    public static string Label(int milliseconds) => milliseconds switch
    {
        Unspecified => "默认",
        Forever => "常驻",
        < 60_000 => $"{milliseconds / 1000} 秒",
        _ => milliseconds % 60_000 == 0 ? $"{milliseconds / 60_000} 分钟" : $"{milliseconds / 60_000.0:0.#} 分钟",
    };
}

/// <summary>
/// 教室端向教师端声明自己会哪些"新"能力。
///
/// 存在的理由：这套协议是两端独立升级的。教室里那台电脑可能还跑着旧版本，
/// 而教师端发出去的图片、展示参数它根本读不懂 —— 读不懂不会有任何报错，
/// 只会"喊了但教室里没反应"，这类故障极难归因。
/// 所以教室端在握手的回包里报一下能力，教师端据此把用不了的选项禁掉。
///
/// 旧教室端不发这个字段，教师端按"只有基础能力"处理，行为与加这个字段之前完全一致。
/// </summary>
public static class ClassroomCapabilities
{
    /// <summary>能收图片喊话（分片传输 + 拼装显示）。</summary>
    public const string Image = "image";

    /// <summary>能按发送方指定的展示方式 / 字号 / 停留时长来呈现。</summary>
    public const string Display = "display";

    /// <summary>本版本教室端会的全部能力。</summary>
    public static readonly string[] Current = [Image, Display];

    public static bool Has(IReadOnlyList<string>? capabilities, string capability)
        => capabilities is not null && capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

/// <summary>教室端为"发送方没指定"准备的那套兜底值。</summary>
/// <param name="Display">默认展示方式。</param>
/// <param name="FontSize">默认字号档位。</param>
/// <param name="HoldMs">默认停留时长（毫秒）。</param>
public readonly record struct ShoutDisplayDefaults(string Display, string FontSize, int HoldMs)
{
    /// <summary>出厂默认：窗口展示、中号字、停留 20 秒。</summary>
    public static ShoutDisplayDefaults Standard =>
        new(ShoutDisplayModes.Window, ShoutFontSizes.Default, ShoutHoldDurations.Default);
}

/// <summary>一次喊话最终生效的展示参数。</summary>
/// <param name="Display">展示方式。</param>
/// <param name="FontSize">字号档位。</param>
/// <param name="HoldMs">停留时长（毫秒），0 表示常驻。</param>
/// <param name="Speak">是否朗读。</param>
public readonly record struct ShoutDisplayPlan(string Display, string FontSize, int HoldMs, bool Speak)
{
    public bool IsWindow => Display == ShoutDisplayModes.Window;

    public bool IsPopup => Display == ShoutDisplayModes.Popup;

    /// <summary>是不是常驻（不自动消失）。</summary>
    public bool IsForever => HoldMs == ShoutHoldDurations.Forever;

    /// <summary>自动消失的时长；常驻时为 <c>null</c>。</summary>
    public TimeSpan? Hold => IsForever ? null : TimeSpan.FromMilliseconds(HoldMs);

    /// <summary>
    /// 把"发送方指定的（可能缺省）+ 教室端的默认值"合成这次真正生效的一套参数。
    ///
    /// 抽成纯函数是为了能断言：这里每一个分支错了，表现都是"教室里显示得不对"，
    /// 而显示得不对不会有任何报错 —— 只有人站在教室里才发现。
    /// </summary>
    public static ShoutDisplayPlan Resolve(
        string? requestedDisplay,
        string? requestedFontSize,
        int requestedHoldMs,
        bool speak,
        ShoutDisplayDefaults defaults)
    {
        var display = ShoutDisplayModes.IsValid(requestedDisplay) ? requestedDisplay! : defaults.Display;

        var fontSize = ShoutFontSizes.IsValid(requestedFontSize) ? requestedFontSize! : defaults.FontSize;

        // 停留时长只有两种"没指定"的写法：负数（协议里的 Unspecified）和非法值。
        // 0 是合法值（常驻），绝不能被当成缺省。
        var holdMs = ShoutHoldDurations.IsSpecified(requestedHoldMs) ? requestedHoldMs : defaults.HoldMs;

        return new ShoutDisplayPlan(display, fontSize, holdMs, speak);
    }
}
