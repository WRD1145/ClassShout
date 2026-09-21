using Avalonia;
using Avalonia.Media;

namespace ClassShout.Design;

/// <summary>
/// 设计系统的平台适配。
///
/// 目前只有一件事：在 Android 上把强调字重从 Medium 降为 Normal。
///
/// 背景（实测数据，不是推测）：在 Android 16 / x86_64 模拟器上做过对照实验，
/// 5 种字体链 × 4 档字重的结果高度一致 ——
///   · FontWeight.Normal：中文正常显示；
///   · Medium / SemiBold / Bold：中文全部渲染成方块，而同一行里的拉丁字母（Abc123）正常。
/// 也就是说 Android 上 Avalonia 的逐字回退只覆盖 Normal 字重。
/// 换字体族无效（含只写 Noto Sans CJK SC、只写 sans-serif、只写 Roboto，表现完全相同），
/// 所以只能从字重入手。
///
/// 为什么用运行时覆盖资源，而不是 XAML 的 {OnPlatform}：
/// 平台条件语法写在 Setter 的 Value 里只会得到一个字符串，
/// 不会转换成 FontWeight，运行时直接抛 InvalidCastException 把应用搞崩。
/// </summary>
public static class Md3Typography
{
    /// <summary>强调字重的资源键，XAML 中以 DynamicResource 引用。</summary>
    public const string EmphasisWeightKey = "Md3.Weight.Emphasis";

    /// <summary>
    /// 在创建任何视图之前调用一次。
    ///
    /// 必须早于视图构造：各控件主题用 DynamicResource 引用这个键，
    /// 值一旦确定就会缓存到控件上，之后再改就来不及了。
    /// </summary>
    public static void ApplyPlatformDefaults(Application application)
    {
        if (OperatingSystem.IsAndroid())
        {
            application.Resources[EmphasisWeightKey] = FontWeight.Normal;
        }
    }
}
