using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ClassShout.Classroom.Services;

namespace ClassShout.Classroom.Views;

/// <summary>
/// 屏幕边缘的喊话提示弹窗。
///
/// 设计要点：
///   · 不抢焦点（ShowActivated=False + WS_EX_NOACTIVATE）—— 老师正在投屏或操作时，
///     提示不该把当前窗口顶下去；
///   · 不进任务栏与 Alt+Tab（WS_EX_TOOLWINDOW）—— 它是个通知，不是一个应用窗口；
///   · 置顶档位由 <see cref="TopmostMode"/> 决定，强制档位靠周期性重申维持。
/// </summary>
public partial class NotificationWindow : Window
{
    private DispatcherTimer? _topmostTimer;
    private ClassroomNotificationSettings _settings = new();

    public NotificationWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>点击任意位置立即关闭 —— 弹窗挡着内容时用户的第一反应就是点掉它。</summary>
    public event EventHandler? DismissRequested;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 这两步必须在原生窗口存在之后做，否则拿不到句柄
        WindowTopmost.MakeNonActivatingToolWindow(this);
        ApplyTopmost();
        StartTopmostWatchdog();
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer?.Stop();
        _topmostTimer = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// 窗口一藏起来就停掉置顶看门狗。
    ///
    /// 弹窗是复用的：显示时 Show()、消失时 Hide()，从不 Close()。
    /// 所以 OnClosed 那条停止路径根本不会走到，看门狗会一直跑下去 ——
    /// 而它每一秒都在重申置顶。以前那句 SetWindowPos 里带着 SWP_SHOWWINDOW，
    /// 于是隐藏之后一到两秒，弹窗就被重新显示成一个"看不见、但在最上层、
    /// 仍然接收鼠标点击"的窗口，正好压住那一片屏幕。
    /// （BringToFront 现在也会自己跳过隐藏窗口，这里是第二道。）
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                StartTopmostWatchdog();
            }
            else
            {
                _topmostTimer?.Stop();
                _topmostTimer = null;
            }
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>应用配置。位置、宽度与置顶档位都在这里生效。</summary>
    public void ApplySettings(ClassroomNotificationSettings settings)
    {
        _settings = settings;
        Width = settings.Width;

        ApplyTopmost();
        StartTopmostWatchdog();
    }

    /// <summary>
    /// 把窗口摆到配置的角落。
    ///
    /// 需要在布局完成之后调用 —— 尺寸未知就无法算底部/右侧的坐标。
    /// Avalonia 的 Position 用的是物理像素，而 Bounds 是逻辑像素，
    /// 所以必须乘上屏幕缩放，否则高 DPI 下位置会跑偏。
    /// </summary>
    public void PositionOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;

        var widthPx = (int)Math.Round(Bounds.Width * scaling);
        var heightPx = (int)Math.Round(Bounds.Height * scaling);
        var margin = (int)Math.Round(_settings.Margin * scaling);

        var onRight = _settings.Corner is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        var onBottom = _settings.Corner is NotificationCorner.BottomLeft or NotificationCorner.BottomRight;

        var x = onRight ? area.Right - widthPx - margin : area.X + margin;
        var y = onBottom ? area.Bottom - heightPx - margin : area.Y + margin;

        Position = new PixelPoint(x, y);
    }

    private void ApplyTopmost()
    {
        Topmost = _settings.Topmost != TopmostMode.None;

        if (_settings.Topmost == TopmostMode.Forced)
        {
            WindowTopmost.BringToFront(this);
        }
        else if (_settings.Topmost == TopmostMode.None)
        {
            WindowTopmost.ClearTopmost(this);
        }
    }

    /// <summary>
    /// 强制档位下周期性重申置顶。
    ///
    /// 单次 SetWindowPos 只能保证"此刻在最前"，任何后把自己设为置顶的程序都会压过我们。
    /// 一秒一次的代价可以忽略，换来的是弹窗在整段显示时间内稳定可见。
    /// </summary>
    private void StartTopmostWatchdog()
    {
        if (_settings.Topmost != TopmostMode.Forced)
        {
            _topmostTimer?.Stop();
            _topmostTimer = null;
            return;
        }

        if (_topmostTimer is not null)
        {
            return;
        }

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (_, _) => WindowTopmost.BringToFront(this);
        _topmostTimer.Start();
    }
}
