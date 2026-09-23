using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassShout.Classroom.Services;
using ClassShout.Classroom.ViewModels;
using ClassShout.Classroom.Views;

namespace ClassShout.Classroom;

public partial class App : Application
{
    private ClassroomViewModel? _viewModel;
    private TrayPresence? _tray;
    private SingleInstance? _singleInstance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 与教师端保持一致；教室端目前只跑在 Windows 上，这里调用是无副作用的
        ClassShout.Design.Md3Typography.ApplyPlatformDefaults(this);

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
