using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ClassShout.Design.Diagnostics;

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
    /// <summary>随程序集分发的字体。单独列一行，用来确认它自己是否可用。</summary>
    public const string BundledFamily = "avares://ClassShout.Design/Assets/Fonts#HarmonyOS Sans SC";

    /// <summary>
    /// 对照组字体链。
    ///
    /// 保留这几条是有来历的：当初为查"Android 上中文变方块"，正是靠它们证明
    /// **换字体族解决不了**（六种选择表现完全一致），问题出在字重上。
    /// 留着它们，将来换平台或换 Avalonia 版本时可以原样再跑一遍这个对照。
    /// </summary>
    private static readonly (string Label, string? Chain)[] ControlChains =
    [
        ("不指定字体族（平台默认）", null),
        ("对照：只写 sans-serif", "sans-serif"),
        ("对照：只写 Noto Sans CJK SC", "Noto Sans CJK SC"),
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

        // 「设计系统当前字体链」直接取令牌本身，而不是把它抄一份写死在上面。
        //
        // 抄一份的代价当场就发生了：字体链已改成以内置字体打头，
        // 而这里还留着旧的那条 —— 诊断页测的于是是一条已经不存在的字体链，
        // 一边报"一切正常"、一边和真实界面毫无关系。诊断工具必须测真东西。
        var designFamily = this.FindResource("Md3.FontFamily") as FontFamily;

        var chains = new List<(string Label, FontFamily? Family)>
        {
            ("内置字体单独测（HarmonyOS Sans SC）", new FontFamily(BundledFamily)),
            ($"设计系统当前字体链（令牌值）", designFamily),
        };

        foreach (var (label, chain) in ControlChains)
        {
            chains.Add((label, chain is null ? null : new FontFamily(chain)));
        }

        foreach (var (label, family) in chains)
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

                // null 表示"不指定"，让文字继承上层字体链（即平台默认那一行）
                if (family is not null)
                {
                    sample.FontFamily = family;
                }

                row.Children.Add(sample);
                root.Children.Add(row);
            }
        }

        host.Content = root;
    }
}
