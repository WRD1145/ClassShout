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
        if (_buffer is null || pcm.IsEmpty)
        {
            return;
        }

        // BufferedWaveProvider 只接受数组，这里必须复制一份
        var copy = pcm.ToArray();
        _buffer.AddSamples(copy, 0, copy.Length);
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
