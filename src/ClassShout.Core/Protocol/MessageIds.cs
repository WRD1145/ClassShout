using System.Globalization;

namespace ClassShout.Core.Protocol;

/// <summary>
/// 消息 ID —— 就是这条消息的**发送时间戳**。
///
/// 为什么 ID 不另生成一个随机串，而是直接拿发送时刻当 ID：
///
///   · 回复要能对上原消息。教室端手里只有"刚收到的这条喊话"，而它天然带着
///     "什么时候发的"这一信息 —— 用它当 ID，两边不必再多交换一次"我收到了哪条"；
///   · 排查现场问题时，日志里一串 <c>20261020233225123</c> 直接就读出发送时刻，
///     而 <c>a1b2c3d4</c> 还得回去翻日志找它是哪一条；
///   · 同一条消息在两条路上（局域网、中继）身份一致：教师端生成一次，
///     服务器与教室端都只是原样带着它。
///
/// 格式是 <c>yyyyMMddHHmmssfff</c>：定长、按字典序排就是按时间排，
/// 也不带任何分隔符（它会出现在 URL、日志与 JSON 里，越朴素越好）。
///
/// 唯一要处理的是"同一毫秒内发两条"：同秒同毫秒在人工操作下几乎不可能，
/// 但排队发送、重试可能挤在一起，所以生成后由调用方自行去重（见 <see cref="New"/>）。
/// </summary>
public static class MessageIds
{
    /// <summary>ID 的长度。定长的好处是日志里对得齐，也便于"这条 ID 是不是截断了"一眼看出。</summary>
    public const int Length = 17;

    private static readonly Lock Gate = new();

    private static string _last = string.Empty;

    /// <summary>按给定时刻（默认现在）生成一个 ID。</summary>
    /// <remarks>
    /// 单调性：真的撞上同一毫秒时，把毫秒数往上加一毫秒，而不是重复。
    /// 重复的 ID 会让"回复对应到哪条消息"变成一次多解，而那种错误
    /// 只有在同一毫秒连发两条时才会出现 —— 最难复现的一类。
    /// </remarks>
    public static string New(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;

        lock (Gate)
        {
            var candidate = Format(now);

            if (string.CompareOrdinal(candidate, _last) <= 0)
            {
                // 时间没走或走回去了（改系统时间、同一毫秒连发）：在上一条的基础上 +1 毫秒
                if (DateTime.TryParseExact(
                        _last,
                        "yyyyMMddHHmmssfff",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var previous))
                {
                    candidate = Format(new DateTimeOffset(
                        previous.AddMilliseconds(1),
                        now.Offset));
                }
            }

            _last = candidate;
            return candidate;
        }
    }

    /// <summary>把一个 ID 还原成时刻（时间戳本身就是可逆的）。认不出来时返回 null。</summary>
    public static DateTimeOffset? Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != Length)
        {
            return null;
        }

        return DateTime.TryParseExact(
            id,
            "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? new DateTimeOffset(value, DateTimeOffset.Now.Offset)
            : null;
    }

    /// <summary>ID 长得像不像一个发送时间戳（用于把"老版本发的 GUID"与新的区分开）。</summary>
    public static bool LooksLikeTimestamp(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length == Length
           && id.All(char.IsAsciiDigit);

    /// <summary>给学生与老师看的时间说法：<c>23:32:25</c>。</summary>
    public static string TimeText(string? id)
        => Parse(id) is { } at ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    private static string Format(DateTimeOffset at)
        => at.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
}
