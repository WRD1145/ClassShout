using Material.Icons;
using SkiaSharp;

namespace ClassShout.IconGen;

/// <summary>
/// 程序化生成应用图标，一次产出 Windows 的 .ico 与 Android 的各密度 mipmap。
///
/// 图形直接取自 Material.Icons 的路径数据，配色取自 MD3 基线色板，
/// 因此图标与界面属于同一套设计语言，而不是外挂进来的素材。
/// </summary>
internal static class Program
{
    /// <summary>MD3 基线主色，教师端使用。</summary>
    private static readonly SKColor Primary = new(0x67, 0x50, 0xA4);

    /// <summary>MD3 基线次级色，教室端使用（与教师端区分，便于任务栏/桌面上一眼分辨）。</summary>
    private static readonly SKColor Secondary = new(0x62, 0x5B, 0x71);

    private static readonly SKColor OnColor = SKColors.White;

    /// <summary>Windows 图标内嵌的尺寸。16/32 用于任务栏与小视图，256 用于大图标视图。</summary>
    private static readonly int[] WindowsSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>Android mipmap 密度对应的像素尺寸。</summary>
    private static readonly (string Folder, int Size)[] AndroidDensities =
    [
        ("mipmap-mdpi", 48),
        ("mipmap-hdpi", 72),
        ("mipmap-xhdpi", 96),
        ("mipmap-xxhdpi", 144),
        ("mipmap-xxxhdpi", 192),
    ];

    public static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var assetsDirectory = Path.Combine(root, "assets");
        var androidResources = Path.Combine(root, "src", "ClassShout.Teacher.Android", "Resources");

        Directory.CreateDirectory(assetsDirectory);

        var targets = new[]
        {
            new IconTarget("teacher", "教师端", MaterialIconKind.Bullhorn, Primary),
            new IconTarget("classroom", "教室端", MaterialIconKind.School, Secondary),
        };

        foreach (var target in targets)
        {
            WriteWindowsIcon(target, assetsDirectory);
            WriteStorePng(target, assetsDirectory);
        }

        WriteAndroidIcons(targets[0], androidResources);

        Console.WriteLine();
        Console.WriteLine("图标生成完毕。");
        return 0;
    }

    private sealed record IconTarget(string Slug, string DisplayName, MaterialIconKind Glyph, SKColor Background);

    // ======================== Windows ========================

    private static void WriteWindowsIcon(IconTarget target, string assetsDirectory)
    {
        var images = WindowsSizes
            .Select(size => (Size: size, Png: Render(target, size, circular: false)))
            .ToList();

        var path = Path.Combine(assetsDirectory, $"{target.Slug}.ico");
        using var stream = File.Create(path);
        WriteIco(stream, images);

        Console.WriteLine($"[Windows] {target.DisplayName} → {path}");
        Console.WriteLine($"          尺寸：{string.Join(" / ", WindowsSizes)}，共 {new FileInfo(path).Length / 1024} KB");
    }

    /// <summary>额外导出一张 512 的大图，方便日后做商店素材或文档配图。</summary>
    private static void WriteStorePng(IconTarget target, string assetsDirectory)
    {
        var path = Path.Combine(assetsDirectory, $"{target.Slug}-512.png");
        File.WriteAllBytes(path, Render(target, 512, circular: false));
        Console.WriteLine($"[预览]    {target.DisplayName} → {path}");
    }

    /// <summary>
    /// 写 ICO 文件。Vista 起 ICO 允许直接内嵌 PNG，因此不必手工拼 BMP 与掩码，
    /// 各尺寸的 alpha 通道也能完整保留。
    /// </summary>
    private static void WriteIco(Stream stream, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);                 // 保留字段
        writer.Write((ushort)1);                 // 类型：1 = 图标
        writer.Write((ushort)images.Count);

        var dataOffset = 6 + images.Count * 16;

        foreach (var (size, png) in images)
        {
            // 256 在 ICO 目录项里用 0 表示
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);               // 调色板数量，真彩色填 0
            writer.Write((byte)0);               // 保留
            writer.Write((ushort)1);             // 色彩平面
            writer.Write((ushort)32);            // 位深
            writer.Write(png.Length);
            writer.Write(dataOffset);

            dataOffset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            writer.Write(png);
        }
    }

    // ======================== Android ========================

    private static void WriteAndroidIcons(IconTarget target, string androidResources)
    {
        if (!Directory.Exists(androidResources))
        {
            Console.WriteLine($"[跳过]    Android 资源目录不存在：{androidResources}");
            return;
        }

        Console.WriteLine();
        foreach (var (folder, size) in AndroidDensities)
        {
            var directory = Path.Combine(androidResources, folder);
            Directory.CreateDirectory(directory);

            // 常规图标：MD3 风格大圆角方形
            File.WriteAllBytes(Path.Combine(directory, "ic_launcher.png"), Render(target, size, circular: false));

            // 圆形图标：供部分启动器使用
            File.WriteAllBytes(Path.Combine(directory, "ic_launcher_round.png"), Render(target, size, circular: true));
        }

        Console.WriteLine($"[Android] {target.DisplayName} → {AndroidDensities.Length} 档密度 × 2 种形状");
        Console.WriteLine($"          输出：{Path.Combine(androidResources, "mipmap-xxxhdpi")}");
    }

    // ======================== 绘制 ========================

    /// <summary>
    /// 画一枚图标：底色块 + 居中图形。
    /// 每个尺寸都独立渲染而不是缩放，小尺寸下笔画才不会糊。
    /// </summary>
    private static byte[] Render(IconTarget target, int size, bool circular)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 底色块
        using (var background = new SKPaint
               {
                   Color = target.Background,
                   IsAntialias = true,
                   Style = SKPaintStyle.Fill,
               })
        {
            if (circular)
            {
                canvas.DrawCircle(size / 2f, size / 2f, size / 2f, background);
            }
            else
            {
                // MD3 的大圆角形状（约等于 Shape.ExtraLarge 的观感）
                var radius = size * 0.22f;
                canvas.DrawRoundRect(SKRect.Create(size, size), radius, radius, background);
            }
        }

        // 图形
        var pathData = MaterialIconDataProvider.GetData(target.Glyph);
        using var path = SKPath.ParseSvgPathData(pathData);

        if (path is not null && path.Bounds.Width > 0)
        {
            var bounds = path.Bounds;

            // 图形占图标约 54%，四周留白符合 MD3 图标的安全边距
            const float glyphRatio = 0.54f;
            var scale = size * glyphRatio / Math.Max(bounds.Width, bounds.Height);

            canvas.Save();
            canvas.Translate(size / 2f, size / 2f);
            canvas.Scale(scale);
            canvas.Translate(-bounds.MidX, -bounds.MidY);

            using var glyphPaint = new SKPaint
            {
                Color = OnColor,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            canvas.DrawPath(path, glyphPaint);
            canvas.Restore();
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
