using ClassShout.Core.Protocol;

namespace ClassShout.Core.Remote;

/// <summary>回复内容的组件种类。
///
/// 与「呼叫」的组件（<see cref="MessageComponentKinds"/>）刻意分成两套：
/// 呼叫是**老师**拼给教室的话（要学生姓名、学号、小组），
/// 而回复是**教室**拼给老师的话 —— 那边的学生名单教室里根本没有。
/// 两套混在一起，组件面板里就会出现"教室里选不了的学生"这种点不动的按钮。
///
/// 能用的都是教室这边确实知道的东西：自己叫什么、刚才谁在说、那句话是什么、
/// 现在几点。
/// </summary>
public static class ReplyComponentKinds
{
    /// <summary>固定文字，例如「收到」「好的，我们马上」。</summary>
    public const string Text = "text";

    /// <summary>教室名，例如「三年二班」。</summary>
    public const string Classroom = "classroom";

    /// <summary>老师在这间教室里的称呼，例如「数学张老师」。</summary>
    public const string Teacher = "teacher";

    /// <summary>回复时间（<c>14:05</c>）。</summary>
    public const string Time = "time";

    /// <summary>原喊话内容。</summary>
    public const string Original = "original";

    public static IReadOnlyList<string> All { get; } = [Text, Classroom, Teacher, Time, Original];

    public static string Label(string kind) => kind switch
    {
        Text => "文字",
        Classroom => "教室名",
        Teacher => "教师名字",
        Time => "回复时间",
        Original => "原喊话内容",
        _ => kind,
    };
}

/// <summary>
/// 教室端拼一条回复时拿得到的上下文。
///
/// 刻意做成一个小对象而不是把一堆参数摊在每个方法上：拼装要被自检、服务器
/// 与两个平台反复调用，参数一多，"少传一个"就变成某个组件输出空字符串 ——
/// 而界面上那看起来只是"这条回复有点怪"。
/// </summary>
/// <param name="Classroom">教室名。</param>
/// <param name="Teacher">老师在这间教室里的称呼（含科目）。</param>
/// <param name="OriginalText">被回复的那条喊话的正文；语音喊话时可以是"（语音）"。</param>
/// <param name="At">回复时刻。</param>
public sealed record ReplyContext(
    string Classroom,
    string Teacher,
    string OriginalText,
    DateTimeOffset At);

/// <summary>一条快捷回复：按钮上显示什么 + 回复内容由哪些组件拼成。</summary>
public sealed class QuickReply
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>按钮上的文字。为空时退回拼出来的内容（至少按钮上得有东西）。</summary>
    public string Label { get; set; } = string.Empty;

    public List<MessageComponent> Components { get; set; } = [];

    /// <summary>给设置界面看的摘要。</summary>
    public string Summary => Components.Count == 0
        ? "（空）"
        : string.Join(" · ", Components.Select(component =>
            component.Kind == ReplyComponentKinds.Text
                ? (string.IsNullOrEmpty(component.Text) ? "文字" : component.Text)
                : ReplyComponentKinds.Label(component.Kind)));

    public static QuickReply Of(string label, params string[] texts)
    {
        var reply = new QuickReply { Label = label };

        foreach (var text in texts)
        {
            reply.Components.Add(MessageComponent.Of(ReplyComponentKinds.Text, text));
        }

        return reply;
    }
}

/// <summary>
/// 教室端的回复设置（`classroom-reply.json`）。
///
/// 为什么这些选项在**教室端**的设置里：回复是教室里那个人（学生、课代表、
/// 下节课的老师）在做，而"能怎么回"取决于这间教室的实际情况 ——
/// 有的教室没有麦克风（语音回复就该关掉），有的班级只需要一个「知道了」。
/// 放在教师端的话，同一套按钮会出现在所有教室里。
/// </summary>
public sealed class ClassroomReplySettings
{
    /// <summary>总开关。关掉之后教室那块屏上不出现任何回复入口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否显示「知道了」按钮。</summary>
    public bool ShowAcknowledge { get; set; } = true;

    /// <summary>
    /// 「知道了」这条回复的内容（同样是组件拼的）。
    ///
    /// 默认只有一句「知道了」：这个按钮的用处是"一秒点完、老师知道教室里看到了"，
    /// 让它可拼装是因为有的班想带上教室名（"三年二班收到"）。
    /// </summary>
    public List<MessageComponent> AcknowledgeComponents { get; set; } =
    [
        MessageComponent.Of(ReplyComponentKinds.Classroom),
        MessageComponent.Of(ReplyComponentKinds.Text, "知道了"),
    ];

    /// <summary>是否允许文字回复（教室里现打一句话）。</summary>
    public bool AllowText { get; set; } = true;

    /// <summary>是否允许语音回复。没有麦克风的教室应当关掉它。</summary>
    public bool AllowVoice { get; set; } = true;

    /// <summary>语音回复的时长上限（秒）。</summary>
    public int VoiceMaxSeconds { get; set; } = 15;

    /// <summary>快捷回复按钮（可以有好几个）。</summary>
    public List<QuickReply> QuickReplies { get; set; } = [];

    /// <summary>按钮最多几个：再多教室里那块屏就摆不下，也点不准。</summary>
    public const int MaxQuickReplies = 6;

    /// <summary>默认的几条：多数班级用得上的说法，空面板对第一次用的人毫无提示作用。</summary>
    public static ClassroomReplySettings WithDefaults() => new()
    {
        QuickReplies =
        [
            QuickReply.Of("收到", "收到"),
            QuickReply.Of("马上", "马上"),
            QuickReply.Of("没问题", "没问题"),
        ],
    };

    /// <summary>把离谱的取值收回来（这个文件可能被手改过，也可能来自旧版本）。</summary>
    public ClassroomReplySettings Normalized()
    {
        VoiceMaxSeconds = Math.Clamp(VoiceMaxSeconds <= 0 ? 15 : VoiceMaxSeconds, 3, 60);

        // 语音与文字都不能用时，至少保住「知道了」—— 否则老师喊完话，
        // 教室里一个能点的东西都没有，而界面上看起来"回复功能开着"。
        if (!AllowText && !AllowVoice && !ShowAcknowledge && QuickReplies.Count == 0)
        {
            ShowAcknowledge = true;
            AcknowledgeComponents = ClassroomReplySettings.WithDefaults().AcknowledgeComponents;
        }

        if (AcknowledgeComponents.Count == 0)
        {
            AcknowledgeComponents =
            [
                MessageComponent.Of(ReplyComponentKinds.Classroom),
                MessageComponent.Of(ReplyComponentKinds.Text, "知道了"),
            ];
        }

        QuickReplies = QuickReplies
            .Where(reply => !string.IsNullOrWhiteSpace(reply.Label) || reply.Components.Count > 0)
            .Take(MaxQuickReplies)
            .ToList();

        foreach (var reply in QuickReplies)
        {
            if (string.IsNullOrWhiteSpace(reply.Label))
            {
                reply.Label = ReplyComposer.Compose(reply, new ReplyContext("教室", "老师", string.Empty, DateTimeOffset.Now));
            }
        }

        return this;
    }
}

/// <summary>
/// 把一条回复模板拼成要发出去的那句话。
///
/// 与 <see cref="CallComposer"/> 同一个规矩：**组件顺序拼接、之间不加空格**，
/// 空格由「文字」组件自己带。两边不一致的话，同一个人在教室与手机上
/// 会看到两种断句。
/// </summary>
public static class ReplyComposer
{
    /// <summary>拼一条快捷回复 / 「知道了」。</summary>
    public static string Compose(QuickReply reply, ReplyContext context)
        => Compose(reply.Components, context);

    /// <summary>拼一组组件。</summary>
    public static string Compose(IReadOnlyList<MessageComponent> components, ReplyContext context)
    {
        if (components.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var component in components)
        {
            builder.Append(component.Kind switch
            {
                ReplyComponentKinds.Classroom => context.Classroom,
                ReplyComponentKinds.Teacher => context.Teacher,
                ReplyComponentKinds.Time => context.At.ToString("HH:mm"),
                ReplyComponentKinds.Original => context.OriginalText,
                _ => component.Text ?? string.Empty,
            });
        }

        return builder.ToString().Trim();
    }

    /// <summary>「知道了」那条回复。</summary>
    public static string ComposeAcknowledge(ClassroomReplySettings settings, ReplyContext context)
    {
        var text = Compose(settings.AcknowledgeComponents, context);

        // 拼出来是空的时候兜一句：教室里点了按钮，老师那边不该收到一条空白回复
        return text.Length == 0 ? "知道了" : text;
    }

    /// <summary>语音回复在列表里显示的那句话（语音本身没法预览）。</summary>
    public static string VoiceSummary(int durationMs, ReplyContext context)
        => $"（语音 {Math.Max(1, (int)Math.Round(durationMs / 1000.0))} 秒）{context.Classroom}";
}
