using ClassShout.Core.Audio;
using ClassShout.Teacher.Services;
using NAudio.Wave;

namespace ClassShout.Teacher.Desktop.Services;

/// <summary>
/// 桌面头（Windows）的麦克风采集，基于 NAudio 的 WaveInEvent。
///
/// 与 Android 头的关系：两者实现同一个 <see cref="IAudioRecorder"/>，
/// 共享 UI 层对具体平台零感知。桌面头主要用于在 PC 上调试手机界面与语音链路。
/// </summary>
public sealed class NAudioRecorder : IAudioRecorder
{
    /// <summary>每片 20 毫秒：与协议分片粒度一致，端到端延迟最低。</summary>
    private const int BufferMilliseconds = 20;

    private Action<ReadOnlyMemory<byte>>? _onData;
    private WaveInEvent? _waveIn;

    public NAudioRecorder()
    {
        Format = ResolveFormat();
    }

    public AudioFormat Format { get; }

    public bool IsRecording => _waveIn is not null;

    public event EventHandler<float>? LevelChanged;

    /// <summary>
    /// 采集中途失败。桌面用 NAudio 的 DataAvailable，回调里抛出即意味着设备被拔掉
    /// 或被独占，同样要让界面知道 —— 否则计时器还在走，而声音早就没了。
    /// </summary>
    public event EventHandler<string>? Failed;

    /// <summary>
    /// 挑一个设备确实支持的格式。
    /// 采集格式会通过 audioStart 告诉教室端，教室端按同样的格式播放，
    /// 所以这里不强行要求 16 kHz —— 设备支持就用，不支持就用设备最高的采样率。
    /// </summary>
    /// <summary>
    /// 统一用 16 kHz 单声道。
    ///
    /// 注释里解释一下为什么不探测设备能力：WaveInCapabilities.SupportsWaveFormat 收的是
    /// SupportedWaveFormat 枚举，而那套枚举只覆盖 8/11.025/22.05/44.1/48 kHz，没有 16 kHz，
    /// 没法用它判断。Windows 的 waveIn 在共享模式下会自行重采样，16 kHz 单声道在
    /// 绝大多数声卡上都可用；万一设备拒绝，StartRecording 会抛出，界面会显示明确错误。
    /// </summary>
    private static AudioFormat ResolveFormat() => AudioFormat.Default;

    public Task StartAsync(Action<ReadOnlyMemory<byte>> onData, CancellationToken cancellationToken = default)
    {
        if (_waveIn is not null)
        {
            return Task.CompletedTask;
        }

        _onData = onData;

        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(Format.SampleRate, Format.BitsPerSample, Format.Channels),
            BufferMilliseconds = BufferMilliseconds,
            NumberOfBuffers = 4,
        };

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;

        // 必须先登记再启动：否则启动瞬间可能先抛错，而清理路径却以为它没建起来
        _waveIn = waveIn;

        try
        {
            waveIn.StartRecording();
        }
        catch
        {
            _waveIn = null;
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        // 必须复制：NAudio 会复用同一个缓冲区，直接引用会被下一片覆盖
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        _onData?.Invoke(chunk);

        var level = (float)PcmLevel.Compute(chunk);
        LevelChanged?.Invoke(this, level);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        LevelChanged?.Invoke(this, 0f);

        if (e.Exception is not null)
        {
            // 原来只把电平清零就算了，界面那边一无所知：
            // 计时器继续走、按钮还写着"正在录音"，而麦克风早就停了。
            Failed?.Invoke(this, $"麦克风已停止：{e.Exception.Message}");
        }
    }

    public Task StopAsync()
    {
        var waveIn = _waveIn;
        if (waveIn is null)
        {
            return Task.CompletedTask;
        }

        _waveIn = null;
        _onData = null;

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;

        try
        {
            waveIn.StopRecording();
        }
        catch (NAudio.MmException)
        {
            // 设备已被移除等情况忽略
        }

        waveIn.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        LevelChanged = null;
    }
}
