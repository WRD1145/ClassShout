using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace ClassShout.Design.Controls;

/// <summary>
/// 装诊断页的小窗口。
///
/// 诊断页（如字体回退矩阵）在教室里那块屏幕上直接铺开并不合适，
/// 但排障时又必须能就地打开 —— 尤其是手机端：问题只在真机上出现，
/// 而"改常量、重新打包、装机"这条路走不通。所以给一个能随时弹出的小窗。
/// </summary>
public sealed class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(string title, Control content, double width = 1180, double height = 520)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new TextBlock
        {
            Text = title,
            FontSize = 15,
            Margin = new Avalonia.Thickness(16, 14, 16, 10),
        };
        header.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurface"));

        var body = new ScrollViewer { Content = content, Padding = new Avalonia.Thickness(0, 0, 0, 12) };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);

        Content = root;
        Background = Brushes.Transparent;

        // 用当前主题的表面色：诊断页本身也是界面的一部分，不该在换主题后显得突兀
        this.Bind(BackgroundProperty, new DynamicResourceExtension("Md3.Surface"));
    }
}
