namespace ClassShout.Core.Remote;

/// <summary>
/// 文字页上那一排「常用语」。
///
/// 出厂给的是课堂上真会说的几句话，但每位老师的口头习惯差得远：班主任要「安静坐好」，
/// 英语老师要「跟着我读」，把他用不上的八条钉死在界面上，等于每次都得先挑一条最像的
/// 再改两个字 —— 那还不如不点。所以这份列表由老师自己维护，存在他自己的设备上。
///
/// 和教室端默认值的区别同 <see cref="TeacherDisplaySettings"/>：这是**这位老师的**常用语，
/// 换一位老师登录不该看见上一位的话。
/// </summary>
public sealed class TeacherPhraseSettings
{
    /// <summary>
    /// 最多留几条。
    ///
    /// 有上限是因为它挤在输入框下面：二十来条还能一眼扫完，再多就把真正要写字的
    /// 输入框推出屏幕了 —— 常用语是给"懒得打字"用的，它不该比打字更费事。
    /// </summary>
    public const int MaxCount = 24;

    /// <summary>单条最长多少个字。常用语是"一句话"，不是一段话。</summary>
    public const int MaxLength = 40;

    /// <summary>常用语内容，按界面上的先后顺序。</summary>
    public List<string> Phrases { get; set; } = [];

    /// <summary>
    /// 出厂自带的那几条 —— 「恢复默认」用它，第一次运行也用它。
    ///
    /// 挑的是各科通用的课堂用语，不假设具体学科：一份自带列表要是偏向某一科，
    /// 别的老师第一件事就是把它全删掉。
    /// </summary>
    public static List<string> Defaults() =>
    [
        "同学们请安静",
        "请注意看黑板",
        "这道题我再讲一遍",
        "请翻到课本第 __ 页",
        "课代表把作业收上来",
        "下课后请到办公室找我",
        "距离下课还有十分钟",
        "请把手机收起来",
    ];

    /// <summary>默认设置（首次运行、文件丢失、文件读坏时用）。</summary>
    public static TeacherPhraseSettings WithDefaults() => new() { Phrases = Defaults() };

    /// <summary>
    /// 收敛手改坏的值：去首尾空白、丢掉空条目、按内容去重、截到上限。
    ///
    /// 这里**不会**"空了就补回默认"：空列表是老师明确删光后的结果，
    /// 下次启动又冒出来，像是在跟人较劲。要恢复默认请按「恢复默认」。
    /// 返回值是新对象，不改调用方手上那份 —— 与 <see cref="TeacherDisplaySettings.Normalized"/> 一致。
    /// </summary>
    public TeacherPhraseSettings Normalized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();

        foreach (var raw in Phrases ?? [])
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // 超长的截断而不是丢弃：手改过的长句子丢掉等于把人写的东西删了，
            // 截断至少留下开头，界面上也看得见发生了什么。
            if (text.Length > MaxLength)
            {
                text = text[..MaxLength];
            }

            if (!seen.Add(text))
            {
                continue;
            }

            cleaned.Add(text);
            if (cleaned.Count >= MaxCount)
            {
                break;
            }
        }

        return new TeacherPhraseSettings { Phrases = cleaned };
    }
}
