using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace ClassShout.Design.Theming;

/// <summary>
/// 把用户选定的种子色装到应用资源上，并支持随时换。
///
/// 做法是在 <see cref="Application.Resources"/> 的 MergedDictionaries 里放一块自己的字典，
/// 里面按亮 / 暗两套给出全部色角色。选这个位置是因为：
///   · 应用层资源的查找优先级高于样式层，所以能压过 Themes/Tokens/Color.axaml 的默认值；
///   · MergedDictionaries 的增删会触发 ResourcesChanged，所有 DynamicResource 绑定
///     当场重算 —— 换色不需要重启，也不必手工去刷新任何控件。
///
/// 这里刻意不缓存"当前配色"以外的任何东西：换色就是"把旧的那块拿掉、把新的放上去"，
/// 没有增量修改，也就不存在"改了一半"的中间状态。
/// </summary>
public static class Md3Appearance
{
    private static ResourceDictionary? _installed;

    /// <summary>当前生效的自定义种子色（#RRGGBB）；使用内置基线配色时为 null。</summary>
    public static string? CurrentSeedHex { get; private set; }

    /// <summary>
    /// 应用配色。
    /// </summary>
    /// <param name="application">目标应用。</param>
    /// <param name="seedHex">
    /// 种子色（#RRGGBB 或 #AARRGGBB）。传 null / 空 / 内置默认色都表示恢复内置基线配色。
    /// </param>
    /// <returns>false 表示给的字符串不是合法颜色，此时**不会**改动任何东西。</returns>
    public static bool Apply(Application application, string? seedHex)
    {
        string? normalized = null;

        if (!string.IsNullOrWhiteSpace(seedHex))
        {
            if (!Md3Palette.TryParseSeed(seedHex, out var parsed))
            {
                // 解析失败就原样保留现状，让调用方去提示 ——
                // 静默忽略会让用户以为自己选了、其实没生效。
                return false;
            }

            var hex = Md3Palette.ToHex(parsed);
            if (!string.Equals(hex, Md3Palette.DefaultSeedHex, StringComparison.OrdinalIgnoreCase))
            {
                normalized = hex;
            }
        }

        if (_installed is not null)
        {
            application.Resources.MergedDictionaries.Remove(_installed);
            _installed = null;
        }

        if (normalized is null)
        {
            CurrentSeedHex = null;
            return true;
        }

        Md3Palette.TryParseSeed(normalized, out var seed);

        var themeDictionaries = new ResourceDictionary
        {
            ThemeDictionaries =
            {
                [ThemeVariant.Light] = ToResourceDictionary(Md3Palette.Build(seed, dark: false)),
                [ThemeVariant.Dark] = ToResourceDictionary(Md3Palette.Build(seed, dark: true)),
            },
        };

        application.Resources.MergedDictionaries.Add(themeDictionaries);

        _installed = themeDictionaries;
        CurrentSeedHex = normalized;
        return true;
    }

    /// <summary>
    /// 色角色转成资源项。
    ///
    /// Md3.* 一律用不可变画刷：它们会被大量控件共享，做成可变画刷的话，
    /// 任何一处不小心改到颜色都会波及整个界面，而且这种问题极难定位。
    /// SystemAccentColor 那几个是给 Fluent 用的，必须是 Color 而不是画刷。
    /// </summary>
    private static ResourceDictionary ToResourceDictionary(IReadOnlyDictionary<string, Color> roles)
    {
        var dictionary = new ResourceDictionary();

        foreach (var (key, color) in roles)
        {
            dictionary[key] = key.StartsWith("SystemAccentColor", StringComparison.Ordinal)
                ? color
                : new ImmutableSolidColorBrush(color);
        }

        return dictionary;
    }
}
