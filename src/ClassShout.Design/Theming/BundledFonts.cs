using Avalonia;
using Avalonia.Media;

namespace ClassShout.Design.Theming;

/// <summary>在创建首个窗口前提供默认字体，避免依赖系统默认字体与字体缓存。</summary>
public static class BundledFonts
{
    public const string Family = "avares://ClassShout.Design/Assets/Fonts#HarmonyOS Sans SC";

    public static AppBuilder WithClassShoutFonts(this AppBuilder builder)
        => builder.With(new FontManagerOptions { DefaultFamilyName = Family });
}
