using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassShout.Classroom.Services;
using ClassShout.Classroom.ViewModels;
using ClassShout.Classroom.Views;
using ClassShout.Core.Remote;

namespace ClassShout.Classroom;

public partial class App : Application
{
    private ClassroomViewModel? _viewModel;
    private TrayPresence? _tray;
    private SingleInstance? _singleInstance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 恢复用户选过的主题色。必须早于任何窗口的创建：
        // 换色是往 Application.Resources 里换一块资源字典，而控件一旦构造完就把
        // 一部分颜色值缓存下来了；能提前就提前，免得界面出现"一半新一半旧"。
        var appearance = LocalSettings.LoadAppearance().Normalized();

        ClassShout.Design.Theming.Md3Appearance.Apply(this, appearance.SeedColor);

        // 明暗同样要在第一个窗口出现之前定下来：晚一步就会先闪一下亮色。
        // 「跟随系统」交给 Avalonia 的 ThemeVariant.Default，系统换主题时它自己广播。
        ClassShout.Design.Theming.Md3Appearance.ApplyThemeMode(this, appearance.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 单实例检查放在最前面：还没建视图模型、还没开监听端口。
            //
            // 第二个实例若照常启动，它会因为端口被占用而在界面上显示一行错误，
            // 但窗口照样开着、照样写着"未连接" —— 值日生看到两个窗口，
            // 很可能把正在工作的那个关掉。
            _singleInstance = SingleInstance.TryAcquire("Classroom");
            if (_singleInstance is null)
            {
                var notice = SingleInstance.CreateAlreadyRunningWindow("ClassShout 教室端");
                desktop.MainWindow = notice;
                notice.Closed += (_, _) => desktop.Shutdown();

                base.OnFrameworkInitializationCompleted();
                return;
            }

            _viewModel = new ClassroomViewModel();

            try
            {
                _viewModel.Start();
            }
            catch (Exception ex)
            {
                // 端口被占用等启动失败不应导致整个进程崩溃，
                // 主窗口仍然打开并显示错误日志，便于排查。
                _viewModel.Logs.Insert(0, new LogEntry(DateTime.Now, "错误", $"启动失败：{ex.Message}"));
            }

            var window = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = window;

            // 自动连接放在窗口出来之后：连服务器要等应答，卡在构造里会让教室端白屏几秒。
            // 失败不弹框 —— 原因落在日志与「跨局域网喊话」那张卡上，与手动连接一致。
            window.Opened += (_, _) => _viewModel.BeginAutoConnect();

            // 收到"窗口式"喊话时，如果窗口正缩在托盘里（或最小化），把它唤出来。
            //
            // 视图模型不知道 Window 与托盘的存在，所以由这里注入。判定放在这里而不是
            // 视图模型里：只有窗口层知道"现在到底看不看得见"。
            _viewModel.RequestStageVisible = () =>
            {
                if (window.IsVisible && window.WindowState != WindowState.Minimized)
                {
                    return false;
                }

                if (_tray is not null)
                {
                    // 走托盘那条路：它会先把最小化还原再显示，否则唤回来还是个最小化的窗口
                    _tray.Restore();
                }
                else
                {
                    window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                }

                return true;
            };

            // 关闭窗口 → 缩到托盘继续接收喊话。装不上就维持"关闭即退出"，
            // 总好过一个关不掉的窗口。
            _tray = TrayPresence.TryInstall(window, desktop);
            _viewModel.Logs.Insert(0, new LogEntry(
                DateTime.Now,
                "信息",
                _tray is null
                    ? "通知区域图标不可用，关闭窗口将直接退出。"
                    : "已驻留通知区域：关闭窗口不会退出，右键托盘图标可退出。"));

            desktop.ShutdownRequested += (_, _) =>
            {
                _tray?.Dispose();
                _singleInstance?.Dispose();

                try
                {
                    _viewModel?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                }
                catch (AggregateException)
                {
                    // 退出路径上的清理异常无需上报
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
