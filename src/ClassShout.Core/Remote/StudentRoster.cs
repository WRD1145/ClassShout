namespace ClassShout.Core.Remote;

/// <summary>一名学生。</summary>
public sealed class Student
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>姓名。必填 —— 少了它这一行就没有意义。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>学号。可空。</summary>
    public string? StudentNo { get; set; }

    /// <summary>简写（名单上常用的那种短称呼，例如"小张"）。可空。</summary>
    public string? ShortName { get; set; }

    /// <summary>小组。可空。</summary>
    public string? Group { get; set; }

    /// <summary>界面上显示这个学生时用哪个标识，见 <see cref="StudentLabelStyles"/>。</summary>
    public string? LabelStyle { get; set; }

    /// <summary>
    /// 喊话里用的完整标识：<c>姓名（学号，简写，小组）</c>，括号内填了才显示。
    ///
    /// 格式固定成这一种，是因为教室里那块屏上只有一行字：
    /// 老师按姓名叫人、而学生之间用简写互称、点名时又要对学号 ——
    /// 把填过的都带上，看一眼就能确认叫的是不是自己。
    /// 没填的那些不占位置，不然满屏都是空括号。
    /// </summary>
    public string Label => StudentLabel.Format(Name, StudentNo, ShortName, Group);

    /// <summary>这一行还缺哪些字段（界面提示用）。</summary>
    public bool HasStudentNo => !string.IsNullOrWhiteSpace(StudentNo);

    public bool HasShortName => !string.IsNullOrWhiteSpace(ShortName);

    public bool HasGroup => !string.IsNullOrWhiteSpace(Group);
}

/// <summary>学生标识的样式：选择列表里用哪一种显示。</summary>
public static class StudentLabelStyles
{
    /// <summary>姓名（默认）。</summary>
    public const string Name = "name";

    /// <summary>学号。只有填了学号的学生才可选这一档。</summary>
    public const string StudentNo = "studentNo";

    /// <summary>简写。只有填了简写的学生才可选这一档。</summary>
    public const string ShortName = "shortName";

    public static readonly string[] All = [Name, StudentNo, ShortName];

    /// <summary>界面上显示的名字。</summary>
    public static string Label(string style) => style switch
    {
        StudentNo => "学号",
        ShortName => "简写",
        _ => "姓名",
    };
}

/// <summary>把姓名与那几个可选字段拼成喊话里用的标识。</summary>
public static class StudentLabel
{
    /// <summary>
    /// 拼成 <c>姓名（学号，简写，小组）</c>。
    ///
    /// 括号内只放**填了的**那几项，一项都没填就只是一个姓名 ——
    /// 教室里那块屏是给全班看的，满屏的空括号既占地方又像是在出故障。
    /// </summary>
    public static string Format(string? name, string? studentNo, string? shortName, string? group)
    {
        var head = string.IsNullOrWhiteSpace(name) ? "（未命名）" : name.Trim();

        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(studentNo))
        {
            parts.Add(studentNo.Trim());
        }

        if (!string.IsNullOrWhiteSpace(shortName))
        {
            parts.Add(shortName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            parts.Add(group.Trim());
        }

        return parts.Count == 0 ? head : $"{head}（{string.Join("，", parts)}）";
    }
}

/// <summary>一份学生名单。</summary>
public sealed class StudentRoster
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>名单名，例如"三年二班"。导入时默认取文件名，也可以改。</summary>
    public string Name { get; set; } = "学生名单";

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;

    public List<Student> Students { get; set; } = [];

    /// <summary>去重后的所有小组（按出现顺序）。</summary>
    public IReadOnlyList<string> Groups =>
        Students
            .Select(student => student.Group)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>教师端的学生名单设置（可以有好多份 —— 一位老师通常教好几个班）。</summary>
public sealed class TeacherRosterSettings
{
    public List<StudentRoster> Rosters { get; set; } = [];

    /// <summary>当前选中的名单。</summary>
    public string? ActiveRosterId { get; set; }

    /// <summary>当前选中的学生（跨设备记住"我正在叫谁"）。</summary>
    public List<string> SelectedStudentIds { get; set; } = [];

    /// <summary>选择列表里用哪一种标识显示（姓名 / 学号 / 简写）。</summary>
    public string LabelStyle { get; set; } = StudentLabelStyles.Name;

    /// <summary>取当前名单；没有就返回第一份。</summary>
    public StudentRoster? Active => Rosters.FirstOrDefault(roster => roster.Id == ActiveRosterId) ?? Rosters.FirstOrDefault();
}

/// <summary>一次导入的结果。</summary>
/// <param name="Roster">解析出来的名单；一行都没解析出来时为 null。</param>
/// <param name="SkippedLines">被跳过的行与原因，直接显示给用户。</param>
public readonly record struct RosterImportResult(StudentRoster? Roster, IReadOnlyList<string> SkippedLines)
{
    public bool Ok => Roster is not null && Roster.Students.Count > 0;
}

/// <summary>
/// 学生名单的 CSV 导入。
///
/// 与"控制台批量导入老师"用的是同一套宽容规则：带表头、带空行、带引号、
/// 从 Excel 直接复制粘贴都能读。老师的名单通常就是从教务系统里导出、
/// 或者在 Excel 里手打的一份表 —— 要求它格式规整，等于要求老师先学一遍 CSV。
/// </summary>
public static class RosterCsv
{
    /// <summary>列顺序：姓名,学号,简写,小组。后三列可选。</summary>
    public static RosterImportResult Parse(string? text, string rosterName)
    {
        var roster = new StudentRoster { Name = string.IsNullOrWhiteSpace(rosterName) ? "学生名单" : rosterName.Trim() };
        var skipped = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new RosterImportResult(null, ["没有可导入的内容。"]);
        }

        var lineNumber = 0;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split(',').Select(field => field.Trim().Trim('"').Trim()).ToArray();

            // 表头行跳过：从 Excel 复制时几乎一定带着它
            if (lineNumber == 1 &&
                (fields[0].Contains("姓名") || fields[0].Equals("name", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = fields.Length > 0 ? fields[0] : string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                skipped.Add($"第 {lineNumber} 行：没有姓名，已跳过。");
                continue;
            }

            roster.Students.Add(new Student
            {
                Name = name,
                StudentNo = At(fields, 1),
                ShortName = At(fields, 2),
                Group = At(fields, 3),
            });
        }

        return new RosterImportResult(roster.Students.Count > 0 ? roster : null, skipped);
    }

    /// <summary>取第 n 列；没有或为空都返回 null（空字符串会被写成"填了但空着"）。</summary>
    private static string? At(string[] fields, int index)
        => index < fields.Length && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index] : null;
}
