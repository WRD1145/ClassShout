namespace ClassShout.Core.Protocol;

/// <summary>协议常量。教师端与教室端必须保持一致。</summary>
public static class ShoutProtocol
{
    /// <summary>当前协议版本。握手时不匹配会给出明确提示。</summary>
    public const int Version = 1;

    /// <summary>控制与音频数据共用的 TCP 端口（教室端监听）。</summary>
    public const int DefaultTcpPort = 45900;

    /// <summary>UDP 发现端口（教室端监听广播）。</summary>
    public const int DefaultDiscoveryPort = 45901;

    /// <summary>UDP 探测报文内容，兼容用固定字符串而非 JSON，便于抓包排查。</summary>
    public const string DiscoveryProbe = "CLASSSHOUT/DISCOVER/1";

    /// <summary>单帧负载上限 1 MiB，用于防御异常长度导致的巨量分配。</summary>
    public const int MaxFrameLength = 1024 * 1024;
}

/// <summary>控制消息基类。JSON 中以 <c>type</c> 字段区分具体类型。</summary>
public abstract class ShoutMessage
{
    /// <summary>线上判别值，由 <c>ShoutCodec</c> 写入 <c>type</c> 字段。</summary>
    public abstract string Type { get; }
}

/// <summary>教师端连接后的握手。<see cref="ProtocolVersion"/> 不匹配时教室端会拒收。</summary>
public sealed class HelloMessage : ShoutMessage
{
    public const string TypeName = "hello";
    public override string Type => TypeName;

    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = ShoutProtocol.Version;
}

/// <summary>文字喊话。教室端用系统 TTS 朗读。</summary>
public sealed class TextShoutMessage : ShoutMessage
{
    public const string TypeName = "textShout";
    public override string Type => TypeName;

    /// <summary>消息 ID，用于对应 <see cref="AckMessage"/>。</summary>
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>TTS 语速，取值 -10 ~ 10（0 为系统默认）。</summary>
    public int Rate { get; set; }

    /// <summary>TTS 音量，取值 0 ~ 100。</summary>
    public int Volume { get; set; } = 100;

    /// <summary>指定系统语音名称；为空则用教室端自身配置。</summary>
    public string? VoiceName { get; set; }

    /// <summary>是否打断当前正在播放/朗读的内容。</summary>
    public bool Interrupt { get; set; } = true;
}

/// <summary>语音喊话开始。之后跟着若干 <see cref="FrameKind.Audio"/> 帧。</summary>
public sealed class AudioStartMessage : ShoutMessage
{
    public const string TypeName = "audioStart";
    public override string Type => TypeName;

    public string Id { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;

    /// <summary>预计时长（毫秒）；未知填 0。</summary>
    public int DurationMs { get; set; }
}

/// <summary>语音喊话结束。</summary>
public sealed class AudioEndMessage : ShoutMessage
{
    public const string TypeName = "audioEnd";
    public override string Type => TypeName;

    public string Id { get; set; } = string.Empty;
}

/// <summary>要求教室端立即停止当前播放或朗读。</summary>
public sealed class StopMessage : ShoutMessage
{
    public const string TypeName = "stop";
    public override string Type => TypeName;

    public string Reason { get; set; } = "教师端中止";
}

/// <summary>教室端回执。</summary>
public sealed class AckMessage : ShoutMessage
{
    public const string TypeName = "ack";
    public override string Type => TypeName;

    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; } = true;
    public string? Detail { get; set; }
}

/// <summary>教室端状态上报，用于教师端显示“正在朗读 / 队列长度”等。</summary>
public sealed class StatusMessage : ShoutMessage
{
    public const string TypeName = "status";
    public override string Type => TypeName;

    /// <summary>idle / speaking / playing / muted。</summary>
    public string State { get; set; } = "idle";

    /// <summary>教室端名称，握手后回给教师端用于确认连对了教室。</summary>
    public string? ClassroomName { get; set; }

    public bool Muted { get; set; }
    public int Volume { get; set; } = 100;
}

/// <summary>教师端主动断开。</summary>
public sealed class ByeMessage : ShoutMessage
{
    public const string TypeName = "bye";
    public override string Type => TypeName;

    public string Reason { get; set; } = "教师端已离开";
}

/// <summary>错误通知（双向）。</summary>
public sealed class ErrorMessage : ShoutMessage
{
    public const string TypeName = "error";
    public override string Type => TypeName;

    public string Code { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
}
