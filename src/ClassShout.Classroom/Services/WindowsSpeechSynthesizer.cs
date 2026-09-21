using System.Speech.Synthesis;
using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 基于 Windows SAPI（System.Speech）的系统语音合成。
///
/// 用同步 Speak 而不是 SpeakAsync：SpeakAsync 依赖 COM 消息泵，
/// 在没有 UI 消息循环的线程上会静默不发声；同步版跑在后台线程里更可靠，
/// 配合 SemaphoreSlim 串行化，天然形成朗读队列。
/// </summary>
public sealed class WindowsSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly SpeechSynthesizer _synthesizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private volatile bool _speaking;
    private volatile bool _disposed;

    public WindowsSpeechSynthesizer()
    {
        _synthesizer = new SpeechSynthesizer();
        _synthesizer.SetOutputToDefaultAudioDevice();

        // 朗读到句子结束时触发，用于让界面状态及时回落
        _synthesizer.SpeakCompleted += (_, _) => SetSpeaking(false);
    }

    /// <summary>朗读状态变化，参数为是否正在朗读。</summary>
    public event EventHandler<bool>? SpeakingChanged;

    public IReadOnlyList<string> GetVoices()
        => _synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>取第一个中文语音，用于首次启动时的默认值。</summary>
    public string? GetDefaultChineseVoice()
        => _synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.VoiceInfo.Name)
            .FirstOrDefault();

    public bool IsSpeaking => _speaking;

    public async Task SpeakAsync(string text, SpeechRequestOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            ApplyOptions(options);
            SetSpeaking(true);

            // 同步阻塞直到读完，因此必须放在线程池线程上，避免卡住 UI
            await Task.Run(() => _synthesizer.Speak(text), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被取消属于正常流程
        }
        finally
        {
            SetSpeaking(false);
            _gate.Release();
        }
    }

    private void ApplyOptions(SpeechRequestOptions options)
    {
        _synthesizer.Rate = Math.Clamp(options.Rate, -10, 10);
        _synthesizer.Volume = Math.Clamp(options.Volume, 0, 100);

        if (string.IsNullOrWhiteSpace(options.VoiceName))
        {
            return;
        }

        try
        {
            _synthesizer.SelectVoice(options.VoiceName);
        }
        catch (ArgumentException)
        {
            // 语音不存在（例如教师端指定了本机没有的语音），保持当前语音继续朗读
        }
    }

    private void SetSpeaking(bool value)
    {
        lock (_stateLock)
        {
            if (_speaking == value)
            {
                return;
            }

            _speaking = value;
        }

        SpeakingChanged?.Invoke(this, value);
    }

    public void Stop()
    {
        try
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
        catch (InvalidOperationException)
        {
            // 没有正在进行的朗读
        }

        SetSpeaking(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _synthesizer.Dispose();
        _gate.Dispose();
    }
}
