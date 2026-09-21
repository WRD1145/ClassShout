using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClassShout.Teacher.Views;

/// <summary>
/// 教师端主视图。
/// 桌面头把它装进手机比例的窗口，Android 头把它作为单视图根节点，
/// 两边跑的是同一份界面。
/// </summary>
public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
