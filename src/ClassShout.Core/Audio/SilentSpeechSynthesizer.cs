namespace ClassShout.Core.Audio;

/// <summary>
/// 什么也不做的朗读引擎。
///
/// 用途：Linux 上可能既没装 spd-say 也没装 espeak，而契约要求
/// <see cref="ISpeechSynthesizer"/> 非空。用一个空实现兜底之后，
/// 上层不必到处判 null，只需要在界面上如实显示"系统语音不可用"——
/// 而那句显示的依据来自 ClassroomPlatform.HasSystemSpeech。
///
/// 刻意不在这里偷偷抛异常或者记日志：调用方知道自己在用什么。
/// </summary>
public sealed class SilentSpeechSynthesizer : ISpeechSynthesizer
{
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsSpeaking => false;

    public IReadOnlyList<string> GetVoices() => [];

    public string? GetDefaultChineseVoice() => null;

    public Task SpeakAsync(string text, SpeechRequestOptions options, CancellationToken cancellationToken = default)
    {
        // 什么都不做是刻意的：这条路径只应该在"本机没有系统朗读"时被走到，
        // 而那时真正该发声的是 Edge 在线语音。
        _ = SpeakingChanged;
        return Task.CompletedTask;
    }

    public void Stop() { }

    public void Dispose() { }
}