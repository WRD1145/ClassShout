using ClassShout.Core.Protocol;

namespace ClassShout.Core.Remote;

/// <summary>一条定时喊话的内容形态。</summary>
public static class ScheduledShoutKinds
{
    public const string Text = "text";

    /// <summary>录好的一段语音。到点当作语音喊话放出去。</summary>
    public const string Voice = "voice";

    public static bool IsVoice(string? kind) => string.Equals(kind, Voice, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一条定时喊话。
///
/// 它是"老师设好时间、到点自动发出去"的一整条喊话：内容、展示参数、发给哪几个班，
/// 全部在**创建时**固定下来。为什么不在发送时才读界面上的当前值 ——
/// 老师上午设的任务，下午上课时早就把字号和目标班级改过好几轮了，
/// 而他要的是"我设的时候那条就是这个样子"。
/// </summary>
public sealed class ScheduledShout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>什么时候发（本地时间）。</summary>
    public DateTimeOffset SendAt { get; set; }

    /// <summary>文字内容。语音定时时为空。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>文字还是语音，取值见 <see cref="ScheduledShoutKinds"/>。</summary>
    public string Kind { get; set; } = ScheduledShoutKinds.Text;

    /// <summary>
    /// 语音片段的文件名（相对数据目录的 schedule-audio 子目录）。
    ///
    /// 只在本机定时时用得上：交给服务器发的那几条，音频在服务器上。
    /// </summary>
    public string? AudioFile { get; set; }

    /// <summary>语音时长（秒），列表里显示用。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>展示参数，含义与即时喊话完全一致。</summary>
    public string? Display { get; set; }

    public string? FontSize { get; set; }

    public int HoldMs { get; set; } = ShoutHoldDurations.Unspecified;

    public bool Speak { get; set; } = true;

    /// <summary>
    /// 发给哪几间教室（UUID）。为空表示"用创建时正在绑定的那间"。
    /// </summary>
    public List<string> TargetUuids { get; set; } = [];

    /// <summary>实际发出的时间；为空表示还没发。</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>发失败时的原因，供界面显示。</summary>
    public string? LastError { get; set; }

    /// <summary>是否已被用户取消。</summary>
    public bool Cancelled { get; set; }

    /// <summary>这条到点了没有。</summary>
    public bool IsDue(DateTimeOffset now) => !Cancelled && SentAt is null && SendAt <= now;

    /// <summary>是不是一条"错过太久"的任务。</summary>
    /// <param name="now">当前时间。</param>
    /// <param name="grace">允许迟发多久。超过这个窗口就只提示、不补发。</param>
    /// <remarks>
    /// "下课前五分钟提醒交作业"这条提醒在过了十分钟之后已经没有意义了 ——
    /// 自动补发反而会在下一节课上突然喊一句不着边际的话。
    /// 所以错过太久的一律只标出来、不自动发。
    /// </remarks>
    public bool IsMissedBeyond(DateTimeOffset now, TimeSpan grace)
        => !Cancelled && SentAt is null && SendAt + grace < now;

    /// <summary>显示用的时间。</summary>
    public string TimeText => SendAt.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>内容摘要（列表里显示）。</summary>
    public string SummaryText
    {
        get
        {
            if (ScheduledShoutKinds.IsVoice(Kind))
            {
                return $"语音 {AudioSeconds:0.#} 秒";
            }

            var text = string.IsNullOrWhiteSpace(Text) ? "（空）" : Text.Trim().ReplaceLineEndings(" ");
            return text.Length <= 24 ? text : text[..24] + "…";
        }
    }
}

/// <summary>教师端的定时喊话设置。</summary>
public sealed class TeacherScheduleSettings
{
    /// <summary>
    /// 最多留几条。
    ///
    /// 这个列表是给"今天剩下来的几件事"用的，不是日历。
    /// 留太多会让"哪条还没发"变得需要逐行找。
    /// </summary>
    public const int MaxItems = 20;

    /// <summary>还没发出去的定时喊话。</summary>
    public List<ScheduledShout> Items { get; set; } = [];

    /// <summary>已经处理完（发过或取消）的记录，只留最近几条供查看。</summary>
    public List<ScheduledShout> History { get; set; } = [];

    /// <summary>把一条任务收进来，并按上限裁剪最旧的未发任务。</summary>
    public void Add(ScheduledShout item)
    {
        Items.RemoveAll(existing => existing.Id == item.Id);
        Items.Add(item);
        Items.Sort((a, b) => a.SendAt.CompareTo(b.SendAt));

        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    /// <summary>把一条任务移进历史（发过或取消）。</summary>
    public void MoveToHistory(ScheduledShout item)
    {
        Items.RemoveAll(existing => existing.Id == item.Id);
        History.Insert(0, item);

        while (History.Count > MaxItems)
        {
            History.RemoveAt(History.Count - 1);
        }
    }
}
