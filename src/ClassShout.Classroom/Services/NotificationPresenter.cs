using Avalonia.Threading;
using ClassShout.Classroom.Views;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 弹窗的显示管理：复用同一个窗口、自动消失、按配置摆位。
///
/// 复用而不是每次新建：新建窗口要走一遍原生窗口创建与首次布局，
/// 连续来消息时会闪；而且反复创建销毁容易漏掉置顶状态。
/// </summary>
public sealed class NotificationPresenter : IDisposable
{
    private readonly ClassroomNotificationSettings _settings;

    private NotificationWindow? _window;
    private DispatcherTimer? _hideTimer;
    private bool _disposed;

    public NotificationPresenter(ClassroomNotificationSettings settings)
    {
        _settings = settings;
    }

    /// <summary>用户在弹窗上点了一下。</summary>
    public event Action? Dismissed;

    /// <summary>显示一条提示。必须在 UI 线程调用。</summary>
    public void Show(NotificationContent content)
    {
        if (_disposed || !_settings.Enabled)
        {
            return;
        }

        var window = EnsureWindow();
        window.DataContext = content;
        window.ApplySettings(_settings);

        if (!window.IsVisible)
        {
            window.Opacity = 0;
            window.Show();
        }

        // 布局完成后尺寸才是准的，底部/右侧的坐标此时才算得出来
        Dispatcher.UIThread.Post(
            () =>
            {
                window.PositionOnScreen();
                window.Opacity = 1;
            },
            DispatcherPriority.Loaded);

        RestartHideTimer();
    }

    /// <summary>立即关闭弹窗。</summary>
    public void Hide()
    {
        _hideTimer?.Stop();

        if (_window is { IsVisible: true } window)
        {
            window.Opacity = 0;
            window.Hide();
        }
    }

    /// <summary>配置变化后立即生效（例如用户改了位置或置顶档位）。</summary>
    public void Refresh()
    {
        if (_window is not { IsVisible: true } window)
        {
            return;
        }

        window.ApplySettings(_settings);
        window.PositionOnScreen();
        RestartHideTimer();
    }

    /// <summary>弹一条自检提示，用于在设置界面里预览效果。</summary>
    public void ShowPreview()
        => Show(new NotificationContent("张老师", "这是一条弹窗预览：同学们请安静，现在讲第三题。", IsVoice: false));

    private NotificationWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var window = new NotificationWindow();
        window.ApplySettings(_settings);
        window.DismissRequested += (_, _) =>
        {
            Hide();
            Dismissed?.Invoke();
        };

        // 文本长短会改变高度，重新摆位才能保证贴边而不是悬在半空
        window.SizeChanged += (_, _) =>
        {
            if (window.IsVisible)
            {
                window.PositionOnScreen();
            }
        };

        _window = window;
        return window;
    }

    private void RestartHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = null;

        if (_settings.DurationSeconds <= 0)
        {
            // 配置为 0 表示不自动消失，需要用户点掉
            return;
        }

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.DurationSeconds) };
        _hideTimer.Tick += (_, _) => Hide();
        _hideTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer?.Stop();
        _hideTimer = null;

        _window?.Close();
        _window = null;
    }
}
