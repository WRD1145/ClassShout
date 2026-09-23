using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClassShout.Design.Theming;
using Material.Icons;

namespace ClassShout.Design.Controls;

/// <summary>
/// 个性化设置：选一个主题色。
///
/// 做成设计系统里的共享控件，是因为教室端与教师端都要有这一项，
/// 而"选色 + 校验 + 恢复默认 + 字体声明"这套逻辑一旦各写一份，迟早会长歪。
/// 控件本身**不碰持久化**（它不认识设置文件），只把选择结果抛给调用方；
/// 谁来存、存到哪，由各端自己决定。
///
/// 整个界面用代码搭而不是 XAML：资源一律走 DynamicResource 绑定，
/// 于是换主题色时控件自己的颜色会跟着变，不必手工刷新。
/// 反过来，如果在构造函数里用 FindResource 取画刷，那个时机控件还没挂到树上，
/// 一律拿到 null —— 界面会变成一片看不见（字体诊断页就踩过这个坑）。
/// </summary>
public sealed class ThemePicker : UserControl
{
    private readonly WrapPanel _swatches = new()
    {
        Orientation = Orientation.Horizontal,
    };

    private readonly TextBox _hexInput = new()
    {
        Watermark = "#RRGGBB",
        MinWidth = 120,
        MaxLength = 9,
    };

    private readonly TextBlock _hint = new()
    {
        FontSize = 12,
        IsVisible = false,
    };

    private readonly Button _resetButton = new() { Content = "恢复默认" };
    private readonly Button _applyButton = new() { Content = "应用" };

    private string? _selectedSeedHex;

    /// <summary>选择发生变化。参数为 #RRGGBB；恢复默认时为 null。</summary>
    public event EventHandler<string?>? SelectionChanged;

    /// <summary>
    /// 当前选中的种子色（#RRGGBB）；null 表示使用内置基线配色。
    /// 由调用方在初始化时写入，用来把界面状态与已保存的设置对齐。
    /// </summary>
    public string? SelectedSeedHex
    {
        get => _selectedSeedHex;
        set
        {
            _selectedSeedHex = value;
            _hexInput.Text = value ?? string.Empty;
            UpdateSwatches();
        }
    }

    public ThemePicker()
    {
        // Avalonia 的 WrapPanel 没有 Spacing（只有 StackPanel 有），
        // 间距只能落在子元素的外边距上。
        foreach (var (label, hex) in Md3Palette.Presets)
        {
            _swatches.Children.Add(CreateSwatch(label, hex));
        }

        _applyButton.Click += (_, _) => ApplyTypedHex();
        _resetButton.Click += (_, _) => Select(null, raise: true);
        _hexInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyTypedHex();
                e.Handled = true;
            }
        };

        // 同样用 WrapPanel 而不是横向 StackPanel：手机上（实测 320 dp 宽）
        // "输入框 + 应用 + 恢复默认"排不进一行，横向 StackPanel 会把最后一个按钮
        // 直接裁掉 —— 看不出被裁、也点不到。换行总比少一个按钮好。
        foreach (var control in new Control[] { _hexInput, _applyButton, _resetButton })
        {
            control.Margin = new Thickness(0, 0, 8, 8);
        }

        var inputRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _hexInput, _applyButton, _resetButton },
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Header("个性化"),
                Body("给界面挑一个主题色。整套配色（主色、容器色、强调色、状态色）会由它算出来，改完立刻生效。"
                     + "设置只保存在本机。"),
                _swatches,
                inputRow,
                _hint,
                CreateFontNotice(),
            },
        };

        SelectedSeedHex = null;
    }

    /// <summary>
    /// 由调用方在保存失败时调用，把失败如实显示出来。
    ///
    /// 控件不认识设置文件，所以它无从知道保存成没成功；但它有提示区，
    /// 与其让每个调用方另找地方报错，不如共用同一处 ——
    /// 这类提示最怕的就是"各自实现"，最后总有一端忘了报。
    /// </summary>
    public void ShowPersistenceFailure(string message) => ShowHint(message, isError: true);

    /// <summary>按标题格式生成一行标题。</summary>
    private static TextBlock Header(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("titleSmall");
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.Primary"));
        return block;
    }

    /// <summary>按正文说明格式生成一段说明文字。</summary>
    private static TextBlock Body(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("bodySmall");
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurfaceVariant"));
        return block;
    }

    /// <summary>
    /// 字体声明。
    ///
    /// 这不是装饰，是许可要求：HarmonyOS Sans 字体许可协议第 2 条第 1 项要求
    /// "在软件中显著声明使用了 HarmonyOS Sans 字体"，第 4 项要求随字体保留版权声明与协议原文
    /// （协议全文随字体一起打进程序集，见 Assets/Fonts/LICENSE.txt）。
    /// 所以这一段不要删。
    /// </summary>
    private static Border CreateFontNotice()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Text = "字体：HarmonyOS Sans SC（© Huawei Device Co., Ltd.，依《HarmonyOS Sans 字体许可协议》"
                   + "随本软件分发，未做修改）。",
        };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurfaceVariant"));

        var border = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            Child = text,
        };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Md3.SurfaceContainerHighest"));

        return border;
    }

    /// <summary>一个色卡。选中时描边加粗并打勾。</summary>
    private Border CreateSwatch(string label, string hex)
    {
        Md3Palette.TryParseSeed(hex, out var argb);
        var color = Color.FromUInt32(argb);

        var check = new Md3Icon
        {
            Kind = MaterialIconKind.Check,
            Size = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        check.Bind(Md3Icon.IconBrushProperty, new DynamicResourceExtension("Md3.OnPrimary"));

        var swatch = new Border
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(2),
            Background = new ImmutableSolidColorBrush(color),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = check,
            Tag = hex,
        };
        swatch.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Md3.OutlineVariant"));
        ToolTip.SetTip(swatch, label);

        swatch.PointerPressed += (_, e) =>
        {
            Select(hex, raise: true);
            e.Handled = true;
        };

        return swatch;
    }

    /// <summary>把界面上的选中状态刷成与 <see cref="SelectedSeedHex"/> 一致。</summary>
    private void UpdateSwatches()
    {
        foreach (var child in _swatches.Children)
        {
            if (child is not Border swatch)
            {
                continue;
            }

            var isSelected = swatch.Tag is string hex &&
                             string.Equals(hex, _selectedSeedHex, StringComparison.OrdinalIgnoreCase);

            if (swatch.Child is Md3Icon check)
            {
                check.IsVisible = isSelected;
            }

            // 选中态用主色描边。这里重绑一次而不是改画刷值：
            // 画刷来自 DynamicResource，直接改值会连累所有共用它的控件。
            swatch.Bind(
                Border.BorderBrushProperty,
                new DynamicResourceExtension(isSelected ? "Md3.Primary" : "Md3.OutlineVariant"));
            swatch.BorderThickness = new Thickness(isSelected ? 3 : 2);
        }
    }

    /// <summary>应用输入框里的颜色。非法时给出明确提示，而不是当作没输入。</summary>
    private void ApplyTypedHex()
    {
        var typed = _hexInput.Text;

        if (!Md3Palette.TryParseSeed(typed, out var argb))
        {
            ShowHint($"「{typed}」不是合法颜色，请填 #RRGGBB 或 #AARRGGBB。", isError: true);
            return;
        }

        Select(Md3Palette.ToHex(argb), raise: true);
    }

    private void Select(string? hex, bool raise)
    {
        SelectedSeedHex = hex;

        // 内置默认色等同于"没有自定义"，恢复默认按钮与选中默认色卡走的是同一条路
        var normalized = string.Equals(hex, Md3Palette.DefaultSeedHex, StringComparison.OrdinalIgnoreCase)
            ? null
            : hex;

        if (normalized is null)
        {
            ShowHint("已恢复内置配色。", isError: false);
        }
        else
        {
            ShowHint($"已选 {normalized}。", isError: false);
        }

        if (raise)
        {
            SelectionChanged?.Invoke(this, normalized);
        }
    }

    private void ShowHint(string message, bool isError)
    {
        _hint.Text = message;
        _hint.IsVisible = true;
        _hint.Bind(
            TextBlock.ForegroundProperty,
            new DynamicResourceExtension(isError ? "Md3.Error" : "Md3.OnSurfaceVariant"));
    }
}
