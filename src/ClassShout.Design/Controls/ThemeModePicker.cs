using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassShout.Core.Remote;

namespace ClassShout.Design.Controls;

/// <summary>
/// 明暗三档的选择器：跟随系统 / 浅色 / 深色。
///
/// 与 <see cref="ThemePicker"/>（选主题色）分开成两个控件，因为它们是两件事：
/// 种子色决定"这套界面是什么颜色"，明暗决定"用亮的那套还是暗的那套"。
/// 两者排在同一张卡片里，但各有各的事件与持久化。
///
/// 界面用代码搭、颜色走 DynamicResource（与 ThemePicker 同一套理由）：
/// 换明暗或换种子色时控件自己的颜色要跟着变，而在构造函数里 FindResource
/// 一律拿到 null（那时还没挂到树上）。
///
/// 控件**不碰持久化**：它只把选中的档位抛给调用方，谁来存由各端决定。
/// </summary>
public sealed class ThemeModePicker : UserControl
{
    private readonly StackPanel _buttons = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
    };

    private readonly List<(RadioButton Button, ThemeMode Mode)> _options = [];

    private readonly TextBlock _hint = new();

    private ThemeMode _selectedMode = ThemeMode.System;
    private bool _suppress;

    /// <summary>选择发生变化。参数是三档之一。</summary>
    public event EventHandler<ThemeMode>? ModeChanged;

    /// <summary>
    /// 当前选中的档位。由调用方在初始化时写入，把界面与已保存的设置对齐 ——
    /// 写入时不触发 <see cref="ModeChanged"/>，否则启动就会白存一次。
    /// </summary>
    public ThemeMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            _selectedMode = value;
            _suppress = true;

            foreach (var (button, mode) in _options)
            {
                button.IsChecked = mode == value;
            }

            _suppress = false;
        }
    }

    public ThemeModePicker()
    {
        var title = new TextBlock { Text = "明暗" };
        title.Classes.Add("bodyMedium");

        _hint.Text = "「跟随系统」随系统主题切换；教室投影、手机强光下建议固定「浅色」。";
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 6, 0, 0);
        _hint.Classes.Add("bodySmall");
        _hint[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions
            .DynamicResourceExtension("Md3.OnSurfaceVariant");

        foreach (var mode in ThemeModeMapping.All)
        {
            var button = new RadioButton
            {
                GroupName = "ClassShoutThemeMode",
                Content = ThemeModeMapping.Label(mode),
                VerticalAlignment = VerticalAlignment.Center,
            };

            button.Classes.Add("bodyMedium");
            button.IsChecked = mode == _selectedMode;
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                {
                    OnOptionChecked(mode);
                }
            };

            _options.Add((button, mode));
            _buttons.Children.Add(button);
        }

        Content = new StackPanel
        {
            Children = { title, _buttons, _hint },
        };
    }

    /// <summary>
    /// 存不下时把话说清楚（与 <see cref="ThemePicker.ShowPersistenceFailure"/> 同一套说法）。
    ///
    /// 这类提示最怕各写一份：一端报了、另一端忘了，用户在那一端就会以为"选了就好了"。
    /// </summary>
    public void ShowPersistenceFailure(string message)
    {
        _hint.Text = message;
        _hint[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions
            .DynamicResourceExtension("Md3.Error");
    }
    private void OnOptionChecked(ThemeMode mode)
    {
        if (_suppress || mode == _selectedMode)
        {
            return;
        }

        _selectedMode = mode;
        ModeChanged?.Invoke(this, mode);
    }
}
