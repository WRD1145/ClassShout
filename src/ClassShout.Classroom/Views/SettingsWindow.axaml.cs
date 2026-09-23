using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;
using ClassShout.Design.Controls;
using ClassShout.Design.Theming;

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
    public SettingsWindow()
    {
        InitializeComponent();

        // 把界面状态与已保存的设置对齐，再接上回调 ——
        // 顺序反了的话，初始化赋值本身会被当成"用户改了颜色"而触发一次多余的保存。
        var theme = this.FindControl<ThemePicker>("Appearance");
        if (theme is not null)
        {
            theme.SelectedSeedHex = LocalSettings.LoadAppearance().SeedColor;
            theme.SelectionChanged += OnAppearanceChanged;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 换主题色：先存、再当场生效。
    ///
    /// 顺序是"先存后应用"：万一应用这一步出问题，磁盘上的设置仍与界面上选中的一致，
    /// 重启后不会出现"显示的是新颜色、实际用的是旧颜色"这种对不上的状态。
    /// </summary>
    private void OnAppearanceChanged(object? sender, string? seedHex)
    {
        var settings = LocalSettings.LoadAppearance();
        settings.SeedColor = seedHex;

        if (!LocalSettings.SaveAppearance(settings))
        {
            // 保存失败不该静默：老师会以为选好了，下次打开又是旧的
            (sender as ThemePicker)?.ShowPersistenceFailure("外观设置没能写入本机，重启后会恢复原样。");
        }

        if (Application.Current is { } app && !Md3Appearance.Apply(app, seedHex))
        {
            // 控件自己已经校验过一次，能走到这里说明两处判断不一致 ——
            // 属于代码缺陷，明确抛出来，别让它伪装成"用户输入有误"。
            throw new InvalidOperationException(
                $"外观控件认定合法、但配色应用器拒绝了「{seedHex}」，两处校验逻辑不一致。");
        }
    }
}
