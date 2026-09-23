using Avalonia.Media;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;

namespace ClassShout.Design.Theming;

/// <summary>
/// 由用户选定的一个种子色，展开成整套 MD3 色角色。
///
/// 用的是 Google material-color-utilities 的 C# 移植，也就是 Material You 自己那套算法：
/// 种子色先进 HCT 色彩空间（色相 / 彩度 / 音调），再由各色调色板按规范取音调。
/// 这样得到的配色对比度天然达标，不必手调 —— 手写一套近似算法最容易出的问题
/// 恰恰是某些色相下"主色上的文字"对比度不够，而那种问题只在用户选到特定颜色时才出现。
///
/// 为什么默认不生成：
/// 内置基线配色（种子 #6750A4）的数值取自 Material Theme Builder，
/// 与"用算法从同一个种子算一遍"**并不完全相等** —— 算法会把彩度规整到风格允许的范围。
/// 默认走手工基线，好处是设计稿与实现逐像素可校验（DesignPreview 就是这么做的）；
/// 只有用户真的选了颜色，才切到算法生成的那一套。
/// </summary>
public static class Md3Palette
{
    /// <summary>内置基线配色的种子色。选回它就等于"恢复默认"。</summary>
    public const string DefaultSeedHex = "#6750A4";

    /// <summary>可作为预设的种子色。挑的是色相分散、且各自在白底上不刺眼的几个。</summary>
    public static readonly (string Label, string Hex)[] Presets =
    [
        ("紫（默认）", DefaultSeedHex),
        ("靛蓝", "#3F51B5"),
        ("青", "#00696D"),
        ("绿", "#2E6B3E"),
        ("琥珀", "#8A5200"),
        ("红", "#A03A32"),
        ("品红", "#9C3F6E"),
        ("石墨", "#4A4A52"),
    ];

    /// <summary>
    /// 解析 #RRGGBB / #AARRGGBB（# 可省略）。失败返回 false，调用方据此提示而不是静默忽略。
    /// </summary>
    public static bool TryParseSeed(string? text, out uint argb)
    {
        argb = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('#');

        // 只写 6 位时补上不透明的 alpha
        if (value.Length == 6)
        {
            value = "FF" + value;
        }

        if (value.Length != 8 || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
        {
            return false;
        }

        argb = parsed;
        return true;
    }

    /// <summary>规格化成界面显示用的 #RRGGBB（丢掉 alpha，种子色始终视为不透明）。</summary>
    public static string ToHex(uint argb) => $"#{argb & 0x00FFFFFF:X6}";

    /// <summary>
    /// 生成一整套色角色。键名与 Themes/Tokens/Color.axaml 完全一致，
    /// 因此可以整块覆盖到应用资源上而不必改动任何一段 XAML。
    /// </summary>
    /// <param name="seed">种子色（0xAARRGGBB）。</param>
    /// <param name="dark">true 生成暗色方案。</param>
    public static IReadOnlyDictionary<string, Color> Build(uint seed, bool dark)
    {
        var core = CorePalette.Of(seed);

        Scheme<uint> scheme = dark
            ? new DarkSchemeMapper().Map(core)
            : new LightSchemeMapper().Map(core);

        var map = new Dictionary<string, Color>(StringComparer.Ordinal);

        void Add(string role, uint argb) => map["Md3." + role] = Color.FromUInt32(argb);

        Add("Primary", scheme.Primary);
        Add("OnPrimary", scheme.OnPrimary);
        Add("PrimaryContainer", scheme.PrimaryContainer);
        Add("OnPrimaryContainer", scheme.OnPrimaryContainer);

        Add("Secondary", scheme.Secondary);
        Add("OnSecondary", scheme.OnSecondary);
        Add("SecondaryContainer", scheme.SecondaryContainer);
        Add("OnSecondaryContainer", scheme.OnSecondaryContainer);

        Add("Tertiary", scheme.Tertiary);
        Add("OnTertiary", scheme.OnTertiary);
        Add("TertiaryContainer", scheme.TertiaryContainer);
        Add("OnTertiaryContainer", scheme.OnTertiaryContainer);

        Add("Error", scheme.Error);
        Add("OnError", scheme.OnError);
        Add("ErrorContainer", scheme.ErrorContainer);
        Add("OnErrorContainer", scheme.OnErrorContainer);

        Add("Surface", scheme.Surface);
        Add("OnSurface", scheme.OnSurface);
        Add("SurfaceVariant", scheme.SurfaceVariant);
        Add("OnSurfaceVariant", scheme.OnSurfaceVariant);
        Add("Background", scheme.Background);
        Add("OnBackground", scheme.OnBackground);

        Add("SurfaceDim", scheme.SurfaceDim);
        Add("SurfaceBright", scheme.SurfaceBright);
        Add("SurfaceContainerLowest", scheme.SurfaceContainerLowest);
        Add("SurfaceContainerLow", scheme.SurfaceContainerLow);
        Add("SurfaceContainer", scheme.SurfaceContainer);
        Add("SurfaceContainerHigh", scheme.SurfaceContainerHigh);
        Add("SurfaceContainerHighest", scheme.SurfaceContainerHighest);

        Add("Outline", scheme.Outline);
        Add("OutlineVariant", scheme.OutlineVariant);

        Add("InverseSurface", scheme.InverseSurface);
        Add("InverseOnSurface", scheme.InverseOnSurface);
        Add("InversePrimary", scheme.InversePrimary);

        // Fixed 系列：MD3 规范要求它在亮暗两套里**取值相同**（不随亮暗反转），
        // 所以这里两套都按亮色的音调取，与 Color.axaml 里的手工基线保持一致。
        // 库的 Scheme 不含这几个角色，映射按规范直接写死，出处见 m3.material.io 的 tone 表。
        Add("PrimaryFixed", core.Primary[90]);
        Add("PrimaryFixedDim", core.Primary[80]);
        Add("OnPrimaryFixed", core.Primary[10]);
        Add("OnPrimaryFixedVariant", core.Primary[30]);

        Add("SecondaryFixed", core.Secondary[90]);
        Add("SecondaryFixedDim", core.Secondary[80]);
        Add("OnSecondaryFixed", core.Secondary[10]);
        Add("OnSecondaryFixedVariant", core.Secondary[30]);

        Add("TertiaryFixed", core.Tertiary[90]);
        Add("TertiaryFixedDim", core.Tertiary[80]);
        Add("OnTertiaryFixed", core.Tertiary[10]);
        Add("OnTertiaryFixedVariant", core.Tertiary[30]);

        // Scrim / Shadow 不随种子色变化，沿用 Color.axaml 里的定义，这里不覆盖。

        // 把 Avalonia/Fluent 的强调色也挪到新主色上，否则滑块、开关、滚动条
        // 这些没被我们重做模板的控件还是原来的紫色，和界面其余部分对不上。
        var primary = core.Primary;
        map["SystemAccentColor"] = Color.FromUInt32(dark ? primary[80] : primary[40]);

        var accentSteps = dark
            ? new[] { primary[90], primary[95], primary[99], primary[70], primary[60], primary[50] }
            : new[] { primary[50], primary[60], primary[70], primary[30], primary[20], primary[10] };

        map["SystemAccentColorLight1"] = Color.FromUInt32(accentSteps[0]);
        map["SystemAccentColorLight2"] = Color.FromUInt32(accentSteps[1]);
        map["SystemAccentColorLight3"] = Color.FromUInt32(accentSteps[2]);
        map["SystemAccentColorDark1"] = Color.FromUInt32(accentSteps[3]);
        map["SystemAccentColorDark2"] = Color.FromUInt32(accentSteps[4]);
        map["SystemAccentColorDark3"] = Color.FromUInt32(accentSteps[5]);

        return map;
    }
}
