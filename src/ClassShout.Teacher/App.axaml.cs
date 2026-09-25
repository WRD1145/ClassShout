using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;
using ClassShout.Teacher.Services;
using ClassShout.Teacher.ViewModels;
using ClassShout.Teacher.Views;

namespace ClassShout.Teacher;

/// <summary>
/// 教师端应用。桌面头与 Android 头共用这个类，
/// 两者的差异只体现在下面走的是哪条生命周期分支。
/// </summary>
public partial class App : Application
{
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
                // Android 头：没有窗口概念，直接给一个根视图。
                //
                // 这里原本还有一个编译期开关 ShowFontDiagnostics，让应用启动后直接进字体矩阵页。
                // 排查中文字形问题时确实需要它，但"改常量、重新打包、装机"本身就是障碍。
                // 现在同一页由开发者模式在运行时打开（「关于」里连点版本号 10 次），
                // 那个常量随之删除 —— 一个必须重打包才能用的诊断开关，
                // 等于逼人在真机出问题的现场放弃诊断。
                singleView.MainView = new MainView { DataContext = _viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();

        // 视图模型已经订阅好了，这时再把"启动时收到的那条分享链接"补发出去。
        // 早一步发就没人接：链接是在 Main 里解析的，而那会儿应用还没起来。
        if (PendingShareLink is { Length: > 0 } startupLink)
        {
            PendingShareLink = null;
            TeacherPlatform.NotifyShareLink(startupLink);
        }
    }

    /// <summary>
    /// 启动时收到、还没交给界面层的分享链接。由平台头在启动早期设置。
    ///
    /// 放在 App 上而不是直接调 TeacherPlatform：事件的订阅者在视图模型构造函数里才建立，
    /// 而启动参数是在那之前解析的 —— 直接发会石沉大海。
    /// </summary>
    public static string? PendingShareLink { get; set; }

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
