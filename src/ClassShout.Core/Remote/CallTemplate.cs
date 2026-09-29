namespace ClassShout.Core.Remote;

/// <summary>组件种类。取值存字符串，旧文件加新组件时不会让老数据失去含义。</summary>
public static class MessageComponentKinds
{
    /// <summary>固定文字，例如"来办公室"、"请"、"："。</summary>
    public const string Text = "text";

    /// <summary>学生姓名。</summary>
    public const string StudentName = "studentName";

    /// <summary>学生简写。</summary>
    public const string StudentShort = "studentShort";

    /// <summary>学生学号。</summary>
    public const string StudentNo = "studentNo";

    /// <summary>这名学生所在小组的成员名单（"张三、李四"）。</summary>
    public const string Group = "group";

    /// <summary>老师自己的名字（含任教科目，例如"数学张老师"）。</summary>
    public const string Teacher = "teacher";

    /// <summary>
    /// 随机叫人：抽中的那几位学生的姓名（顿号分隔）。
    ///
    /// 与其他组件不同，它不取"当前这位学生"的字段 —— 由谁出现在这句话里是抽签决定的
    /// （见 <see cref="RandomCall"/>），所以渲染时要一次拿到抽中的整组人。
    /// </summary>
    public const string RandomStudent = "randomStudent";

    public static readonly string[] All =
        [Text, StudentName, StudentShort, StudentNo, Group, Teacher, RandomStudent];

    /// <summary>界面上那个组件叫什么。</summary>
    public static string Label(string kind) => kind switch
    {
        StudentName => "学生（姓名）",
        StudentShort => "学生（简写）",
        StudentNo => "学生（学号）",
        Group => "小组成员",
        Teacher => "教师名字",
        RandomStudent => "随机叫人",
        _ => "文字",
    };

    /// <summary>是不是"每个学生一条"的那类组件。</summary>
    public static bool IsPerStudent(string kind)
        => kind is StudentName or StudentShort or StudentNo;

    /// <summary>是不是"随机叫人"这一种（它会改变整条呼叫的拼法，见 CallComposer）。</summary>
    public static bool IsRandom(string kind) => kind == RandomStudent;
}

/// <summary>
/// 拼装区里的一个组件。
///
/// 做成"小而扁"的结构而不是每类一个类型：组件要能存进模板、要被拖动、要被序列化，
/// 而它们的差别只是"取哪个字段" —— 一个 Kind 加一个 Text 就够了，
/// 多态层次在这里只会让序列化和拖放都变复杂。
/// </summary>
public sealed class MessageComponent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Kind { get; set; } = MessageComponentKinds.Text;

    /// <summary><see cref="MessageComponentKinds.Text"/> 时的固定文字；其余种类用不到。</summary>
    public string? Text { get; set; }

    public static MessageComponent Of(string kind, string? text = null) => new() { Kind = kind, Text = text };
}

/// <summary>一个呼叫模板。</summary>
public sealed class CallTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "新模板";

    public List<MessageComponent> Components { get; set; } = [];

    /// <summary>模板里有没有"小组成员"组件 —— 有它时按小组归并发送，而不是逐个学生。</summary>
    public bool HasGroupComponent => Components.Any(c => c.Kind == MessageComponentKinds.Group);

    /// <summary>
    /// 模板里有没有"随机叫人"组件 —— 有它时不再按勾选的学生逐条发，而是先抽签再拼一句。
    /// </summary>
    public bool HasRandomComponent => Components.Any(c => MessageComponentKinds.IsRandom(c.Kind));

    /// <summary>给界面看的摘要：组件用 · 连起来。</summary>
    public string Summary
    {
        get
        {
            if (Components.Count == 0)
            {
                return "（空模板）";
            }

            var parts = Components.Select(component => component.Kind == MessageComponentKinds.Text
                ? (string.IsNullOrEmpty(component.Text) ? "文字" : component.Text)
                : MessageComponentKinds.Label(component.Kind));

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>教师端的呼叫模板设置。</summary>
public sealed class TeacherCallSettings
{
    public List<CallTemplate> Templates { get; set; } = [];

    public string? ActiveTemplateId { get; set; }

    // ======================== 随机叫人的参数 ========================
    //
    // 三个都存本机：它们是"这节课想怎么抽"的临时选择，不该跑到服务器上去。
    // 衰减窗口也是每位老师自己的偏好 —— 有的班一节课 40 分钟，有的连堂 90 分钟。

    /// <summary>随机叫人的范围：只要这一组；留空表示不限。</summary>
    public string? RandomGroup { get; set; }

    /// <summary>随机叫人的范围：只要这个性别（「男」「女」）；留空表示不限。</summary>
    public string? RandomGender { get; set; }

    /// <summary>一次抽几位（1 ～ <see cref="RandomCall.MaxCount"/>）。</summary>
    public int RandomCount { get; set; } = 1;

    /// <summary>时间因子从 1.00 衰减到 0.00 要多少分钟。</summary>
    public int DecayMinutes { get; set; } = (int)RandomCall.DefaultDecayWindow.TotalMinutes;

    /// <summary>衰减窗口（分钟换算成 TimeSpan，非法值退回默认）。</summary>
    public TimeSpan DecayWindow =>
        DecayMinutes > 0 ? TimeSpan.FromMinutes(DecayMinutes) : RandomCall.DefaultDecayWindow;

    /// <summary>默认给一份能直接用的模板 —— 空白的组件面板对第一次用的人毫无提示作用。</summary>
    public static CallTemplate DefaultTemplate() => new()
    {
        Name = "来办公室",
        Components =
        [
            MessageComponent.Of(MessageComponentKinds.StudentName),
            MessageComponent.Of(MessageComponentKinds.Text, " 来 "),
            MessageComponent.Of(MessageComponentKinds.Teacher),
            MessageComponent.Of(MessageComponentKinds.Text, " 办公室"),
        ],
    };

    /// <summary>随机叫人那条默认模板：一句话把抽到的人叫起来回答。</summary>
    public static CallTemplate RandomTemplate() => new()
    {
        Name = "随机叫人",
        Components =
        [
            MessageComponent.Of(MessageComponentKinds.Text, "请 "),
            MessageComponent.Of(MessageComponentKinds.RandomStudent),
            MessageComponent.Of(MessageComponentKinds.Text, " 来回答这个问题"),
        ],
    };

    public CallTemplate? Active => Templates.FirstOrDefault(t => t.Id == ActiveTemplateId) ?? Templates.FirstOrDefault();

    /// <summary>把离谱的取值收回来（名单/设置文件都可能被手改过）。</summary>
    public TeacherCallSettings Normalized()
    {
        RandomCount = Math.Clamp(RandomCount <= 0 ? 1 : RandomCount, 1, RandomCall.MaxCount);

        if (!RandomCall.DecayChoices.Contains(TimeSpan.FromMinutes(DecayMinutes)))
        {
            DecayMinutes = (int)RandomCall.DefaultDecayWindow.TotalMinutes;
        }

        RandomGender = RosterCsv.NormalizeGender(RandomGender);
        RandomGroup = string.IsNullOrWhiteSpace(RandomGroup) ? null : RandomGroup.Trim();

        return this;
    }
}

/// <summary>
/// 把模板 + 选中的学生拼成要发出去的几句话。
///
/// 两条规则，都是照着教室里那块屏的实际需要定的：
///
/// 1. **组件顺序拼接，之间不加空格。** 空格由"文字"组件自己带 ——
///    "张三" + " 来 " + "数学张老师" 才是"张三 来 数学张老师"，
///    而"张三" + "：" + "请到办公室"要的是标点紧贴。自动加空格会让后者变得难看，
///    而"作者自己控制空格"这件事对写模板的人来说很自然。
///
/// 2. **含"小组成员"组件时按小组归并，否则每个学生一条。**
///    老师一次叫三个人，教室里要的是"张三、李四、王五 来办公室"这样一句，
///    而不是连着闪三条几乎一样的喊话。
/// </summary>
public static class CallComposer
{
    /// <summary>
    /// 拼装。
    /// </summary>
    /// <param name="template">模板。</param>
    /// <param name="students">选中的学生（顺序按名单里的顺序）。</param>
    /// <param name="roster">学生所在的整份名单 —— "小组成员"要按它取同组的人。</param>
    /// <param name="teacherName">老师自己的名字（含任教科目）。</param>
    /// <returns>要依次发出去的文本；没有可发的就返回空表。</returns>
    public static IReadOnlyList<string> Compose(
        CallTemplate template,
        IReadOnlyList<Student> students,
        StudentRoster? roster,
        string teacherName)
    {
        if (template.Components.Count == 0 || students.Count == 0)
        {
            return [];
        }

        if (template.HasGroupComponent)
        {
            return ComposeByGroup(template, students, roster, teacherName);
        }

        var messages = new List<string>(students.Count);

        foreach (var student in students)
        {
            var text = Render(template, student, students, roster, teacherName).Trim();

            if (text.Length > 0)
            {
                messages.Add(text);
            }
        }

        return messages;
    }

    /// <summary>按小组归并：同一组的选中学生合成一条。</summary>
    private static IReadOnlyList<string> ComposeByGroup(
        CallTemplate template,
        IReadOnlyList<Student> students,
        StudentRoster? roster,
        string teacherName)
    {
        var messages = new List<string>();

        // 没有小组的学生各算一组（用他自己的 Id 当键），
        // 否则"没填小组"的人会被并进同一条 —— 而他们本来不是一组的。
        var buckets = students
            .GroupBy(student => string.IsNullOrWhiteSpace(student.Group)
                ? $"#{student.Id}"
                : student.Group.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var bucket in buckets)
        {
            var members = bucket.ToList();

            // 代表用组里第一位：group 组件会把整组成员列出来，
            // 其余组件（姓名/学号之类）取他这一位就够了。
            var text = Render(template, members[0], members, roster, teacherName).Trim();

            if (text.Length > 0)
            {
                messages.Add(text);
            }
        }

        return messages;
    }

    /// <summary>
    /// 随机叫人：把抽中的那几位拼成**一句**。
    ///
    /// 与 <see cref="Compose"/> 的区别在"谁出现在句子里"：那边是勾选的学生各发一条，
    /// 这边是先抽签（见 <see cref="RandomCall.Pick"/>）、再把这一组人拼进同一句 ——
    /// 随机叫人要的正是"请 张三、李四 来回答"这样一句话，而不是抽三个人发三条。
    ///
    /// 模板里同时写了别的组件时按这个规矩解释（写在文档里，免得靠猜）：
    ///   · 「随机叫人」→ 抽中这几位，顿号分隔；
    ///   · 姓名 / 简写 / 学号 → 抽中的第一位（用来做"张三 请回答"这种写法）；
    ///   · 「小组成员」→ 抽中这几位所在的小组（去重，顿号分隔）。
    /// </summary>
    public static string? ComposeRandom(
        CallTemplate template,
        IReadOnlyList<Student> picked,
        StudentRoster? roster,
        string teacherName)
    {
        if (template.Components.Count == 0 || picked.Count == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var component in template.Components)
        {
            builder.Append(component.Kind switch
            {
                MessageComponentKinds.RandomStudent => string.Join("、", picked.Select(s => s.Name)),

                MessageComponentKinds.StudentName => picked[0].Name,
                MessageComponentKinds.StudentShort => picked[0].ShortName ?? string.Empty,
                MessageComponentKinds.StudentNo => picked[0].StudentNo ?? string.Empty,

                MessageComponentKinds.Group => PickedGroups(picked),

                MessageComponentKinds.Teacher => teacherName,

                _ => component.Text ?? string.Empty,
            });
        }

        _ = roster;

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>抽中这几位所在的小组（去重、顿号分隔）。没分组的不占位置。</summary>
    private static string PickedGroups(IReadOnlyList<Student> picked)
    {
        var groups = picked
            .Select(student => student.Group?.Trim())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups.Count == 0 ? string.Empty : string.Join("、", groups);
    }

    /// <summary>把一条消息渲染出来。<paramref name="selected"/> 用于"小组成员"取同组的人。</summary>
    private static string Render(
        CallTemplate template,
        Student student,
        IReadOnlyList<Student> selected,
        StudentRoster? roster,
        string teacherName)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var component in template.Components)
        {
            builder.Append(component.Kind switch
            {
                MessageComponentKinds.StudentName => student.Name,
                MessageComponentKinds.StudentShort => student.ShortName ?? string.Empty,
                MessageComponentKinds.StudentNo => student.StudentNo ?? string.Empty,

                // 小组成员刻意取**整份名单**里同组的人，而不是只在选中的那几位里找：
                // "叫这一组"的含义就是这一组的全部人，漏掉没被勾上的同学反而奇怪。
                MessageComponentKinds.Group => GroupMembers(student, selected, roster),

                MessageComponentKinds.Teacher => teacherName,

                _ => component.Text ?? string.Empty,
            });
        }

        return builder.ToString();
    }

    /// <summary>这名学生所在小组的成员名单（顿号分隔）。</summary>
    private static string GroupMembers(Student student, IReadOnlyList<Student> selected, StudentRoster? roster)
    {
        if (string.IsNullOrWhiteSpace(student.Group))
        {
            // 没分组的就报他自己，总比输出一句"、来办公室"强
            return student.Name;
        }

        var group = student.Group.Trim();
        var source = roster?.Students ?? selected;

        var members = source
            .Where(item => string.Equals(item.Group?.Trim(), group, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .ToList();

        return members.Count == 0 ? student.Name : string.Join("、", members);
    }
}
