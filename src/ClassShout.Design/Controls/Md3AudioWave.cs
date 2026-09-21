using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassShout.Design.Controls;

/// <summary>
/// 音频电平波形控件。
///
/// 每次 <see cref="Level"/> 变化就往环形缓冲里压一个采样，
/// 渲染时把最近的若干采样画成一条滚动的柱状波形 —— 也就是录音时常见的那种效果。
///
/// 之所以不用逐帧动画：电平本身来自真实麦克风数据，
/// 让数据驱动渲染比定时器驱动更真实，也不会多占一个 UI 定时器。
/// </summary>
public class Md3AudioWave : Control
{
    private double[] _history = new double[48];
    private int _cursor;
    private int _filled;

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<Md3AudioWave, double>(nameof(Level));

    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<Md3AudioWave, int>(nameof(BarCount), 48);

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<Md3AudioWave, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<Md3AudioWave, double>(nameof(BarWidth), 3d);

    public static readonly StyledProperty<double> MinBarHeightProperty =
        AvaloniaProperty.Register<Md3AudioWave, double>(nameof(MinBarHeight), 3d);

    static Md3AudioWave()
    {
        AffectsRender<Md3AudioWave>(LevelProperty, BarBrushProperty, BarWidthProperty, MinBarHeightProperty);
    }

    /// <summary>当前电平，取值 0~1。每次赋值都会推进一格波形。</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>波形柱数量。</summary>
    public int BarCount
    {
        get => GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    /// <summary>柱体颜色。</summary>
    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>柱体宽度。</summary>
    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    /// <summary>静音时柱体的最小高度，避免整条波形消失。</summary>
    public double MinBarHeight
    {
        get => GetValue(MinBarHeightProperty);
        set => SetValue(MinBarHeightProperty, value);
    }

    /// <summary>清空波形历史（开始新一轮录音时调用）。</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _cursor = 0;
        _filled = 0;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BarCountProperty)
        {
            var count = Math.Max(1, BarCount);
            _history = new double[count];
            _cursor = 0;
            _filled = 0;
        }
        else if (change.Property == LevelProperty)
        {
            PushSample(Level);
            InvalidateVisual();
        }
    }

    private void PushSample(double level)
    {
        if (_history.Length == 0)
        {
            return;
        }

        _history[_cursor] = Math.Clamp(level, 0d, 1d);
        _cursor = (_cursor + 1) % _history.Length;
        _filled = Math.Min(_filled + 1, _history.Length);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || _history.Length == 0)
        {
            return;
        }

        var brush = BarBrush
                    ?? this.FindResource("Md3.Primary") as IBrush
                    ?? Brushes.MediumPurple;

        var count = _history.Length;
        var slot = bounds.Width / count;
        var barWidth = Math.Min(BarWidth, Math.Max(1d, slot - 1));
        var radius = barWidth / 2;
        var maxHeight = bounds.Height;

        // 从右往左画：最新的采样在最右边
        for (var i = 0; i < count; i++)
        {
            // 取出第 i 个历史采样（i=0 是最旧的）
            var index = _filled < count
                ? i
                : (_cursor + i) % count;

            var value = index < _history.Length ? _history[index] : 0d;
            if (_filled < count && i >= _filled)
            {
                value = 0d;
            }

            var height = Math.Max(MinBarHeight, value * maxHeight);
            var x = i * slot + (slot - barWidth) / 2;
            var y = (maxHeight - height) / 2;

            var rect = new Rect(x, y, barWidth, height);
            context.DrawRectangle(brush, null, rect, radius, radius);
        }
    }
}
