using NLayer;

namespace ClassShout.Core.Audio;

/// <summary>解码结果：PCM 数据与它的格式。</summary>
/// <param name="SampleRate">采样率。</param>
/// <param name="Channels">声道数。</param>
/// <param name="BitsPerSample">位深。</param>
/// <param name="Pcm">交错排列的 PCM 数据。</param>
public readonly record struct DecodedAudio(int SampleRate, int Channels, int BitsPerSample, byte[] Pcm);

/// <summary>
/// 把 MP3 解码成 PCM。
///
/// 为什么需要它：Edge 在线语音只回 MP3（接口只提供这一种输出），
/// 而两端的播放链路都是"原始 PCM 送进播放设备"，中间必须有人解码。
///
/// 用 NLayer 而不是 NAudio 的 Mp3FileReader：后者走 Windows 的 ACM / Media Foundation，
/// 在 Linux 上根本不工作。而 Edge 在线语音恰恰是 Linux 教室端最重要的朗读引擎
/// （Linux 上没有 SAPI 那类系统语音），所以解码必须是跨平台的。
/// 顺带也把 Windows 侧的这条路统一了：同一份解码代码，两端行为一致。
///
/// 一次性整段解码而不是流式：一句话的 MP3 只有几十 KB，
/// 攒完再解能避开帧边界与"解码器还没就绪就来数据"这两类麻烦，
/// 代价只是首字延迟多几百毫秒 —— 在线语音本来就是可选引擎，音质优先。
/// </summary>
public static class Mp3AudioDecoder
{
    /// <summary>解码失败返回 null，由调用方决定回落还是报错。</summary>
    public static DecodedAudio? TryDecode(byte[] mp3)
    {
        if (mp3.Length == 0)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(mp3);
            using var reader = new MpegFile(input);

            var sampleRate = reader.SampleRate;
            var channels = reader.Channels;

            if (sampleRate <= 0 || channels <= 0)
            {
                return null;
            }

            // NLayer 输出的是 float 采样，转成 16 bit 整数 PCM ——
            // 两端的播放设备吃的都是 16 bit，多出来的精度在这里没有意义，
            // 而 float 到设备的转换反而要额外一层。
            var samples = new List<byte>();
            var buffer = new float[16 * 1024];

            int read;
            while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var clamped = Math.Clamp(buffer[i], -1f, 1f);
                    var value = (short)(clamped * short.MaxValue);

                    samples.Add((byte)(value & 0xFF));
                    samples.Add((byte)((value >> 8) & 0xFF));
                }
            }

            var pcm = samples.ToArray();
            return pcm.Length == 0
                ? null
                : new DecodedAudio(sampleRate, channels, 16, pcm);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or ArgumentException
                                      or FormatException
                                      or EndOfStreamException
                                      or IndexOutOfRangeException)
        {
            // 数据不是合法的 MP3、或者解码器内部出错时返回 null，
            // 让上层把引擎回落到别的朗读方式，而不是让教室端直接哑掉。
            return null;
        }
    }
}