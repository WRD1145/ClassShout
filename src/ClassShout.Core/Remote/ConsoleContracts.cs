namespace ClassShout.Core.Remote;

/// <summary>控制台手动创建账号。</summary>
/// <param name="Username">用户名，与邮箱至少填一个。</param>
/// <param name="Email">邮箱。</param>
/// <param name="DisplayName">显示名（教师端与教室端弹窗显示的就是它）。</param>
/// <param name="Password">初始口令。老师拿到后可以自己改。</param>
/// <param name="Subject">
/// 任教科目，例如「数学」。可留空 —— 留空时喊话来源只报姓名。
///
/// 管理员在控制台建号时顺手填上，比让每位老师自己在手机上补更省事：
/// 开学时录一批账号，本来就知道谁教什么。
/// </param>
public sealed record CreateUserRequest(
    string? Username,
    string? Email,
    string? DisplayName,
    string Password,
    string? Subject = null);

/// <summary>控制台批量导入账号。</summary>
/// <param name="Csv">CSV 文本，每行一个账号。</param>
public sealed record ImportUsersRequest(string Csv);

/// <summary>批量导入的结果。</summary>
/// <param name="Created">成功创建的条数。</param>
/// <param name="Failed">失败的条数。</param>
/// <param name="Details">逐行说明，只列失败的与成功的摘要，便于直接贴回给管理员核对。</param>
public sealed record ImportUsersResponse(int Created, int Failed, IReadOnlyList<string> Details);

/// <summary>
/// 控制台改一位老师的任教科目。
/// <paramref name="Value"/> 留空（或只有空白）表示清掉默认科目，喊话来源回到只报姓名。
/// </summary>
/// <param name="Value">默认科目（没被 <paramref name="ByClassroom"/> 覆盖的班级用它）。</param>
/// <param name="ByClassroom">
/// 按班级覆盖：教室 UUID → 科目。传 null 表示只改默认科目、不动覆盖表；
/// 传一份表（可以是空表）则整表替换。
/// </param>
public sealed record ConsoleSubjectRequest(
    string? Value,
    IReadOnlyDictionary<string, string?>? ByClassroom = null);

/// <summary>管理员在控制台上直接对某个班级喊话。</summary>
/// <param name="Uuid">教室 UUID。</param>
/// <param name="Text">要朗读的文字。</param>
/// <param name="Rate">语速。</param>
/// <param name="Volume">音量。</param>
/// <param name="Interrupt">是否打断教室里当前的朗读。</param>
public sealed record ConsoleShoutRequest(string Uuid, string Text, int Rate = 1, int Volume = 100, bool Interrupt = true);