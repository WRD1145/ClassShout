namespace ClassShout.Core.Remote;

/// <summary>主题明暗的三档。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统（默认）。系统换主题时界面跟着换。</summary>
    System = 0,

    /// <summary>始终浅色。</summary>
    Light = 1,

    /// <summary>始终深色。</summary>
    Dark = 2,
}

/// <summary>
/// 外观设置（个性化）。
///
/// 单独一个文件，不和教室 / 教师的联网配置混在一起：外观是纯粹的本机偏好，
/// 与身份、凭据、服务器地址的生命周期完全不同 —— 换台机器、清空登录状态
/// 都不该把老师挑好的配色一起带走或一起弄丢。
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>
    /// 主题种子色，形如 #RRGGBB。
    /// 为空表示使用内置基线配色（等同于"没有自定义过"）。
    /// </summary>
    public string? SeedColor { get; set; }

    /// <summary>是否做过自定义。界面据此决定选中哪张色卡。</summary>
    public bool IsCustomized => !string.IsNullOrWhiteSpace(SeedColor);

    /// <summary>
    /// 明暗：跟随系统 / 浅色 / 深色。
    ///
    /// 默认跟随系统。早先两端把明暗写死在 App.axaml 里（`RequestedThemeVariant="Light"`），
    /// 当时的理由是教室投影与手机强光下浅色更稳；但"写死"意味着晚上用深色模式的人
    /// 无论怎么调系统都改不动它。默认跟随系统、想固定浅色的人自己选一次，比替他决定好。
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>收拾手改坏的值（JSON 里可能写了 3、-1，或者旧版本根本没有这个字段）。</summary>
    public AppearanceSettings Normalized() => new()
    {
        SeedColor = string.IsNullOrWhiteSpace(SeedColor) ? null : SeedColor.Trim(),
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeMode.System,
    };
}

/// <summary>
/// 三档主题与界面的对应关系。
///
/// 单独抽出来是为了能被自检直接断言：这里错一个映射，表现是"选深色没反应"
/// 或者"选了跟随系统却一直是深色"，而这两种错都得靠人工点开界面才发现。
/// </summary>
public static class ThemeModeMapping
{
    /// <summary>三档的显示名。</summary>
    public static string Label(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.Dark => "深色",
        _ => "跟随系统",
    };

    /// <summary>
    /// 这一档要不要交给系统决定。
    /// "跟随系统"在 Avalonia 里对应 <c>ThemeVariant.Default</c>，不是 Light/Dark —— 混了就等于写死。
    /// </summary>
    public static bool FollowsSystem(ThemeMode mode) => mode == ThemeMode.System;

    /// <summary>下一档（界面上那颗"切换"按钮用，三档循环）。</summary>
    public static ThemeMode Next(ThemeMode mode) => mode switch
    {
        ThemeMode.System => ThemeMode.Light,
        ThemeMode.Light => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    /// <summary>三档排好序给下拉框用。</summary>
    public static IReadOnlyList<ThemeMode> All { get; } = [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];
}
