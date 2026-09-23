using System.Text.Json.Serialization;

namespace ClassShout.Core.Audio;

/// <summary>教室端用哪一种朗读引擎。</summary>
public enum SpeechEngine
{
    /// <summary>
    /// 系统语音（Windows SAPI / Linux 上的 spd-say 之类）。
    /// 离线可用，是所有环境下的保底选项。
    /// </summary>
    System = 0,

    /// <summary>
    /// 微软 Edge 在线语音。自然度明显更高，但需要教室端能上外网。
    /// 连不上时会自动回落到系统语音，不会让教室端变哑。
    /// </summary>
    Edge = 1,
}

/// <summary>
/// 教室端的朗读设置。
///
/// 与联网配置分开存：换服务器地址和换朗读音色是两件事，
/// 备份/清理时的处置也不同，混在一个文件里只会让以后难办。
/// </summary>
public sealed class ClassroomSpeechSettings
{
    /// <summary>选择的引擎。默认系统语音 —— 它不依赖外网，一定能用。</summary>
    public SpeechEngine Engine { get; set; } = SpeechEngine.System;

    /// <summary>Edge 音色名（如 zh-CN-XiaoxiaoNeural）。</summary>
    public string? EdgeVoice { get; set; }

    /// <summary>是否已经提示过"在线语音连不上"。避免每次朗读都刷一条日志。</summary>
    [JsonIgnore]
    public bool EdgeFallbackNoted { get; set; }
}