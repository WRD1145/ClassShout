using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 托盘驻留：点关闭按钮不退出，而是缩到通知区域继续接收喊话。
///
/// 为什么教室端必须这样：这台机器全天挂在教室里等着收喊话，
/// 而"关闭窗口"离"最小化"只有一个像素。老师或值日生顺手点一下 ×，
/// 整个班就此失联，而且没有任何人会发现 —— 直到下一位老师喊话没人应。
/// 所以关闭被解释成"收起来"，真正的退出挪到托盘右键菜单里，
/// 让退出变成一个需要刻意完成的动作。
///
/// 这个类自己订阅窗口的 Closing，因此 MainWindow 不需要为托盘改一行代码。
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private const string IconUri = "avares://ClassShout.Classroom/Assets/classroom.ico";

    private readonly Window _window;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly TrayIcon _icon;

    /// <summary>是否已经在走"真正退出"的流程。见 OnWindowClosing 里的说明。</summary>
    private bool _exiting;

    private bool _disposed;

    private TrayPresence(Window window, IClassicDesktopStyleApplicationLifetime desktop, TrayIcon icon)
    {
        _window = window;
        _desktop = desktop;
        _icon = icon;

        _icon.Clicked += (_, _) => Restore();
        _window.Closing += OnWindowClosing;

        // 双保险。真正的退出靠 _exiting 这个标志，不依赖 Avalonia 内部给关闭事件
        // 标了哪种原因；ShutdownRequested 一响就说明确实该收尾了，一律放行。
        _desktop.ShutdownRequested += OnShutdownRequested;
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) => _exiting = true;

    /// <summary>
    /// 创建托盘图标并接管关闭行为。失败时返回 null —— 调用方应当退回"关闭即退出"，
    /// 而不是让教室端连窗口都关不掉。
    /// </summary>
    public static TrayPresence? TryInstall(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (Application.Current is not { } app)
            {
                throw new InvalidOperationException("Application 尚未初始化，无法挂载托盘图标。");
            }

            var icon = new TrayIcon
            {
                Icon = LoadIcon(),
                ToolTipText = "ClassShout 教室端 —— 正在接收喊话（双击打开）",
                IsVisible = true,
            };

            // 先有实例，才能把菜单项接到实例方法上
            var presence = new TrayPresence(window, desktop, icon);
            icon.Menu = presence.BuildMenu();
            icon.Clicked += (_, _) => presence.Restore();

            // 挂到 Application 上，托盘图标才会真的出现在通知区域
            TrayIcon.SetIcons(app, new TrayIcons { icon });

            return presence;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private NativeMenu BuildMenu()
    {
        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => Restore();

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => Exit();

        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>把窗口收进托盘。</summary>
    public void Minimize()
    {
        if (_disposed)
        {
            return;
        }

        _window.Hide();
    }

    /// <summary>从托盘恢复窗口。</summary>
    public void Restore()
    {
        if (_disposed)
        {
            return;
        }

        // 先回到 Normal 再显示：否则从最小化状态唤回时会保持最小化，
        // 用户看到托盘图标闪了一下却没窗口出现。
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        _window.Activate();
    }

    /// <summary>真正退出。只有托盘菜单里的「退出」会走到这里。</summary>
    public void Exit()
    {
        if (_disposed)
        {
            return;
        }

        // 先把图标摘掉。留着它，进程退出后通知区域里可能残留一个点不动的影子图标。
        _icon.IsVisible = false;

        _exiting = true;
        _desktop.Shutdown();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 两条放行条件，缺一不可：
        //
        //   _exiting          —— 托盘菜单点了「退出」，这是我们自己要关；
        //   CloseReason 不是 WindowClosing —— 操作系统在关机 / 注销，或者程序在收尾。
        //
        // 第二条特别要紧：教室电脑多半设了定时关机。如果这里无差别地拦下关闭，
        // 夜里关机时 Windows 会弹"此应用正在阻止关机"，然后把我们强杀掉。
        // 只拦"用户点了 ×"这一种情况，语义才是准的。
        if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        e.Cancel = true;
        Minimize();
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri(IconUri));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _window.Closing -= OnWindowClosing;
        _desktop.ShutdownRequested -= OnShutdownRequested;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
