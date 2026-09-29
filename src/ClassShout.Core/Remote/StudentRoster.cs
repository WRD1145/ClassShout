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

    /// <summary>
    /// 性别。可空 —— 名单里没这一列时就是空的，随机叫人里"按性别筛选"也就用不上。
    /// </summary>
    public string? Gender { get; set; }

    /// <summary>
    /// 时间因子（隐形 tag）：越大代表"刚被叫过"，被随机抽中的概率越小。
    ///
    /// 语义见 <see cref="RandomCall"/>：0.00 表示很久没叫到（概率最高），上限 1.00；
    /// 被抽中一次就重置到 0.95～1.00 之间，之后随时间线性衰减回 0。
    /// 界面上不显示它 —— 它对老师没有意义，显示出来只会让人以为哪里坏了。
    /// </summary>
    public double TimeFactor { get; set; }

    /// <summary>
    /// 上一次"把时间因子写成 <see cref="TimeFactor"/> 这个值"的时刻。
    ///
    /// 存时刻而不是靠定时器不停改：因子随时间衰减，而"现在是多少"用一个减法就推得出来
    /// （见 <see cref="RandomCall.EffectiveFactor"/>）—— 后台跑个计时器去改它，
    /// 只会让应用必须一直开着，还会在休眠、改系统时间之后彻底失准。
    /// </summary>
    public DateTimeOffset? FactorSetAt { get; set; }

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
/// 学生名单的导入：CSV（粘贴或文件）与 Excel（.xlsx / .xls）走**同一套行解析**。
///
/// 为什么两种格式共用一段：学校里流传的名单一半是教务系统导出的表、一半是从 Excel
/// 里复制粘贴的一片字，而"哪些行该跳过""表头怎么认""空列怎么算"这些规则必须一致 ——
/// 否则同一份名单从文件导入和从剪贴板导入会得到两个结果，那才是最难查的一种错。
///
/// 宽容规则（与"控制台批量导入老师"同一套）：带表头、带空行、带引号、
/// 少几列都行。老师的名单通常是从教务系统里导出、或者在 Excel 里手打的，
/// 要求它格式规整，等于要求老师先学一遍 CSV。
/// </summary>
public static class RosterCsv
{
    /// <summary>列顺序：姓名,学号,简写,小组,性别。只有姓名必填，后面四列可选。</summary>
    public static IReadOnlyList<string> Columns { get; } = ["姓名", "学号", "简写", "小组", "性别"];

    /// <summary>从一段文本（粘贴的内容，或 .csv 文件的内容）解析。</summary>
    public static RosterImportResult Parse(string? text, string rosterName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new RosterImportResult(null, ["没有可导入的内容。"]);
        }

        return FromRows(
            text.ReplaceLineEndings("\n").Split('\n').Select(CsvLine.Split).ToList(),
            rosterName,
            source: null);
    }

    /// <summary>从一张表里解析：<paramref name="rows"/> 已经按行/列切好（Excel 与 CSV 都归到这里）。</summary>
    /// <param name="rows">每一行是一个字段数组。</param>
    /// <param name="rosterName">名单名。</param>
    /// <param name="source">来源文件名（用于提示里写清是哪一份文件；为空表示粘贴的内容）。</param>
    public static RosterImportResult FromRows(
        IReadOnlyList<IReadOnlyList<string?>> rows,
        string rosterName,
        string? source = null)
    {
        var roster = new StudentRoster { Name = string.IsNullOrWhiteSpace(rosterName) ? "学生名单" : rosterName.Trim() };
        var skipped = new List<string>();
        var where = string.IsNullOrWhiteSpace(source) ? string.Empty : $"（{source}）";

        var isFirstRow = true;

        for (var index = 0; index < rows.Count; index++)
        {
            var fields = rows[index];
            var lineNumber = index + 1;

            // 整行空：跳过，不当作错误（表格里常有这种收尾行）
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            // 以 # 开头的注释行：粘贴的名单里常有人拿它写"三年二班名单"这种标题
            if (fields.Count == 1 && (fields[0]?.TrimStart().StartsWith('#') ?? false))
            {
                continue;
            }

            var first = (fields.Count > 0 ? fields[0] : null)?.Trim() ?? string.Empty;

            // 表头行跳过：从 Excel 复制、或直接读表格时几乎一定带着它
            if (isFirstRow && (first.Contains("姓名") || first.Equals("name", StringComparison.OrdinalIgnoreCase)))
            {
                isFirstRow = false;
                continue;
            }

            isFirstRow = false;

            if (first.Length == 0)
            {
                skipped.Add($"第 {lineNumber} 行{where}：没有姓名，已跳过。");
                continue;
            }

            roster.Students.Add(new Student
            {
                Name = first,
                StudentNo = At(fields, 1),
                ShortName = At(fields, 2),
                Group = At(fields, 3),
                Gender = NormalizeGender(At(fields, 4)),
            });
        }

        return new RosterImportResult(roster.Students.Count > 0 ? roster : null, skipped);
    }

    /// <summary>
    /// 把性别统一成「男」「女」，认不出来的留空。
    ///
    /// 认得出几种常见写法（男/女、M/F、male/female、1/0、男生/女生）——
    /// 教务系统导出的表里这几样都有；认不出就留空，而不是原样存进去：
    /// 留空只意味着"这一项筛不了"，原样存进去则会让"按性别筛选"出现
    /// 「男」「男生」「M」三个互不相干的选项。
    /// </summary>
    public static string? NormalizeGender(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        return text switch
        {
            "男" or "男生" or "男性" or "M" or "m" or "male" or "Male" or "MALE" or "1" => "男",
            "女" or "女生" or "女性" or "F" or "f" or "female" or "Female" or "FEMALE" or "0" => "女",
            _ => null,
        };
    }

    /// <summary>取第 n 列；没有或为空都返回 null（空字符串会被写成"填了但空着"）。</summary>
    private static string? At(IReadOnlyList<string?> fields, int index)
        => index < fields.Count && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index]!.Trim() : null;
}
