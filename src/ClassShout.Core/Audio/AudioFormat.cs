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

    public override string ToString() => $"{SampleRate} Hz / {Channels} 声道 / {BitsPerSample} bit";
}
