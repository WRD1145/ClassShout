using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;

namespace ClassShout.Classroom.Views;

/// <summary>
/// 进入设置前的 PIN 提示窗。
///
/// 用 ShowDialog&lt;bool&gt; 而不是自己做一套回调：调用方只关心"放行还是不放行"，
/// 一个布尔值就够了，而模态对话框天然表达了"这一步没完成就不能往下走"。
/// 校验本身放在 SettingsLock 里，这里只负责收集输入与显示错误 ——
/// 界面代码不该知道 PIN 是怎么存、怎么比的。
/// </summary>
public partial class PinPromptWindow : Window
{
    public PinPromptWindow()
    {
        InitializeComponent();

        // 打开就能直接输，不用先点一下输入框
        Opened += (_, _) => this.FindControl<TextBox>("PinInput")?.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 换一套标题与说明。
    ///
    /// 同一个窗口服务两个场景：进入设置、退出程序。两处的"为什么要输 PIN"
    /// 完全不同（前者是"别乱改"，后者是"别把教室关掉"），
    /// 用同一句文案会让其中一边的人看不懂自己在确认什么。
    /// </summary>
    public void Configure(string title, string detail)
    {
        this.FindControl<TextBlock>("PromptTitle")?.SetCurrentValue(TextBlock.TextProperty, title);
        this.FindControl<TextBlock>("PromptDetail")?.SetCurrentValue(TextBlock.TextProperty, detail);
        Title = title;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => TryAccept();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void TryAccept()
    {
        var input = this.FindControl<TextBox>("PinInput")?.Text ?? string.Empty;
        var error = this.FindControl<TextBlock>("ErrorText");

        if (SettingsLock.Verify(input))
        {
            Close(true);
            return;
        }

        if (error is not null)
        {
            error.Text = "PIN 不正确。";
            error.IsVisible = true;
        }

        if (this.FindControl<TextBox>("PinInput") is { } box)
        {
            box.Text = string.Empty;
            box.Focus();
        }
    }
}