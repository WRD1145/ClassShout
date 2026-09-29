namespace ClassShout.Core.Remote;

/// <summary>
/// 账号角色。
///
/// 三级：**教师 &lt; 班主任 &lt; 管理员**。管理员不在这张表里 ——
/// 它不是账号上的一个字段，而是"这个身份就是配置里写的那一个"（见服务器里的
/// IsAdminIdentity）：把管理员做成可以随手勾的一个角色，等于给了一个"给自己升权"
/// 的入口，而管理员本来就是运维在服务器上定的。
///
/// 班主任处在中间：他能管**自己被授予班主任的那几间班**的权限（给别的老师授权、
/// 收回、上传这个班统一用的名单），但管不到别人的班，也管不到账号本身。
/// 刻意做成"账号角色 + 每个班一条带标记的授权"两层：一位老师完全可能既是二班的
/// 班主任、又只是三班的任课老师 —— 只用一个账号字段表达不了这件事。
/// </summary>
public static class UserRoles
{
    /// <summary>普通教师（默认）。</summary>
    public const string Teacher = "teacher";

    /// <summary>班主任。</summary>
    public const string HeadTeacher = "headTeacher";

    public static IReadOnlyList<string> All { get; } = [Teacher, HeadTeacher];

    /// <summary>收口：认不出来的角色一律当普通教师，绝不因为一个写坏的值而放大权限。</summary>
    public static string Normalize(string? role)
        => string.Equals(role, HeadTeacher, StringComparison.OrdinalIgnoreCase) ? HeadTeacher : Teacher;

    public static bool IsKnown(string? role)
        => !string.IsNullOrWhiteSpace(role)
           && All.Any(item => string.Equals(item, role.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsHeadTeacher(string? role) => Normalize(role) == HeadTeacher;

    /// <summary>界面上显示的名字。</summary>
    public static string Label(string? role) => IsHeadTeacher(role) ? "班主任" : "教师";

    /// <summary>一句话说明这个角色能做什么（控制台上跟在角色后面给人看）。</summary>
    public static string Describe(string? role) => IsHeadTeacher(role)
        ? "可以管理自己被授予班主任的班级：给别的老师授权、收回，并上传那个班统一使用的名单。"
        : "可以给自己被授权的班级喊话，并管理自己的名单。";
}

/// <summary>
/// 某个班里，老师实际该用哪一份名单。
///
/// 规则来自班主任与任课老师之间的分工：
///   · 班主任传了一份名单、并且**设为强制** → 这个班一律用它，任课老师自己传的也压不过它
///     （一个班到底按哪份名单叫人，必须只有一个答案，否则同一个班里两位老师叫出来的人不一样）；
///   · 班主任没设强制（或压根没传） → 任课老师可以传自己的、用自己的；
///   · 任课老师自己没传，但班主任传了一份默认的 → 用班主任那份（不用自己再录一遍）。
///
/// 这套判定放在 Core 里由服务器与客户端共用：服务器据此决定回哪一份名单，
/// 客户端据此决定"要不要让我上传"以及"为什么不让"——两边各写一遍迟早会不一致，
/// 而不一致的表现是"界面允许上传、服务器默默丢掉"。
/// </summary>
public static class ClassroomRosterRules
{
    /// <summary>用我自己导入的名单。</summary>
    public const string Own = "own";

    /// <summary>用班主任统一上传的名单。</summary>
    public const string HeadTeacher = "headTeacher";

    /// <summary>这个班现在没有名单可用。</summary>
    public const string Empty = "empty";

    public static string Resolve(bool hasHeadTeacherRoster, bool enforced, bool hasOwnRoster)
    {
        if (hasHeadTeacherRoster && enforced)
        {
            return HeadTeacher;
        }

        if (hasOwnRoster)
        {
            return Own;
        }

        return hasHeadTeacherRoster ? HeadTeacher : Empty;
    }

    public static string Label(string? source) => Normalize(source) switch
    {
        Own => "我自己导入的名单",
        HeadTeacher => "班主任统一上传的名单",
        _ => "还没有名单",
    };

    /// <summary>收口：认不出来的来源当"没有名单"，不要凭空说成"用你的名单"。</summary>
    public static string Normalize(string? source) => source switch
    {
        Own => Own,
        HeadTeacher => HeadTeacher,
        Empty => Empty,
        _ => Empty,
    };

    /// <summary>老师这时候能不能上传自己的名单。被强制时不能，界面要据此把入口关掉并说明原因。</summary>
    public static bool CanUploadOwn(string? source) => Normalize(source) != HeadTeacher;

    /// <summary>不能上传时给老师看的那句话。</summary>
    public static string LockedHint(string? classroomName)
        => $"这个班（{classroomName ?? "当前班级"}）的名单由班主任统一管理并设为了强制，"
           + "你不能再上传自己的名单。要改用你自己那份，请联系班主任取消强制。";
}
