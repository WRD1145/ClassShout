using Avalonia.Media.Imaging;

namespace ClassShout.Teacher.Services;

/// <summary>一张准备发出去的图片。</summary>
/// <param name="Bytes">图片字节。</param>
/// <param name="ContentType">MIME 类型。</param>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="Compressed">是否经过压缩（界面上据此说明"已压缩"）。</param>
public sealed record PreparedImage(byte[] Bytes, string ContentType, int Width, int Height, bool Compressed)
{
    /// <summary>给人看的大小说明。</summary>
    public string SizeText
    {
        get
        {
            var kb = Bytes.Length / 1024.0;
            var size = kb >= 1024 ? $"{kb / 1024:0.##} MB" : $"{kb:0.#} KB";

            return Compressed
                ? $"{size} · 已压缩到 {Width}×{Height}"
                : $"{size} · {Width}×{Height}（原图）";
        }
    }
}

/// <summary>
/// 把用户选中的图片处理成"适合发到教室里"的样子。
///
/// 为什么一定要压缩而不能原样转发：手机拍一张照片就有三四兆，
/// 经中继发出去要切成几百个请求（服务器对单个请求体只有 16 KiB），
/// 教室里那台电脑还得先收完几百片才能显示 —— 而屏幕能呈现的细节远不到那个量级。
/// 压到最长边 1600 之后，一张课堂照片通常只剩一两百 KB，
/// 在教室的投影或大屏上完全看不出差别。
/// </summary>
public static class ShoutImage
{
    /// <summary>压缩后的最长边（像素）。</summary>
    public const int MaxEdge = 1600;

    /// <summary>JPEG 质量。</summary>
    public const int JpegQuality = 80;

    /// <summary>
    /// 小于这个体积、且尺寸也没超的原图直接发。
    ///
    /// 好处是避免"老师发一张截图，被重新编码一遍"——JPEG 是有损的，
    /// 截图上的文字被压一次就会发糊。省下的那点流量不值得。
    /// </summary>
    public const int PassThroughBytes = 400 * 1024;

    /// <summary>处理一张图片。无法识别为图片时返回 <c>null</c>。</summary>
    public static async Task<PreparedImage?> PrepareAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var raw = buffer.ToArray();

        if (raw.Length == 0)
        {
            return null;
        }

        // 先试着按原图解码：解不出来就不是图片，直接告诉调用方
        Bitmap decoded;
        try
        {
            using var probe = new MemoryStream(raw);
            decoded = new Bitmap(probe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }

        using (decoded)
        {
            var longest = Math.Max(decoded.PixelSize.Width, decoded.PixelSize.Height);

            if (raw.Length <= PassThroughBytes && longest <= MaxEdge)
            {
                return new PreparedImage(
                    raw,
                    GuessContentType(raw),
                    decoded.PixelSize.Width,
                    decoded.PixelSize.Height,
                    Compressed: false);
            }

            // 按最长边等比缩放。窄边跟着比例走，不会把图拉变形。
            Bitmap scaled;
            using (var input = new MemoryStream(raw))
            {
                scaled = decoded.PixelSize.Width >= decoded.PixelSize.Height
                    ? Bitmap.DecodeToWidth(input, MaxEdge, BitmapInterpolationMode.HighQuality)
                    : Bitmap.DecodeToHeight(input, MaxEdge, BitmapInterpolationMode.HighQuality);
            }

            using (scaled)
            {
                // 两种编码都试一遍，取小的那个。
                //
                // 为什么不能一律用 JPEG：课堂上的图片有两类 —— 手机拍的照片，和屏幕截图。
                // 照片用 JPEG 能小一个数量级；而截图/图表这类"大片纯色 + 锐利线条"的图，
                // JPEG 反而可能比 PNG 大（有损压缩对这类内容毫无优势），
                // 而且文字边缘还会发糊。多编码一次的代价是几百毫秒，
                // 换来的是"同一个文件不会因为选了图反而传得更久"。
                var jpeg = Encode(scaled, JpegQuality);
                var png = Encode(scaled, quality: null);

                var (bytes, contentType) = jpeg.Length <= png.Length
                    ? (jpeg, "image/jpeg")
                    : (png, "image/png");

                return new PreparedImage(
                    bytes,
                    contentType,
                    scaled.PixelSize.Width,
                    scaled.PixelSize.Height,
                    Compressed: true);
            }
        }
    }

    /// <summary>把位图编码成字节；quality 为 null 时编成 PNG。</summary>
    private static byte[] Encode(Bitmap bitmap, int? quality)
    {
        using var output = new MemoryStream();
        bitmap.Save(output, quality);
        return output.ToArray();
    }

    /// <summary>
    /// 按文件头猜 MIME。不用扩展名：老师从相册选出来的文件常常没有扩展名，
    /// 而 Content-Type 只是给对端一个提示，猜错不影响解码。
    /// </summary>
    private static string GuessContentType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 12 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        return "image/jpeg";
    }
}
