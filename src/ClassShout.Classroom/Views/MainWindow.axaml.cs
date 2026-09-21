using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ClassShout.Classroom.ViewModels;

namespace ClassShout.Classroom.Views;

public partial class MainWindow : Window
{
    private ClassroomViewModel? _subscribed;

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
