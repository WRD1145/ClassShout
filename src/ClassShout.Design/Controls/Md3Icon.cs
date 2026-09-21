using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Material.Icons;

namespace ClassShout.Design.Controls;

/// <summary>
/// Material 图标控件。
///
/// 直接使用 <c>Material.Icons</c> 内置的路径数据（13637 个图标），
/// 按 MD3 规范的 24×24 网格缩放绘制，因此任意 Size 下都保持正确比例。
/// 解析后的 Geometry 会按图标种类缓存，避免同一图标反复解析字符串。
///
/// 用法：&lt;controls:Md3Icon Kind="Send" Size="24" /&gt;
/// </summary>
public class Md3Icon : Control
{
    /// <summary>MDI 图标设计网格边长，所有图标路径都按这个尺寸绘制。</summary>
    private const double DesignGrid = 24d;

    private static readonly ConcurrentDictionary<MaterialIconKind, Geometry?> GeometryCache = new();

    public static readonly StyledProperty<MaterialIconKind> KindProperty =
        AvaloniaProperty.Register<Md3Icon, MaterialIconKind>(nameof(Kind), MaterialIconKind.Circle);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Md3Icon, double>(nameof(Size), 24d);

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<Md3Icon, IBrush?>(nameof(IconBrush));

    static Md3Icon()
    {
        AffectsRender<Md3Icon>(KindProperty, IconBrushProperty);
        AffectsMeasure<Md3Icon>(SizeProperty);
    }

    /// <summary>图标种类。</summary>
    public MaterialIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>图标边长（dp）。</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>图标颜色；不设时回退到 <see cref="Control.Foreground"/> 的继承值。</summary>
    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Size;
        return new Size(size, size);
    }

    public override void Render(DrawingContext context)
    {
        var geometry = GeometryCache.GetOrAdd(Kind, static kind =>
        {
            try
            {
                var data = MaterialIconDataProvider.GetData(kind);
                return string.IsNullOrWhiteSpace(data) ? null : Geometry.Parse(data);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                // 个别图标的路径数据可能无法解析，画不出来总好过整页崩掉。
                return null;
            }
        });

        if (geometry is null || Size <= 0)
        {
            return;
        }

        // 取色优先级：显式设置 > 继承到的文字色 > 弱化的表面色。
        // 继承这一层很关键 —— 放在按钮、导航项里的图标能自动跟随其前景色，
        // 不必给每个图标单独指定颜色。
        var brush = IconBrush
                    ?? this.GetValue(TextElement.ForegroundProperty) as IBrush
                    ?? this.FindResource("Md3.OnSurfaceVariant") as IBrush
                    ?? Brushes.Gray;

        var scale = Size / DesignGrid;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
