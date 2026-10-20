using System.Collections.ObjectModel;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>
/// 教师端的「教室回复」这一块。
///
/// 教室端回了一句之后，老师这边要做三件事：
///   1. 记下来（按时间倒序，能翻到刚才那几条）；
///   2. 弹一次系统通知（老师多半正在做别的事，不会一直盯着这一页）；
///   3. 把"未读"这件事说清（导航上的角标 + 进来就知道哪几条是新的）。
///
/// 单独一个 partial 文件，理由与教室端的回复那一块一样：主文件已经三千多行，
/// 塞进去之后"回复"就会被埋掉。
/// </summary>
public partial class TeacherShellViewModel
{
    /// <summary>最多留多少条回复：再多也不会有人翻，而它每次启动都要读一份配置。</summary>
    private const int MaxReplies = 50;

    /// <summary>已经处理过的回复 ID（同一条回复可能从局域网与中继各来一次）。</summary>
    private readonly HashSet<string> _seenReplyIds = new(StringComparer.Ordinal);

    /// <summary>教室回复，最新的在最前。</summary>
    public ObservableCollection<ClassroomReplyItem> Replies { get; } = [];

    public bool HasReplies => Replies.Count > 0;

    private int _unreadReplies;

    /// <summary>还没看过的回复条数（导航角标用）。</summary>
    public int UnreadReplies
    {
        get => _unreadReplies;
        private set
        {
            if (SetProperty(ref _unreadReplies, value))
            {
                OnPropertyChanged(nameof(HasUnreadReplies));
                OnPropertyChanged(nameof(ReplySummaryText));
            }
        }
    }

    public bool HasUnreadReplies => _unreadReplies > 0;

    /// <summary>「教室回复」那张卡的标题行：有几条新的 / 一共几条。</summary>
    public string ReplySummaryText => Replies.Count == 0
        ? "还没有回复"
        : UnreadReplies > 0
            ? $"{Replies.Count} 条，其中 {UnreadReplies} 条没看"
            : $"{Replies.Count} 条";

    /// <summary>
    /// 收下一条回复。
    ///
    /// 两条链路都可能送来同一条（局域网直连 + 服务器转发同时挂着是常态），
    /// 所以按回复自己的 ID 去重 —— 重复弹两次通知会让老师以为教室里回了两次。
    ///
    /// 公开出来是因为它就是"收到一条教室回复"这件事的唯一入口：
    /// 局域网与中继两条路都落到这里，自检也直接喂它 —— 只有这样，
    /// 断言测的才是真实那条路，而不是另写一份。
    /// </summary>
    public void AcceptReply(ReplyMessage reply)
    {
        var id = string.IsNullOrWhiteSpace(reply.Id) ? MessageIds.New() : reply.Id!;

        if (!_seenReplyIds.Add(id))
        {
            return;
        }

        // 去重集合不能无限长：留最近 200 条足够覆盖"同一条从两条路各来一次"的窗口
        if (_seenReplyIds.Count > 200)
        {
            _seenReplyIds.Clear();
            _seenReplyIds.Add(id);
        }

        var item = new ClassroomReplyItem(
            id,
            reply.ReplyToId,
            reply.Kind,
            string.IsNullOrWhiteSpace(reply.From) ? ClassroomName : reply.From!,
            reply.Text,
            reply.AudioWav,
            reply.AudioDurationMs,
            DateTimeOffset.Now);

        Replies.Insert(0, item);

        while (Replies.Count > MaxReplies)
        {
            Replies.RemoveAt(Replies.Count - 1);
        }

        UnreadReplies++;

        OnPropertyChanged(nameof(HasReplies));
        OnPropertyChanged(nameof(ReplySummaryText));

        AddLog($"「{item.From}」{ReplyKinds.Label(item.Kind)}：{item.DisplayText}");

        // 系统通知：老师这时多半在做别的事
        NotifyReply(item);
    }

    /// <summary>弹一条系统通知。没有通知能力时只留应用内那条日志与列表。</summary>
    private void NotifyReply(ClassroomReplyItem item)
    {
        var title = $"「{item.From}」{ReplyKinds.Label(item.Kind)}";

        var body = item.Kind == ReplyKinds.Voice
            ? $"语音回复（{item.AudioSeconds} 秒）"
            : item.DisplayText;

        TeacherPlatform.Notify(title, body);
    }

    /// <summary>把这些回复标成已看（进「教室回复」那张卡时调用）。</summary>
    [RelayCommand]
    public void MarkRepliesRead()
    {
        if (UnreadReplies == 0)
        {
            return;
        }

        UnreadReplies = 0;
    }

    /// <summary>清空回复列表。</summary>
    [RelayCommand]
    private void ClearReplies()
    {
        Replies.Clear();
        _seenReplyIds.Clear();
        UnreadReplies = 0;

        OnPropertyChanged(nameof(HasReplies));
        OnPropertyChanged(nameof(ReplySummaryText));
    }

    /// <summary>播放一条语音回复。没有播放能力时返回一句说明。</summary>
    [RelayCommand]
    private void PlayReply(ClassroomReplyItem? item)
    {
        if (item?.AudioWav is not { Length: > 0 } base64)
        {
            return;
        }

        try
        {
            var wav = Convert.FromBase64String(base64);
            TeacherPlatform.PlayWav(wav);
            AddLog($"正在播放「{item.From}」的语音回复。");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
        {
            ErrorMessage = $"这段语音回复播不出来：{ex.Message}";
        }
    }
}

/// <summary>「教室回复」列表里的一条。</summary>
public sealed class ClassroomReplyItem
{
    public ClassroomReplyItem(
        string id,
        string? replyToId,
        string kind,
        string from,
        string text,
        string? audioWav,
        int audioDurationMs,
        DateTimeOffset at)
    {
        Id = id;
        ReplyToId = replyToId ?? string.Empty;
        Kind = kind;
        From = from;
        Text = text;
        AudioWav = audioWav;
        AudioDurationMs = audioDurationMs;
        At = at;
    }

    /// <summary>这条回复自己的 ID（发送时刻）。</summary>
    public string Id { get; }

    /// <summary>回的是哪一条消息的 ID。</summary>
    public string ReplyToId { get; }

    /// <summary>取值见 <see cref="ReplyKinds"/>。</summary>
    public string Kind { get; }

    public string From { get; }

    public string Text { get; }

    public string? AudioWav { get; }

    public int AudioDurationMs { get; }

    public DateTimeOffset At { get; }

    /// <summary>回复方式的文字（"知道了" / "快捷回复" / "文字回复" / "语音回复"）。</summary>
    public string KindLabel => ReplyKinds.Label(Kind);

    /// <summary>语音回复没有正文，只有时长。</summary>
    public string DisplayText => Kind == ReplyKinds.Voice && Text.Length == 0
        ? $"（语音 {AudioSeconds} 秒）"
        : Text;

    public int AudioSeconds => Math.Max(1, (int)Math.Round(AudioDurationMs / 1000.0));

    public bool HasAudio => AudioWav is { Length: > 0 };

    public bool IsVoice => Kind == ReplyKinds.Voice;

    /// <summary>发出这条回复的时刻（与消息 ID 是同一个时间戳，但以收到的本地时刻为准显示）。</summary>
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>「回的是哪一条」的一句话。</summary>
    public string ReplyToText => ReplyToId.Length == 0
        ? "不针对某一条"
        : $"回的是 {MessageIds.TimeText(ReplyToId)} 那条";
}
