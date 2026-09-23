namespace ClassShout.Core.Audio;

/// <summary>音频输入（麦克风）抽象。Android 头用 AudioRecord，桌面头用 NAudio 实现。</summary>
public interface IAudioRecorder : IAsyncDisposable
{
    /// <summary>当前是否正在采集。</summary>
    bool IsRecording { get; }

    /// <summary>采集格式，由实现决定（通常为 16 kHz 单声道 16 bit）。</summary>
    AudioFormat Format { get; }

    /// <summary>音量电平（0~1），用于界面上的波形/电平表。</summary>
    event EventHandler<float>? LevelChanged;

    /// <summary>
    /// 采集中途失败（设备被抢占、底层读取报错等）。
    ///
    /// 必须有这条通道：采集循环跑在后台线程上，出错时它只能自己退出。
    /// 没有这个事件的话，界面会一直显示"正在录音"、计时器继续走，
    /// 而实际上一个字节都没采到 —— 老师以为自己在喊话，教室里一片安静。
    /// </summary>
    event EventHandler<string>? Failed;

    /// <summary>开始采集，每段 PCM 通过 <paramref name="onData"/> 回调。</summary>
    Task StartAsync(Action<ReadOnlyMemory<byte>> onData, CancellationToken cancellationToken = default);

    /// <summary>停止采集。</summary>
    Task StopAsync();
}

/// <summary>音频输出（扬声器）抽象。停止是同步的，所以用 IDisposable 而非 IAsyncDisposable。</summary>
public interface IAudioPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>开始一次播放会话。</summary>
    void Start(AudioFormat format);

    /// <summary>送入一段 PCM。</summary>
    void Write(ReadOnlySpan<byte> pcm);

    /// <summary>本次数据已送完，等待播完缓冲。</summary>
    Task CompleteAsync();

    /// <summary>立即停止并清空缓冲。</summary>
    void Stop();
}

/// <summary>系统 TTS 抽象（教室端用 Windows SAPI 实现）。</summary>
public interface ISpeechSynthesizer : IDisposable
{
    /// <summary>当前系统可用的语音名称。</summary>
    IReadOnlyList<string> GetVoices();

    /// <summary>是否正在朗读。</summary>
    bool IsSpeaking { get; }

    /// <summary>朗读一段文字。</summary>
    Task SpeakAsync(string text, SpeechRequestOptions options, CancellationToken cancellationToken = default);

    /// <summary>立即停止朗读。</summary>
    void Stop();
}

/// <summary>朗读参数。</summary>
/// <param name="Rate">语速，-10 ~ 10。</param>
/// <param name="Volume">音量，0 ~ 100。</param>
/// <param name="VoiceName">指定语音；为空用默认。</param>
public readonly record struct SpeechRequestOptions(int Rate, int Volume, string? VoiceName);
