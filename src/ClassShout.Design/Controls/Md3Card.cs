using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ClassShout.Design.Controls;

/// <summary>
/// Material Design 3 卡片容器。
///
/// 三种变体（通过 Classes 指定，默认 elevated）：
///     &lt;controls:Md3Card&gt;...&lt;/controls:Md3Card&gt;          抬升卡片，有阴影
///     &lt;controls:Md3Card Classes="filled"&gt;...                 填充卡片，用容器色，无阴影
///     &lt;controls:Md3Card Classes="outlined"&gt;...               描边卡片，最轻量
///
/// 之所以做成独立类型而不是给 Border 加样式，是因为 ContentControl 是所有窗口和
/// 用户控件的基类，给 ContentControl 定义默认主题会波及它们。
/// </summary>
public class Md3Card : ContentControl
{
    public static readonly StyledProperty<Thickness> CardPaddingProperty =
        AvaloniaProperty.Register<Md3Card, Thickness>(nameof(CardPadding), new Thickness(16));

    static Md3Card()
    {
        AffectsMeasure<Md3Card>(CardPaddingProperty);
    }

    /// <summary>卡片内边距。独立于 ContentControl 的 Padding，避免与内容自身内边距互相干扰。</summary>
    public Thickness CardPadding
    {
        get => GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }
}
