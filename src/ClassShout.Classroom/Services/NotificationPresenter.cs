using Avalonia.Threading;
using ClassShout.Classroom.Views;
using ClassShout.Core.Protocol;

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

    /// <summary>当前这条弹窗要求的停留时长；无表示用教室端设置里的秒数。</summary>
    private TimeSpan? _requestedHold;

    /// <summary>当前这条是不是"常驻"（不自动消失，要点掉才算）。</summary>
    private bool _isForever;

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

        // 发送方可以指定这条停留多久（0 = 常驻，要点掉）。没指定才用教室端设置里的秒数。
        _isForever = content.HoldMs == ShoutHoldDurations.Forever;
        _requestedHold = ShoutHoldDurations.IsSpecified(content.HoldMs) && !_isForever
            ? TimeSpan.FromMilliseconds(content.HoldMs)
            : null;

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

    /// <summary>
    /// 预览用的来源与内容。
    ///
    /// 抽成常量是因为预览有两条路（教室端自己的弹窗、投给 ClassIsland），
    /// 两处各写一份文案的话，改了一处另一处就会说的是另一句话。
    /// </summary>
    public const string PreviewSourceName = "张老师";

    /// <inheritdoc cref="PreviewSourceName" />
    public const string PreviewText = "这是一条弹窗预览：同学们请安静，现在讲第三题。";

    /// <summary>弹一条自检提示，用于在设置界面里预览效果。</summary>
    public void ShowPreview()
        => Show(new NotificationContent(PreviewSourceName, PreviewText, IsVoice: false));

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

        // 常驻：不自动消失，需要用户点掉
        if (_isForever)
        {
            return;
        }

        // 这条自己指定了停留时长就用它，否则用设置里的秒数；两处都是"不自动消失"时就不装计时器。
        var hold = _requestedHold
                   ?? (_settings.DurationSeconds > 0 ? TimeSpan.FromSeconds(_settings.DurationSeconds) : null);

        if (hold is not { } interval)
        {
            return;
        }

        _hideTimer = new DispatcherTimer { Interval = interval };
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
