using System.Text.Json.Serialization;

namespace ClassShout.Core.Remote;

/// <summary>一条喊话记录。</summary>
/// <param name="Text">文字喊话的内容；语音喊话存一句说明。</param>
/// <param name="SentAt">发出时间。</param>
/// <param name="IsVoice">是不是语音喊话。</param>
public sealed record ShoutRecord(string Text, DateTimeOffset SentAt, bool IsVoice);

/// <summary>
/// 最近喊话记录（本机保存）。
///
/// 为什么留着它：课堂上喊错一句、或者想重复上一句口令都是常有的事，
/// 而喊话是"说完就没了"的东西 —— 界面一换就再也找不回来。
/// 最近二十条足够覆盖一节课的上下文，又不至于把磁盘当日志写。
///
/// 刻意只留"说了什么、什么时候说的"：教室口令、登录令牌这类凭据一概不进这个文件。
/// </summary>
public sealed class ShoutHistory
{
    /// <summary>最多保留多少条，超出从最旧一端丢弃。</summary>
    public const int MaxCount = 20;

    /// <summary>最近的喊话，最新的在前。</summary>
    public List<ShoutRecord> Recent { get; set; } = [];

    /// <summary>
    /// 在已有列表前面插入一条并裁到上限，返回新列表。
    ///
    /// 返回新列表而不是就地修改：调用方手里的通常是一份刚从磁盘读出来的副本，
    /// 就地改会让"谁负责保存"变得含糊，也容易在别处被误当成共享状态。
    /// </summary>
    public static List<ShoutRecord> Append(IReadOnlyList<ShoutRecord> existing, ShoutRecord record)
    {
        var list = new List<ShoutRecord>(MaxCount + 1) { record };

        foreach (var item in existing)
        {
            if (list.Count >= MaxCount)
            {
                break;
            }

            list.Add(item);
        }

        return list;
    }
}

/// <summary>
/// 喊话记录的读写。
///
/// 与其它本地设置共用同一个目录，但单独一个文件：
/// teacher.json 里存的是登录状态那类"配置"，而这里是可以随时丢掉的历史。
/// 混在一起会让"退出登录该清哪些东西"难以回答 ——
/// 记录不该因为退出登录而消失。
/// </summary>
public static class ShoutHistoryStore
{
    /// <summary>最多保留的条数，供界面提示用。</summary>
    public const int MaxCount = ShoutHistory.MaxCount;

    private const string FileName = "shout-history.json";

    public static ShoutHistory Load() => LocalSettings.Load(FileName, static () => new ShoutHistory());

    public static bool Save(ShoutHistory history) => LocalSettings.Save(FileName, history);

    /// <summary>
    /// 记一条并落盘，返回更新后的列表。
    ///
    /// 写盘失败不抛也不上报：喊话已经发出去了，
    /// 因为记不下历史而弹一个错误反而更打扰正在上课的人。
    /// 返回值让调用方能顺手刷新界面。
    /// </summary>
    public static IReadOnlyList<ShoutRecord> Record(string text, bool isVoice)
    {
        var history = Load();

        if (string.IsNullOrWhiteSpace(text))
        {
            return history.Recent;
        }

        var updated = ShoutHistory.Append(
            history.Recent,
            new ShoutRecord(text.Trim(), DateTimeOffset.Now, isVoice));

        history.Recent = updated;
        Save(history);

        return updated;
    }

    /// <summary>清空记录。</summary>
    public static void Clear() => Save(new ShoutHistory());
}

/// <summary>
/// 界面上的一条"最近喊话"。
///
/// 单独一个类而不是直接绑 ShoutRecord：界面要显示"刚刚""3 分钟前"这种相对时间，
/// 而相对时间必须在**渲染时**算，不能在记录时算好存起来 ——
/// 存起来的话，昨天看到的"刚刚"过一天还是"刚刚"。
/// </summary>
public sealed class ShoutRecordItem
{
    public ShoutRecordItem(ShoutRecord record) => Record = record;

    public ShoutRecord Record { get; }

    public string Text => Record.Text;

    public bool IsVoice => Record.IsVoice;

    /// <summary>「语音」或「文字」，用于列表上的小标记。</summary>
    public string KindText => Record.IsVoice ? "语音" : "文字";

    /// <summary>相对时间，渲染时计算。</summary>
    public string TimeText
    {
        get
        {
            var elapsed = DateTimeOffset.Now - Record.SentAt;

            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return "刚刚";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return $"{(int)elapsed.TotalMinutes} 分钟前";
            }

            if (Record.SentAt.Date == DateTime.Today)
            {
                return Record.SentAt.ToString("HH:mm");
            }

            return Record.SentAt.ToString("MM-dd HH:mm");
        }
    }
}