using System.Buffers.Binary;

namespace ClassShout.Core.Audio;

/// <summary>
/// 最小可用的 WAV（RIFF/PCM）编解码。
///
/// 定时语音需要"把一段录好的声音存起来、到点再放出去"，中间必然经过一次落盘 ——
/// 存裸 PCM 的话格式得另找地方记，而**格式丢了就没法播**（教室端拿到 16 kHz 的字节
/// 当成 48 kHz 放，出来的是快进的声音）。WAV 自带格式，一个文件就够。
///
/// 只做 PCM（format 1）：教室端播的就是它，其他编码还要带解码器，不值得。
/// </summary>
public static class WavCodec
{
    /// <summary>标准 44 字节头。</summary>
    public const int HeaderBytes = 44;

    /// <summary>把一段 PCM 包成 WAV。</summary>
    public static byte[] Encode(AudioFormat format, ReadOnlySpan<byte> pcm)
    {
        var buffer = new byte[HeaderBytes + pcm.Length];
        var span = buffer.AsSpan();

        var byteRate = format.SampleRate * format.Channels * (format.BitsPerSample / 8);
        var blockAlign = (short)(format.Channels * (format.BitsPerSample / 8));

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(span[8..]);

        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);                                  // fmt 块长度
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);                                   // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)format.BitsPerSample);

        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[HeaderBytes..]);

        return buffer;
    }

    /// <summary>
    /// 读一个 WAV，取出格式与 PCM。
    ///
    /// 宽容地跳过 fmt 与 data 之间的其他块（LIST/fact 之类很常见），
    /// 但**不**猜格式：读不懂就返回 false，让调用方说一句人话，
    /// 而不是放出一段没人听得懂的噪声。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> wav, out AudioFormat format, out byte[] pcm)
    {
        format = AudioFormat.Default;
        pcm = [];

        if (wav.Length < HeaderBytes ||
            !wav[..4].SequenceEqual("RIFF"u8) ||
            !wav[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var channels = 0;
        var sampleRate = 0;
        var bits = 0;
        var seenFormat = false;

        var offset = 12;

        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);

            if (size < 0 || offset + 8 + size > wav.Length)
            {
                // 块长度越界：文件被截断过。已经读到 fmt 就用已读到的部分，
                // 否则只能认输 —— 数据块不完整时宁可当它不存在。
                break;
            }

            var body = wav.Slice(offset + 8, size);

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(body);
                if (audioFormat != 1)
                {
                    return false;
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
                seenFormat = channels > 0 && sampleRate > 0 && bits > 0;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!seenFormat)
                {
                    return false;
                }

                format = new AudioFormat(sampleRate, channels, bits);
                pcm = body.ToArray();
                return true;
            }

            // 块按偶数字节对齐
            offset += 8 + size + (size % 2);
        }

        return false;
    }
}
