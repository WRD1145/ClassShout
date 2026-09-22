using ClassShout.Core.Audio;
using NAudio.Wave;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 基于 NAudio 的流式音频播放器。
///
/// 教师端一边录一边发，教室端一边收一边放，所以必须用带缓冲的流式播放
/// （BufferedWaveProvider），而不是"收完整个文件再播"。
/// 缓冲设成 5 秒并开启溢出丢弃：网络抖动时宁可丢一点，也不要累积出越来越大的延迟。
/// </summary>
public sealed class NAudioLoopbackPlayer : IAudioPlayer
{
    private static readonly TimeSpan BufferDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(8);

    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public void Start(AudioFormat format)
    {
        Stop();

        // 格式来自网络对端，必须先确认它合法。
        // WaveFormat 的构造函数会校验并在不合法时抛异常，而这里跑在 UI 线程上，
        // 抛出去就是整个教室端进程退出 —— 一个畸形包换一次服务中断。
        if (!format.IsSupported)
        {
            throw new NotSupportedException($"不支持的音频格式：{format}。");
        }

        _buffer = new BufferedWaveProvider(new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels))
        {
            BufferDuration = BufferDuration,
            // 网络抖动时丢弃新数据而不是让延迟无限增长
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.Init(_buffer);
        _output.Play();
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        // 先把字段抓进局部变量再判断。
        //
        // 原来的写法是"检查 _buffer 非空，然后使用 _buffer"，
        // 而 Stop() 会在另一条线程（收到 audioEnd / 停止指令）上把它置空 ——
        // 中间那一下正好撞上就是 NullReferenceException，
        // 而且会直接抛进 TCP 读循环里，把整条连接带走。
        var buffer = _buffer;
        if (buffer is null || pcm.IsEmpty)
        {
            return;
        }

        try
        {
            // BufferedWaveProvider 只接受数组，这里必须复制一份
            var copy = pcm.ToArray();
            buffer.AddSamples(copy, 0, copy.Length);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // 同一时刻 Stop() 把底层设备释放掉了：这一片丢掉即可，不该影响接收
        }
    }

    public async Task CompleteAsync()
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow + DrainTimeout;
        while (buffer.BufferedBytes > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(40).ConfigureAwait(false);
        }

        Stop();
    }

    public void Stop()
    {
        try
        {
            _output?.Stop();
        }
        catch (NAudio.MmException)
        {
            // 声卡被占用或拔出时忽略
        }

        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
