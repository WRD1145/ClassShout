using Avalonia;
using Avalonia.Controls;
using ClassShout.Core.Remote;
using ClassShout.Design.Theming;

namespace ClassShout.Design.Controls;

public sealed class FontScalePicker : UserControl
{
    private static readonly int[] Scales = [100, 115, 130];
    private readonly ComboBox _picker;
    private readonly TextBlock _error = new() { Text = "界面字号保存失败，请重试。", IsVisible = false };
    private bool _restoring;

    public FontScalePicker()
    {
        _picker = new ComboBox
        {
            ItemsSource = new[] { "标准 (100%)", "较大 (115%)", "最大 (130%)" },
            SelectedIndex = Array.IndexOf(Scales, LocalSettings.LoadAppearance().Normalized().FontScalePercent),
        };
        Avalonia.Automation.AutomationProperties.SetName(_picker, "界面字号");
        _picker.SelectionChanged += OnSelectionChanged;
        Content = new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = "界面字号" }, _picker, _error },
        };
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_restoring || _picker.SelectedIndex < 0 || Application.Current is not { } app)
            return;

        var settings = LocalSettings.LoadAppearance().Normalized();
        var previous = settings.FontScalePercent;
        settings.FontScalePercent = Scales[_picker.SelectedIndex];
        _error.IsVisible = !LocalSettings.SaveAppearance(settings);
        if (_error.IsVisible)
        {
            _restoring = true;
            _picker.SelectedIndex = Array.IndexOf(Scales, previous);
            _restoring = false;
            return;
        }

        InterfaceTypography.Apply(app, settings.FontScalePercent);
    }
}
