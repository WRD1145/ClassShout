using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ClassShout.Classroom.Views;

/// <summary>
/// 教室端设置窗口。
///
/// 单开一个窗口而不是在主界面占一列：教室电脑那块屏幕是给学生看大字用的，
/// 一排滑块和文本框摆在那儿既挤占了展示区，也容易被顺手改掉。
/// 设置是"配置一次"的事，独立成窗更符合它的使用频率。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}