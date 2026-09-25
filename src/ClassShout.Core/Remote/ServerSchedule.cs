namespace ClassShout.Core.Remote;

/// <summary>
/// 一条存在**服务器**上的定时喊话。
///
/// 为什么要有它：本机定时有一条绕不过去的边界 —— 老师把手机上的应用划掉之后，
/// 到点没人替他发。要做"关掉手机也照样响"，这条任务就得先交给一个
/// 一直开着的进程，而服务器正好就是那个进程。
///
/// 内容与目标都在创建时固定：老师上午排的任务，下午早就把班级改过几轮了，
/// 而他要的是"我排的时候那条就是这个样子"。
/// </summary>
public sealed class ServerScheduledShout
{
    /// <summary>单条语音的长度上限（秒）。再长就不该用"定时喊一句"来做。</summary>
    public const int MaxVoiceSeconds = 60;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>创建它的账号。只有本人（或管理员）能看与取消。</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>创建时的老师姓名，用于界面显示 —— 账号改名后历史记录仍说得清是谁排的。</summary>
    public string OwnerDisplayName { get; set; } = string.Empty;

    /// <summary>什么时候发（UTC）。</summary>
    public DateTimeOffset SendAt { get; set; }

    /// <summary>text / voice，取值见 <see cref="ScheduledShoutKinds"/>。</summary>
    public string Kind { get; set; } = ScheduledShoutKinds.Text;

    public string? Text { get; set; }

    /// <summary>语音的 PCM 文件名（服务器上 relay-schedule-audio 目录里）。</summary>
    public string? AudioFile { get; set; }

    public int AudioSampleRate { get; set; }

    public int AudioChannels { get; set; }

    public int AudioBitsPerSample { get; set; }

    public double AudioSeconds { get; set; }

    /// <summary>展示参数，含义与即时喊话完全一致。</summary>
    public string? Display { get; set; }

    public string? FontSize { get; set; }

    public int HoldMs { get; set; } = Protocol.ShoutHoldDurations.Unspecified;

    public bool Speak { get; set; } = true;

    /// <summary>目标教室 UUID。</summary>
    public List<string> TargetUuids { get; set; } = [];

    // —— 处理结果 ——

    /// <summary>pending / sent / missed / cancelled。</summary>
    public string Status { get; set; } = ServerScheduleStatus.Pending;

    public DateTimeOffset? HandledAt { get; set; }

    /// <summary>逐间的结果一句话，例如"三年二班：已发出"。贴回给老师核对用。</summary>
    public List<string> Results { get; set; } = [];

    /// <summary>没发出去时的原因。</summary>
    public string? Error { get; set; }

    /// <summary>已取消。</summary>
    public bool IsCancelled => string.Equals(Status, ServerScheduleStatus.Cancelled, StringComparison.Ordinal);

    /// <summary>还没处理。</summary>
    public bool IsPending => string.Equals(Status, ServerScheduleStatus.Pending, StringComparison.Ordinal);
}

/// <summary>服务器上定时任务的几种状态。</summary>
public static class ServerScheduleStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";

    /// <summary>错过（到点时应用没开着不是理由 —— 这里是服务器；教室不在线才是）。</summary>
    public const string Missed = "missed";

    public const string Cancelled = "cancelled";
}

/// <summary>创建一条服务器定时（文字）。语音走 multipart，字段同名。</summary>
/// <param name="Text">要朗读/显示的文字（语音定时时可为空）。</param>
/// <param name="SendAt">什么时候发（UTC）。</param>
/// <param name="TargetUuids">发给哪几间教室。</param>
/// <param name="Display">展示方式。</param>
/// <param name="FontSize">字号档位。</param>
/// <param name="HoldMs">停留时长。</param>
/// <param name="Speak">是否朗读。</param>
public sealed record ScheduleShoutRequest(
    string? Text,
    DateTimeOffset SendAt,
    IReadOnlyList<string> TargetUuids,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = Protocol.ShoutHoldDurations.Unspecified,
    bool Speak = true);

/// <summary>服务器上一条定时任务的样子（列表与创建都用它）。</summary>
public sealed record ScheduledShoutDto(
    string Id,
    string Kind,
    string? Text,
    double AudioSeconds,
    DateTimeOffset SendAt,
    string Status,
    DateTimeOffset? HandledAt,
    string? Error,
    IReadOnlyList<string> TargetUuids,
    IReadOnlyList<string> TargetNames,
    IReadOnlyList<string> Results,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = Protocol.ShoutHoldDurations.Unspecified,
    bool Speak = true);

/// <summary>创建的结果。</summary>
public sealed record ScheduleShoutResponse(
    bool Ok,
    ScheduledShoutDto? Item = null,
    string? Error = null);
