namespace ClassShout.Core.Audio;

/// <summary>PCM 音频格式。</summary>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
/// <param name="BitsPerSample">位深。</param>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>
    /// 默认格式：16 kHz / 单声道 / 16 bit。
    /// 人声喊话足够清晰，且 32 KB/s 在局域网上几乎不占带宽，无需引入编解码器依赖。
    /// 若日后要上公网，可在此处替换为 Opus 之类的压缩格式。
    /// </summary>
    public static AudioFormat Default { get; } = new(16000, 1, 16);

    public int BlockAlign => Channels * BitsPerSample / 8;

    public int BytesPerSecond => SampleRate * BlockAlign;

    public int DurationMsOf(int byteCount) =>
        BytesPerSecond == 0 ? 0 : (int)(byteCount * 1000L / BytesPerSecond);

    public int BytesForDuration(int milliseconds) => BytesPerSecond * milliseconds / 1000;

    /// <summary>
    /// 这个格式能不能拿去做播放。
    ///
    /// 采样率、声道数、位深都来自网络对端，教室端不能假定它们是合理的。
    /// NAudio 的 WaveFormat 构造函数会校验参数并在不合法时抛异常，
    /// 而构造点跑在 UI 线程上（收到 audioStart 后要立刻建播放器），
    /// 于是对端只要发一个 Channels = 0 就能把整个教室端进程打掉 ——
    /// 一个畸形包换一次服务中断，代价完全不对等。
    ///
    /// 所以先在这里挡一道，把"能不能播"的判断收在协议边界上。
    /// </summary>
    public bool IsSupported =>
        SampleRate is >= 8000 and <= 192_000
        && Channels is >= 1 and <= 2
        && BitsPerSample is 8 or 16 or 24 or 32;

    public override string ToString() => $"{SampleRate} Hz / {Channels} 声道 / {BitsPerSample} bit";
}
