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

/// <summary>老师（网页端）能喊话的一个班级。</summary>
/// <param name="Uuid">教室 UUID。</param>
/// <param name="Name">教室名。</param>
/// <param name="Online">当前是否在线（最近一分半内有过动静）。</param>
/// <param name="LastSeenAt">最近一次活动时间。</param>
public sealed record TeacherClassroomDto(string Uuid, string Name, bool Online, DateTimeOffset LastSeenAt);

/// <summary>老师从网页喊一句话。参数与 App 里那条完全一致。</summary>
/// <param name="TargetUuids">发给哪几个班（可以多选）。</param>
/// <param name="Text">要朗读的文字。</param>
/// <param name="Rate">语速。</param>
/// <param name="Volume">音量。</param>
/// <param name="Interrupt">是否打断教室里当前的朗读。</param>
/// <param name="Display">展示方式（留空＝用教室端默认）。</param>
/// <param name="FontSize">字号档位。</param>
/// <param name="HoldMs">停留时长。</param>
/// <param name="Speak">是否朗读。</param>
public sealed record TeacherWebShoutRequest(
    IReadOnlyList<string> TargetUuids,
    string Text,
    int Rate = 1,
    int Volume = 100,
    bool Interrupt = true,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = Protocol.ShoutHoldDurations.Unspecified,
    bool Speak = true);

/// <summary>逐间的结果 —— 有一间没送到时要能说清是哪一间。</summary>
/// <param name="Uuid">教室 UUID。</param>
/// <param name="ClassroomName">教室名。</param>
/// <param name="Ok">这一间是否发出去了。</param>
/// <param name="Error">失败原因。</param>
public sealed record TeacherShoutResult(string Uuid, string ClassroomName, bool Ok, string? Error);

/// <summary>网页喊话的结果。</summary>
/// <param name="Ok">至少发出去一间。</param>
/// <param name="Sent">成功几间。</param>
/// <param name="Results">逐间结果。</param>
/// <param name="Message">给人看的一句话。</param>
public sealed record TeacherWebShoutResponse(
    bool Ok,
    int Sent,
    IReadOnlyList<TeacherShoutResult> Results,
    string Message);
// ======================== 名单与呼叫（老师同步到服务器，WebUI 也能呼叫） ========================
//
// 为什么名单要上服务器：客户端的「呼叫」必须以名单为前提，而 WebUI 跑在服务器上、
// 看不到老师手机里的那份名单。同步一份上去，WebUI 才能用**同一份名单、同一套拼装规则**
// 拼出同样的话 —— 拼装本身复用 Core 里的 CallComposer，两边跑的是同一段代码，
// 所以"要求与客户端一致"是结构上保证的，不是照着抄一遍。

/// <summary>服务器上存着的某位老师的名单与呼叫模板。</summary>
/// <param name="Rosters">名单（一位老师通常教好几个班，可以有好几份）。</param>
/// <param name="ActiveRosterId">当前选中的名单。</param>
/// <param name="Templates">呼叫模板。</param>
/// <param name="ActiveTemplateId">当前选中的模板。</param>
/// <param name="UpdatedAt">最近一次同步时间。</param>
public sealed record TeacherRosterSnapshot(
    IReadOnlyList<StudentRoster> Rosters,
    string? ActiveRosterId,
    IReadOnlyList<CallTemplate> Templates,
    string? ActiveTemplateId,
    DateTimeOffset? UpdatedAt);

/// <summary>老师把名单与模板同步到服务器。留空表示"这一项不动"。</summary>
public sealed record TeacherRosterUpload(
    IReadOnlyList<StudentRoster>? Rosters = null,
    string? ActiveRosterId = null,
    IReadOnlyList<CallTemplate>? Templates = null,
    string? ActiveTemplateId = null,
    string? CsvText = null,
    string? RosterName = null);

/// <summary>WebUI 上拼一次呼叫。</summary>
/// <param name="TargetUuids">发给哪几个班。</param>
/// <param name="StudentIds">选了哪几位学生（服务器用名单里的顺序与字段拼装）。</param>
/// <param name="TemplateId">用哪个模板；留空表示用当前模板。</param>
/// <param name="Components">临时拼的组件（给了它就按它拼，不落盘）。</param>
/// <param name="Rate">语速。</param>
/// <param name="Volume">音量。</param>
/// <param name="Interrupt">是否打断教室里当前的朗读。</param>
/// <param name="Display">展示方式。</param>
/// <param name="FontSize">字号档位。</param>
/// <param name="HoldMs">停留时长。</param>
/// <param name="Speak">是否朗读。</param>
public sealed record TeacherCallRequest(
    IReadOnlyList<string> TargetUuids,
    IReadOnlyList<string> StudentIds,
    string? TemplateId = null,
    IReadOnlyList<MessageComponent>? Components = null,
    int Rate = 1,
    int Volume = 100,
    bool Interrupt = true,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = Protocol.ShoutHoldDurations.Unspecified,
    bool Speak = true);

/// <summary>WebUI 呼叫的结果。</summary>
/// <param name="Ok">至少发出去一间。</param>
/// <param name="Sent">成功几间。</param>
/// <param name="Messages">拼出来的那几句话（原样回给界面，老师能看见到底喊了什么）。</param>
/// <param name="Results">逐间结果。</param>
/// <param name="Message">给人看的一句话。</param>
public sealed record TeacherCallResponse(
    bool Ok,
    int Sent,
    IReadOnlyList<string> Messages,
    IReadOnlyList<TeacherShoutResult> Results,
    string Message);

/// <summary>同步名单的结果（服务器只回一句"成没成"和原因，名单本身不必回传）。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Rosters">服务器上现在有几份名单。</param>
/// <param name="Templates">服务器上现在有几个模板。</param>
public sealed record RosterSyncResult(bool Ok, string? Error = null, int Rosters = 0, int Templates = 0);
