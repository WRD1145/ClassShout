using NAudio.Wave;

namespace ClassShout.Classroom.Services;

/// <summary>解码结果：PCM 数据与它的格式。</summary>
/// <param name="SampleRate">采样率。</param>
/// <param name="Channels">声道数。</param>
/// <param name="BitsPerSample">位深。</param>
/// <param name="Pcm">交错排列的 PCM 数据。</param>
public readonly record struct DecodedAudio(int SampleRate, int Channels, int BitsPerSample, byte[] Pcm);

/// <summary>
/// 把 MP3 解码成 PCM。
///
/// 为什么需要它：Edge 在线语音只会回 MP3（接口只提供这一种输出），
/// 而教室端的播放链路是"原始 PCM 送进带缓冲的播放器"，中间必须有人解码。
///
/// 一次性整段解码而不是流式：一句话的 MP3 只有几十 KB，
/// 攒完再解能避开帧边界与"解码器还没就绪就来数据"这两类麻烦，
/// 代价只是首字延迟多几百毫秒 —— 在线语音本来就是可选引擎，音质优先。
///
/// 用 NAudio 的 Mp3FileReader 而不是自己拆帧配帧解码器：
/// 后者（Mp3FrameDecompressor）在 NAudio 2.2.1 的公开面里并没有暴露出来，
/// 而 Mp3FileReader 收一个可定位的流就能把整段解成 PCM，正是这里要的。
///
/// 它底层走 Windows 的 ACM / Media Foundation，所以这个实现是 Windows 专用的。
/// 将来做 Linux 客户端时换一个纯托管解码器（例如 NLayer）替换这个类即可，
/// 上层只认 DecodedAudio 这个结果类型。
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
            using var reader = new Mp3FileReader(input);

            var format = reader.WaveFormat;
            if (format is null || format.SampleRate <= 0 || format.Channels <= 0)
            {
                return null;
            }

            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];

            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }

            if (output.Length == 0)
            {
                return null;
            }

            return new DecodedAudio(format.SampleRate, format.Channels, format.BitsPerSample, output.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or ArgumentException
                                      or FormatException
                                      or NAudio.MmException
                                      or EndOfStreamException)
        {
            // 解码器不可用（系统被裁剪过、缺少 ACM 解码器）时返回 null，
            // 让上层把引擎回落到系统语音，而不是让教室端直接哑掉。
            return null;
        }
    }
}