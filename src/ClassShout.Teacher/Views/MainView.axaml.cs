using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;
using ClassShout.Design.Controls;
using ClassShout.Design.Theming;
using ClassShout.Teacher.ViewModels;

namespace ClassShout.Teacher.Views;

/// <summary>
/// 教师端主视图。
/// 桌面头把它装进手机比例的窗口，Android 头把它作为单视图根节点，
/// 两边跑的是同一份界面。
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // 先对齐界面状态，再接回调 —— 反过来的话，初始化赋值会被当成"用户改了颜色"，
        // 白白触发一次保存。
        var theme = this.FindControl<ThemePicker>("Appearance");
        if (theme is not null)
        {
            theme.SelectedSeedHex = LocalSettings.LoadAppearance().SeedColor;
            theme.SelectionChanged += OnAppearanceChanged;
        }

        var about = this.FindControl<AboutPanel>("About");
        if (about is not null)
        {
            about.AppName = "ClassShout 教师端";
            about.DiagnosticsProvider = BuildDiagnostics;
        }
    }

    /// <summary>
    /// 「复制诊断信息」的文本。
    ///
    /// 只放排障时一定会被问到的几项，且**不放任何凭据**：
    /// 服务器地址与绑定教室是排障必需的，但登录令牌一概不进这里 ——
    /// 这份文本的用途是贴到别处给人看，写进去就等于泄露。
    /// </summary>
    private string BuildDiagnostics()
    {
        if (DataContext is not TeacherShellViewModel vm)
        {
            return "（界面尚未挂上视图模型）";
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"服务器地址 ：{(vm.IsServerConfigured ? vm.SavedServerUrl : "（未配置）")}");
        text.AppendLine($"账号       ：{(vm.IsSignedIn ? vm.SignedInName : "未登录")}");
        text.AppendLine($"绑定教室   ：{(vm.IsServerBound ? vm.ClassroomName : "未绑定")}");
        text.AppendLine($"服务器状态 ：{vm.ServerStatusText}");
        text.AppendLine($"链路       ：{vm.ActiveLinkText}");
        text.AppendLine($"配置目录   ：{LocalSettings.Directory}");
        return text.ToString();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 换主题色：先存、再当场生效。
    ///
    /// 先存后应用：万一应用出问题，磁盘上的设置仍与界面选中的一致，
    /// 重启后不会出现"界面显示新颜色、实际用旧颜色"的错位。
    /// 这套顺序与教室端的设置窗口一致，两端的表现应当完全一样。
    /// </summary>
    private void OnAppearanceChanged(object? sender, string? seedHex)
    {
        var settings = LocalSettings.LoadAppearance();
        settings.SeedColor = seedHex;

        if (!LocalSettings.SaveAppearance(settings))
        {
            (sender as ThemePicker)?.ShowPersistenceFailure("外观设置没能写入本机，重启后会恢复原样。");
        }

        if (Application.Current is { } app && !Md3Appearance.Apply(app, seedHex))
        {
            // 控件已校验过一次，能走到这里说明两处判断不一致，属于代码缺陷，
            // 明确抛出来而不是伪装成"用户输入有误"。
            throw new InvalidOperationException(
                $"外观控件认定合法、但配色应用器拒绝了「{seedHex}」，两处校验逻辑不一致。");
        }
    }
}
