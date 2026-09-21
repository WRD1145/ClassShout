using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace ClassShout.DesignPreview;

/// <summary>
/// 渲染结果校验器。
///
/// 为什么需要它：预览工具的价值不只是"导出 PNG"，更要在界面改动后自动判断
/// 渲染是否正常。最常见的两种故障是 —— 界面整片空白（样式没加载）和颜色令牌
/// 没解析到（配色全错）。这两种都能从像素统计里看出来，不需要人逐张看图。
///
/// 判据说明（两条都刻意留了余量，避免误报）：
///   内容是否正常：不同颜色数 > 200。空白页只有一两种颜色。
///   布局是否展开：占比最高的单色 < 85%。界面本来就该有大面积纯色（背景、卡片），
///                 但如果一种颜色占了九成以上，多半是内容根本没布局出来。
/// </summary>
internal static class FrameInspector
{
    /// <summary>把一帧的像素统计打印到控制台，并返回是否通过校验。</summary>
    public static bool Report(string label, WriteableBitmap frame, IReadOnlyList<(string Name, uint Rgb)> expected)
    {
        using var locked = frame.Lock();

        var width = locked.Size.Width;
        var height = locked.Size.Height;
        var rowBytes = locked.RowBytes;
        var buffer = new byte[rowBytes * height];
        Marshal.Copy(locked.Address, buffer, 0, buffer.Length);

        // Skia 在 Windows 上通常是 Rgba8888，但不同后端可能给 Bgra8888，
        // 这里按实际格式决定通道顺序，避免红蓝颠倒导致误判。
        var isBgra = locked.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var rIndex = isBgra ? 2 : 0;
        var bIndex = isBgra ? 0 : 2;

        var counts = new Dictionary<uint, int>(capacity: 4096);
        var total = width * height;

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var offset = rowStart + x * 4;
                var rgb = (uint)(
                    (buffer[offset + rIndex] << 16) |
                    (buffer[offset + 1] << 8) |
                    buffer[offset + bIndex]);

                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, rgb, out _);
                slot++;
            }
        }

        var ordered = counts.OrderByDescending(pair => pair.Value).ToList();
        var dominantShare = ordered[0].Value / (double)total;
        var topFiveShare = ordered.Take(5).Sum(pair => pair.Value) / (double)total;

        Console.WriteLine();
        Console.WriteLine($"── {label} ─────────────────────────────");
        Console.WriteLine($"  尺寸        : {width} × {height}，像素 {total:N0}");
        Console.WriteLine($"  像素格式    : {locked.Format}");
        Console.WriteLine($"  不同颜色数  : {counts.Count:N0}");
        Console.WriteLine($"  主色占比    : {dominantShare:P1}（前五色合计 {topFiveShare:P1}）");
        Console.WriteLine("  主要颜色    :");
        foreach (var (rgb, count) in ordered.Take(4))
        {
            Console.WriteLine($"      #{rgb:X6}  {count / (double)total,7:P2}");
        }

        var allFound = true;
        Console.WriteLine("  期望令牌    :");
        foreach (var (name, rgb) in expected)
        {
            var found = counts.TryGetValue(rgb, out var count);
            allFound &= found;
            Console.WriteLine(found
                ? $"      [命中] {name,-30} #{rgb:X6}  像素 {count:N0}"
                : $"      [缺失] {name,-30} #{rgb:X6}  该颜色未出现在渲染结果中");
        }

        var hasContent = counts.Count > 200;
        var hasLayout = dominantShare < 0.85;

        Console.WriteLine(
            $"  判定        : 内容{(hasContent ? "正常" : "疑似空白")} / " +
            $"布局{(hasLayout ? "正常" : "疑似未展开")} / " +
            $"令牌{(allFound ? "全部命中" : "有缺失")}");

        return hasContent && hasLayout && allFound;
    }
}
