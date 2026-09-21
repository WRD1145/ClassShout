using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ClassShout.Teacher.Diagnostics;

/// <summary>
/// 字体回退诊断页。
///
/// 为什么做成纵向列表而不是矩阵：手机屏幕窄，四列并排时每列只有几十像素，
/// 文字会溢出串行，反而看不清。每种「字体链 × 字重」独占一行才读得准。
///
/// 放在共享 UI 层而不是预览工具里，是因为这个问题**只在部分平台出现** ——
/// 桌面渲染一切正常，Android 上却可能整片变方块。
/// 能直接在真机上跑这一页，就不必靠"改代码、打包、安装、截图"反复试错。
/// </summary>
public partial class FontDiagnostics : UserControl
{
    /// <summary>待测的字体链。</summary>
    private static readonly (string Label, string? Chain)[] Chains =
    [
        ("不指定字体族（平台默认）", null),
        ("只写 Noto Sans CJK SC", "Noto Sans CJK SC"),
        ("只写 sans-serif", "sans-serif"),
        ("只写 Roboto", "Roboto"),
        ("设计系统当前字体链", "Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, Microsoft YaHei UI, Microsoft YaHei, PingFang SC, Hiragino Sans GB, sans-serif, Roboto"),
    ];

    private static readonly FontWeight[] Weights =
    [
        FontWeight.Normal,
        FontWeight.Medium,
        FontWeight.SemiBold,
        FontWeight.Bold,
    ];

    /// <summary>样本串混合中英文与数字：既能看出中文缺字形，也能确认拉丁字母是否正常。</summary>
    private const string Sample = "教师端 同学们请安静 Abc123";

    private bool _built;

    public FontDiagnostics()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 在进入可视树之后再构建内容。
    ///
    /// 这一点很容易踩坑：构造函数里控件还没挂到树上，FindResource 一律返回 null，
    /// 把 null 赋给 Foreground 会让文字**整片不可见** —— 诊断页自己先瞎掉。
    /// 放到 AttachedToVisualTree 里，资源查找才拿得到真实画刷。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_built)
        {
            return;
        }

        _built = true;
        BuildList();
    }

    private void BuildList()
    {
        var host = this.FindControl<ContentControl>("MatrixHost");
        if (host is null)
        {
            return;
        }

        var root = new StackPanel { Spacing = 4 };
        var mutedBrush = this.FindResource("Md3.OnSurfaceVariant") as IBrush;
        var accentBrush = this.FindResource("Md3.Primary") as IBrush;

        foreach (var (label, chain) in Chains)
        {
            root.Children.Add(new TextBlock
            {
                Text = $"—— {label} ——",
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 2),
                Foreground = accentBrush,
            });

            foreach (var weight in Weights)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

                row.Children.Add(new TextBlock
                {
                    Text = weight.ToString(),
                    FontSize = 10,
                    Width = 58,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = mutedBrush,
                });

                var sample = new TextBlock
                {
                    Text = Sample,
                    FontSize = 13,
                    FontWeight = weight,
                };

                if (chain is not null)
                {
                    sample.FontFamily = new FontFamily(chain);
                }

                row.Children.Add(sample);
                root.Children.Add(row);
            }
        }

        host.Content = root;
    }
}
