using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassShout.Design.Controls;

namespace ClassShout.DesignPreview;

/// <summary>
/// 设计系统画廊。
/// 除了展示各组件，还把波形控件喂一串合成电平，
/// 这样截图里能看到它在真实数据下的样子，而不是一条直线。
/// </summary>
public partial class Showcase : UserControl
{
    public Showcase()
    {
        InitializeComponent();
        FeedSyntheticWave();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FeedSyntheticWave()
    {
        if (this.FindControl<Md3AudioWave>("Wave") is not { } wave)
        {
            return;
        }

        // 一段有起伏的合成波形，模拟真实喊话时的音量变化
        double[] samples =
        [
            0.12, 0.30, 0.58, 0.82, 0.64, 0.41, 0.72, 0.95, 0.78, 0.52, 0.35, 0.61,
            0.88, 0.70, 0.44, 0.28, 0.55, 0.79, 0.91, 0.66, 0.38, 0.22, 0.48, 0.74,
            0.86, 0.60, 0.33, 0.19, 0.42, 0.68, 0.90, 0.75, 0.50, 0.31, 0.57, 0.83,
        ];

        foreach (var sample in samples)
        {
            wave.Level = sample;
        }
    }
}
