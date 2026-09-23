using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 单实例约束：一台教室电脑上只允许跑一个教室端。
///
/// 为什么必须做：教室端要对 45900 端口做 UDP 广播与 TCP 监听。
/// 第二个实例起不来监听会报端口占用（界面上一行红字），
/// 但更糟的是它仍然会开一个窗口、显示"未连接"，
/// 值日生看到两个窗口，很可能把正在工作的那个关掉。
/// 与其让人去分辨哪个是"真的"，不如第二个实例直接说清楚。
///
/// 作用域用"当前用户"而不是全局：同一台电脑上不同 Windows 账户
/// 各自跑一个教室端是合理的（例如一台机器同时管两个教室的试点），
/// 全局互斥会把这个场景也堵死。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// 尝试成为唯一实例。已经在跑时返回 null。
    ///
    /// 名字里带上用户名：Windows 的 Local\ 前缀本身就是按会话隔离的，
    /// 这里再显式带上，是为了让名字在日志与排查时能一眼看出属于谁。
    /// </summary>
    public static SingleInstance? TryAcquire(string appName)
    {
        var name = $"Local\\ClassShout.{appName}.{Environment.UserName}";

        try
        {
            // initiallyOwned: true —— 创建者直接持有；已存在时 createdNew 为 false
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstance(mutex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // 拿不到互斥体（受限环境、命名受限等）时不该把用户挡在门外 ——
            // 单实例是体验优化，不是功能前提，失败就放行。
            return new SingleInstance(new Mutex());
        }
    }

    /// <summary>
    /// 弹出"已经在运行"的提示窗，等用户关掉之后由调用方退出。
    ///
    /// 用代码搭窗口而不是再开一个 axaml：它只有一个标题、一句话、一个按钮，
    /// 为它单开一套 XAML 文件反而更难维护，也不方便在别的平台头上复用。
    /// </summary>
    public static Window CreateAlreadyRunningWindow(string appName)
    {
        var okButton = new Button
        {
            Content = "知道了",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(24, 10),
        };

        var window = new Window
        {
            Title = $"{appName} 已在运行",
            Width = 420,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{appName} 已经在这台电脑上运行了。",
                        FontSize = 17,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "请到任务栏或屏幕右下角的通知区域找到它 —— "
                             + "如果窗口被关掉了，它仍然在后台接收喊话，双击托盘图标就能重新打开。",
                        FontSize = 13,
                        Opacity = 0.75,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    okButton,
                },
            },
        };

        okButton.Click += (_, _) => window.Close();
        return window;
    }

    public void Dispose()
    {
        var mutex = _mutex;
        if (mutex is null)
        {
            return;
        }

        _mutex = null;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不是当前线程持有的（进程正在退出），忽略
        }

        mutex.Dispose();
    }
}