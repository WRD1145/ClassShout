using System.Buffers.Binary;

namespace ClassShout.Core.Protocol;

/// <summary>帧类型。</summary>
public enum FrameKind : byte
{
    /// <summary>负载是 UTF-8 JSON 控制消息。</summary>
    Control = 1,

    /// <summary>负载是裸 PCM 音频分片。</summary>
    Audio = 2,

    /// <summary>负载是图片字节流的一个分片（由 imageStart / imageEnd 界定首尾）。</summary>
    Image = 3,
}

/// <summary>一个完整的传输帧。</summary>
/// <param name="Kind">帧类型。</param>
/// <param name="Payload">负载内容（不含帧头）。</param>
public readonly record struct ShoutFrame(FrameKind Kind, byte[] Payload);

/// <summary>
/// TCP 分帧：<c>[4 字节大端长度][1 字节类型][负载]</c>，长度字段含类型字节。
/// 音频与控制共用一条连接，靠类型字节区分，避免额外端口和乱序问题。
/// </summary>
public static class FrameProtocol
{
    /// <summary>帧头长度：4 字节长度 + 1 字节类型。</summary>
    public const int HeaderLength = 5;

    public static async ValueTask WriteAsync(
        Stream stream,
        FrameKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var total = payload.Length + 1;
        if (total > ShoutProtocol.MaxFrameLength)
        {
            throw new InvalidOperationException($"帧长度 {total} 超出上限 {ShoutProtocol.MaxFrameLength}。");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)total);
        header[4] = (byte)kind;

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取一帧；对端正常关闭时返回 <c>null</c>。</summary>
    public static async ValueTask<ShoutFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var total = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (total is < 1 or > ShoutProtocol.MaxFrameLength)
        {
            throw new InvalidDataException($"收到非法帧长度 {total}，连接可能已错位。");
        }

        var payload = new byte[total - 1];
        if (payload.Length > 0 && !await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ShoutFrame((FrameKind)header[4], payload);
    }

    /// <summary>读满整个缓冲区；中途对端关闭返回 <c>false</c>。</summary>
    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
