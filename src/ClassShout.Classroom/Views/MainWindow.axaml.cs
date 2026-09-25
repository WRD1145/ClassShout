using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ClassShout.Classroom.Services;
using ClassShout.Classroom.ViewModels;
using ClassShout.Core.Remote;

namespace ClassShout.Classroom.Views;

public partial class MainWindow : Window
{
    private ClassroomViewModel? _subscribed;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();

        // 视图模型不直接碰剪贴板（那是 UI 层的事），
        // 它只发一个"请复制这段文本"的请求，由窗口负责实际写剪贴板。
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.CopyRequested -= OnCopyRequested;
        }

        _subscribed = DataContext as ClassroomViewModel;

        if (_subscribed is not null)
        {
            _subscribed.CopyRequested += OnCopyRequested;
        }
    }

    private async void OnCopyRequested(string text)
    {
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                _subscribed?.NotifyCopied(Shorten(text));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 剪贴板偶发不可用（远程桌面、权限受限等），不该影响主流程
        }
    }

    private static string Shorten(string text)
        => text.Length <= 24 ? text : text[..24] + "…";

    /// <summary>
    /// 打开设置窗口。启用设置口令时先验一次 PIN。
    ///
    /// 1. 没启用 PIN —— 直接开；
    /// 2. 已启用且本次尚未验证 —— 弹 PIN 提示窗，验过才开；
    /// 3. 已经开着 —— 把它提到前面，而不是再开一个。
    ///
    /// 关掉设置窗口时会重新上锁：验证一次是"这一次进入"的通行证，
    /// 不是"这台机器永久免验"。用户要的是改设置时不必逐项验，而不是验过就永远放行。
    /// </summary>
    private async void OnOpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        if (!SettingsAccess.IsUnlocked)
        {
            // 只有在"进去要 PIN"这一类保护开着时才拦；只设了分项保护时，
            // 设置窗口可以随便进，锁的是里面那几项本身。
            if (SettingsLock.IsEntryLocked)
            {
                var prompt = new PinPromptWindow();
                var passed = await prompt.ShowDialog<bool>(this);

                if (!passed)
                {
                    return;
                }
            }

            SettingsAccess.Unlock();

            // 整体锁验过之后，受保护的那几项也一并放行 ——
            // 进来时已经验过一次，再让用户为每一项各输一遍，只会把 PIN 逼到便签上。
            (DataContext as ViewModels.ClassroomViewModel)?.UnlockAllAreas();
        }

        var window = new SettingsWindow { DataContext = DataContext };
        window.Closed += (_, _) =>
        {
            SettingsAccess.Lock();

            // 关掉窗口就把分项锁恢复：与整体锁同一个语义 ——
            // 验证一次是"这一次进入"的通行证，不是"这台机器永久免验"。
            (DataContext as ViewModels.ClassroomViewModel)?.LockAreasAgain();
            _settingsWindow = null;
        };

        _settingsWindow = window;
        window.Show(this);
    }

    /// <summary>
    /// 切换亮色 / 暗色主题。
    /// 教室端白天多在明亮环境投影，默认用亮色；晚自习等场景可切暗色。
    /// </summary>
    private void OnToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
