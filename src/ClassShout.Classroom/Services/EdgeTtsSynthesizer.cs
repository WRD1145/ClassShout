using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;


/// <summary>
/// 带自动回落的朗读合成器。
///
/// Edge 在线语音的音质比系统 SAPI 高一个档次，但它是**在线**的。
/// 教室网经常是隔离的，所以不能把"能不能出声"押在一条外网链路上：
/// 这里每次朗读都先试 Edge，失败就地回落到系统语音，
/// 并把"这次实际用了哪个"记下来供界面显示 ——
/// 老师需要知道"为什么今天的音质变差了"，而不是毫无线索。
///
/// 回落是**静默**的：喊话是实时动作，为了一个音质问题弹错误框、
/// 或者干脆不发声，都比"用差一点的音色念出来"更糟。
/// </summary>
public sealed class EdgeTtsSynthesizer : ISpeechSynthesizer
{
    private readonly EdgeTtsClient _client;
    private readonly ISpeechSynthesizer _systemVoice;
    private readonly NAudioLoopbackPlayer _player = new();
    private readonly Lock _stateLock = new();

    private volatile bool _speaking;
    private volatile bool _disposed;

    public EdgeTtsSynthesizer(EdgeTtsClient client, ISpeechSynthesizer systemVoice)
    {
        _client = client;
        _systemVoice = systemVoice;

        _systemVoice.SpeakingChanged += (_, speaking) => SetSpeaking(speaking);
    }

    /// <summary>最近一次朗读实际用了哪个引擎。界面上显示它。</summary>
    public string LastEngineText { get; private set; } = "尚未朗读";

    public event EventHandler<bool>? SpeakingChanged;

    /// <summary>Edge 最近一次是否可用。界面据此提示"当前连不上在线语音"。</summary>
    public bool EdgeReachable { get; private set; } = true;

    public bool IsSpeaking => _speaking;

    /// <summary>用户选择的引擎。默认系统语音：它不依赖外网，一定能用。</summary>
    public SpeechEngine Engine { get; set; } = SpeechEngine.System;

    /// <summary>Edge 音色名。为空时用默认中文女声。</summary>
    public string? EdgeVoice { get; set; }

    /// <summary>Edge 的音色列表（拉不到就是空，界面据此回落到系统语音）。</summary>
    public async Task<IReadOnlyList<EdgeVoiceInfo>> GetEdgeVoicesAsync(CancellationToken cancellationToken = default)
        => await _client.GetVoicesAsync(cancellationToken).ConfigureAwait(false);

    public IReadOnlyList<string> GetVoices() => _systemVoice.GetVoices();

    public string? GetDefaultChineseVoice() => _systemVoice.GetDefaultChineseVoice();

    public async Task SpeakAsync(
        string text,
        SpeechRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 用户选了系统语音就直接走它，不必先试一次 Edge ——
        // 那样每次朗读都要多等一个网络往返的超时，而用户已经明确说了不用在线语音。
        if (Engine != SpeechEngine.Edge)
        {
            LastEngineText = "系统语音";
            await _systemVoice.SpeakAsync(text, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await TrySpeakWithEdgeAsync(text, options, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // 走到这里说明 Edge 这条路不通：连不上、被拒、或者解不出音频。
        // 回落是这里存在的全部意义，所以不再往上抛异常。
        LastEngineText = EdgeReachable ? "系统语音（在线语音解码失败）" : "系统语音（连不上在线语音）";

        try
        {
            await _systemVoice.SpeakAsync(text, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            LastEngineText = "朗读失败（系统语音也不可用）";
        }
    }

    private async Task<bool> TrySpeakWithEdgeAsync(
        string text,
        SpeechRequestOptions options,
        CancellationToken cancellationToken)
    {
        byte[] mp3;

        try
        {
            // 界面上给的是 -10~10 的档位，Edge 要的是百分比。
            // 一档约合 10%，是照着"听感差不多"调的，不是精确换算。
            var rate = Math.Clamp(options.Rate, -10, 10) * 10;
            var volume = Math.Clamp(options.Volume, 0, 100) - 100;

            // 用 Edge 自己的音色名，而不是 options.VoiceName ——
            // 后者装的是系统语音的名字（如 "Microsoft Huihui Desktop"），
            // 拿去问 Edge 只会得到一个"音色不存在"的错误。
            mp3 = await _client
                .SynthesizeAsync(text, EdgeVoice ?? EdgeTtsClient.DefaultVoice, rate, volume, cancellationToken)
                .ConfigureAwait(false);

            EdgeReachable = true;
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException
                                      or HttpRequestException
                                      or TaskCanceledException
                                      or OperationCanceledException
                                      or InvalidOperationException)
        {
            EdgeReachable = false;
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var decoded = Mp3AudioDecoder.TryDecode(mp3);
        if (decoded is not { } audio || audio.Pcm.Length == 0)
        {
            return false;
        }

        SetSpeaking(true);

        try
        {
            var format = new AudioFormat(audio.SampleRate, audio.Channels, audio.BitsPerSample);
            if (!format.IsSupported)
            {
                return false;
            }

            _player.Start(format);
            _player.Write(audio.Pcm);
            await _player.CompleteAsync().ConfigureAwait(false);

            LastEngineText = "Edge 在线语音";
            return true;
        }
        finally
        {
            _player.Stop();
            SetSpeaking(false);
        }
    }

    public void Stop()
    {
        _systemVoice.Stop();
        _player.Stop();
        SetSpeaking(false);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _player.Dispose();
        _systemVoice.Dispose();
        _client.Dispose();
    }
}