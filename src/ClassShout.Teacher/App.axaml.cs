using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;
using ClassShout.Teacher.ViewModels;
using ClassShout.Teacher.Views;

namespace ClassShout.Teacher;

/// <summary>
/// 教师端应用。桌面头与 Android 头共用这个类，
/// 两者的差异只体现在下面走的是哪条生命周期分支。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 排查字体问题时把这里改成 true，应用启动后会直接进入字体诊断矩阵页。
    ///
    /// 保留这个开关是因为中文字形问题**只在某些平台/设备上出现**：
    /// 桌面渲染一切正常，Android 上却可能整片变方块。
    /// 能在真机上直接跑诊断页，就不必靠"改代码、打包、安装、截图"反复试错。
    /// </summary>
    private const bool ShowFontDiagnostics = false;

    private TeacherShellViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 恢复用户选过的主题色（个性化）。必须在创建视图之前：
        // 控件主题靠 DynamicResource 读这些色角色，晚了就缓存成旧配色了。
        ClassShout.Design.Theming.Md3Appearance.Apply(this, LocalSettings.LoadAppearance().SeedColor);

        _viewModel = new TeacherShellViewModel();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                // 桌面头：手机比例窗口，用于在 PC 上调试手机界面
                desktop.MainWindow = new MainWindow { DataContext = _viewModel };
                desktop.ShutdownRequested += (_, _) => DisposeViewModel();
                break;

            case ISingleViewApplicationLifetime singleView:
                // Android 头：没有窗口概念，直接给一个根视图
                singleView.MainView = ShowFontDiagnostics
                    ? new Diagnostics.FontDiagnostics()
                    : new MainView { DataContext = _viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisposeViewModel()
    {
        try
        {
            _viewModel?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 退出路径上的清理异常无需上报
        }
    }
}
