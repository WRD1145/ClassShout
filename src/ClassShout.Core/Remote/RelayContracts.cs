using ClassShout.Core.Protocol;

namespace ClassShout.Core.Remote;

/// <summary>
/// 跨局域网中继的线路契约。
///
/// 放在 Core 里是因为服务器、教室端、教师端三边都要引用同一份定义 ——
/// 任何一边单独改字段都会立刻编译失败，而不是等到运行时才发现对不上。
/// </summary>
public static class RelayPaths
{
    public const string Health = "/api/health";

    public const string RegisterClassroom = "/api/classrooms/register";

    /// <summary>查询教室是否已注册。教室端启动时用它判断该走注册还是校验。</summary>
    public const string LookupClassroom = "/api/classrooms/{0}";

    /// <summary>教师端绑定教室，需要 UUID + secret。</summary>
    public const string BindTeacher = "/api/teachers/bind";

    public const string TeacherText = "/api/teachers/{0}/text";
    public const string TeacherAudioStart = "/api/teachers/{0}/audio/start";
    public const string TeacherAudio = "/api/teachers/{0}/audio";
    public const string TeacherAudioEnd = "/api/teachers/{0}/audio/end";

    // —— 图片喊话。和音频一样三段式：先声明、再分片、最后收尾 ——
    public const string TeacherImageStart = "/api/teachers/{0}/image/start";
    public const string TeacherImageChunk = "/api/teachers/{0}/image/chunk";
    public const string TeacherImageEnd = "/api/teachers/{0}/image/end";

    public const string TeacherStop = "/api/teachers/{0}/stop";
    public const string TeacherUnbind = "/api/teachers/{0}";

    /// <summary>教师端长轮询收状态（教室端的静音、音量等）。</summary>
    public const string TeacherEvents = "/api/teachers/{0}/events";

    /// <summary>教室端长轮询收喊话 —— 即教室端侧的 Webhook 投递通道。</summary>
    public const string ClassroomEvents = "/api/classrooms/{0}/events";

    public const string ClassroomStatus = "/api/classrooms/{0}/status";

    // —— 账号 ——
    public const string AuthRegister = "/api/auth/register";
    public const string AuthLogin = "/api/auth/login";
    public const string AuthMe = "/api/auth/me";
    public const string AuthLogout = "/api/auth/logout";

    /// <summary>教师端列出「管理员授权给我使用的教室」。登录后无需再填 UUID 与口令即可绑定。</summary>
    public const string TeacherAuthorized = "/api/teachers/authorized";

    /// <summary>管理控制台：班级授权。</summary>
    public const string ConsoleBindings = "/api/console/bindings";

    /// <summary>携带教室/教师会话令牌的请求头名。</summary>
    /// <remarks>
    /// 令牌走请求头而不是查询串：查询串会进访问日志，口令这类东西不该留在日志里。
    /// </remarks>
    public const string TokenHeader = "X-Relay-Token";

    /// <summary>携带登录令牌的请求头名。带上它，服务器就能知道喊话的是哪位老师。</summary>
    public const string AuthTokenHeader = "X-Auth-Token";

    /// <summary>
    /// 把客户端用的格式串转成 ASP.NET 路由模板。
    ///
    /// 为什么要这么绕：客户端需要 <c>string.Format</c> 风格来拼 URL，
    /// 服务器需要 <c>{uuid}</c> 风格的路由模板。两边各写一份迟早会失配，
    /// 而且失配只会在运行时以 404 的形式暴露。用同一个常量转换可以根治这个问题。
    /// </summary>
    public static string Route(string format, string parameterName = "uuid")
        => format.Replace("{0}", "{" + parameterName + "}");
}

/// <summary>事件类型。</summary>
public static class RelayKinds
{
    public const string TextShout = "textShout";
    public const string AudioStart = "audioStart";
    public const string Audio = "audio";
    public const string AudioEnd = "audioEnd";
    public const string ImageStart = "imageStart";
    public const string Image = "image";
    public const string ImageEnd = "imageEnd";
    public const string Stop = "stop";
    public const string Status = "status";
    public const string ClassroomOnline = "classroomOnline";
    public const string ClassroomOffline = "classroomOffline";
}

// ======================== 教室端：注册与查询 ========================

/// <summary>教室端注册请求。首次注册必须带 <see cref="Secret"/>。</summary>
/// <param name="Uuid">教室端首次启动时本地生成的 UUID。</param>
/// <param name="Name">教室名，例如「三年二班」。</param>
/// <param name="Secret">口令。已注册的教室重新连接时也要带上以便校验。</param>
public sealed record ClassroomRegisterRequest(string Uuid, string Name, string? Secret);

/// <param name="Ok">是否成功。</param>
/// <param name="IsNew">本次是否新建了注册记录。</param>
/// <param name="Secret">服务器最终采用的口令。客户端未提供时由服务器生成并回传。</param>
/// <param name="Token">会话令牌，教室端后续的长轮询与状态上报都要带上。</param>
/// <param name="Error">失败原因。</param>
public sealed record ClassroomRegisterResponse(
    bool Ok,
    bool IsNew,
    string Uuid,
    string Name,
    string? Secret,
    string? Token,
    string? Error);

/// <param name="Exists">该 UUID 是否已注册。</param>
/// <param name="Name">已注册的教室名。</param>
public sealed record ClassroomLookupResponse(bool Exists, string? Name);

// ======================== 教师端：绑定 ========================

/// <summary>教师端绑定教室。UUID 定位教室，secret 用于鉴权。</summary>
public sealed record TeacherBindRequest(string Uuid, string Secret, string TeacherName);

/// <param name="Token">绑定成功后拿到的会话令牌，后续发喊话都带它。</param>
public sealed record TeacherBindResponse(bool Ok, string? Token, string? ClassroomName, string? Error);

// ======================== 喊话内容 ========================

/// <summary>
/// 教师端经服务器发一条文字喊话。
///
/// 后面四项是展示参数（见 <see cref="ShoutDisplayModes"/> 等），全部可选：
/// 不填就用教室端的默认值。它们是可空/负数而不是必填枚举，
/// 这样"老教师端发来的请求"与"新教师端没选任何项"在服务器看来是同一种东西 ——
/// 服务器不必理解这些字段，只负责原样转发。
/// </summary>
/// <param name="Text">喊话内容。</param>
/// <param name="Rate">TTS 语速。</param>
/// <param name="Volume">TTS 音量。</param>
/// <param name="Interrupt">是否打断当前朗读。</param>
/// <param name="Display">展示方式，取值见 <see cref="ShoutDisplayModes"/>。</param>
/// <param name="FontSize">字号档位，取值见 <see cref="ShoutFontSizes"/>。</param>
/// <param name="HoldMs">停留时长（毫秒），见 <see cref="ShoutHoldDurations"/>。</param>
/// <param name="Speak">是否朗读。</param>
public sealed record TextShoutRequest(
    string Text,
    int Rate,
    int Volume,
    bool Interrupt,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = ShoutHoldDurations.Unspecified,
    bool Speak = true);

public sealed record AudioStartRequest(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// 图片喊话的声明。之后跟着若干 <see cref="ImageChunkRequest"/>，由 image/end 收尾。
/// </summary>
/// <param name="Id">本次图片的标识，分片与收尾都要带上它。</param>
/// <param name="TotalBytes">图片总字节数。</param>
/// <param name="ContentType">MIME 类型。</param>
/// <param name="Text">随图显示的说明文字。</param>
/// <param name="Width">像素宽（0 表示未知）。</param>
/// <param name="Height">像素高（0 表示未知）。</param>
public sealed record ImageStartRequest(
    string Id,
    int TotalBytes,
    string ContentType,
    string? Text = null,
    int Width = 0,
    int Height = 0,
    string? Display = null,
    string? FontSize = null,
    int HoldMs = ShoutHoldDurations.Unspecified,
    bool Speak = false);

/// <summary>
/// 一片图片数据。
///
/// 走 base64 而不是二进制端点：这条线路上已经有音频在用同一个形状，
/// 多一种消息形态只会让"服务器只是转发"这件事变得不清楚。
/// </summary>
/// <param name="Id">属于哪张图。</param>
/// <param name="DataBase64">这一片的字节（base64）。</param>
public sealed record ImageChunkRequest(string Id, string DataBase64);

public sealed record ClassroomStatusRequest(bool Muted, int Volume, string State);

// ======================== 账号 ========================

/// <summary>注册请求。用户名与邮箱至少要有一个。</summary>
/// <param name="Username">用户名，3~20 位、字母开头、可含数字与下划线。</param>
/// <param name="Email">邮箱。</param>
/// <param name="DisplayName">老师姓名。教室端弹窗与教师端界面显示的就是它。</param>
/// <param name="Password">口令，至少 6 位。</param>
public sealed record RegisterRequest(string? Username, string? Email, string DisplayName, string Password);

/// <summary>登录请求。<paramref name="Account"/> 填用户名或邮箱都可以。</summary>
public sealed record LoginRequest(string Account, string Password);

/// <summary>
/// 服务器健康检查的返回。
///
/// 教师端在「中继服务器」里点「测试连接」时用它 —— 在还没登录、也没有任何凭据的情况下，
/// 这是唯一能确认"地址填对了、对面确实是 ClassShout 服务器"的办法。
/// </summary>
/// <param name="Ok">服务器自认为是否正常。</param>
/// <param name="Service">服务名，用来确认对面不是别的什么东西占着同一个端口。</param>
/// <param name="Protocol">协议版本，供客户端判断兼容性。</param>
/// <param name="Classrooms">已注册教室数；</param>
/// <param name="Users">已注册账号数。两者都只是给管理员看的概览数字。</param>
/// <param name="Time">服务器时间。</param>
public sealed record RelayHealthDto(
    bool Ok,
    string? Service,
    int Protocol,
    int Classrooms,
    int Users,
    DateTimeOffset Time);

/// <summary>账号公开信息。刻意不含任何口令相关字段。</summary>
/// <param name="IsAdmin">是否为内置管理员。管理控制台只对管理员开放。</param>
public sealed record UserProfileDto(
    string Id,
    string? Username,
    string? Email,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool Disabled,
    bool IsAdmin);

/// <summary>管理控制台用的教室条目。</summary>
public sealed record ConsoleClassroom(
    string Uuid,
    string Name,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    int OnlineTeachers);

/// <summary>管理控制台概览。</summary>
public sealed record ConsoleOverview(
    int Classrooms,
    int Users,
    int OnlineTeachers,
    int Bindings,
    bool AdminPasswordIsInitial,
    DateTimeOffset ServerTime,
    string Version);

/// <summary>把某个开关设为指定值（停用账号、提升管理员等通用请求体）。</summary>
public sealed record ConsoleFlagRequest(bool Value);

/// <summary>管理员把某个班级授权给某位老师。</summary>
public sealed record GrantBindingRequest(string UserId, string Uuid);

/// <summary>控制台里的授权记录，带上双方名称便于阅读。</summary>
public sealed record ConsoleBinding(
    string UserId,
    string UserDisplayName,
    string Uuid,
    string ClassroomName,
    string GrantedBy,
    DateTimeOffset GrantedAt);

/// <summary>教师端可见的「已授权教室」。</summary>
/// <param name="Online">该教室当前是否在线（最近有向服务器注册）。</param>
public sealed record AuthorizedClassroom(
    string Uuid,
    string Name,
    bool Online,
    DateTimeOffset LastSeenAt);

/// <summary>管理控制台重置口令。</summary>
/// <param name="UserId">目标账号 Id；重置自己的口令时传自己的 Id。</param>
/// <param name="NewPassword">新口令。</param>
public sealed record ResetPasswordRequest(string UserId, string NewPassword);

/// <param name="Ok">是否成功。</param>
/// <param name="Token">登录令牌，后续请求放 <see cref="RelayPaths.AuthTokenHeader"/> 头里。</param>
/// <param name="User">账号信息。</param>
/// <param name="Error">失败原因，可直接展示给用户。</param>
public sealed record AuthResponse(bool Ok, string? Token, UserProfileDto? User, string? Error);

// ======================== 事件（长轮询返回） ========================

/// <summary>
/// 长轮询投递的一条事件。
///
/// 用一个大而全的扁平结构而不是每类事件一个类型：这条线路上跑的是高频音频分片，
/// 多一层多态解析就多一份开销，而且服务端只是转发、并不理解内容，
/// 扁平结构反而让"服务器不需要懂业务"这一点更清楚。
/// </summary>
public sealed class RelayEnvelope
{
    /// <summary>单调递增序号，客户端据此判断有没有丢事件。</summary>
    public long Sequence { get; set; }

    /// <summary>见 <see cref="RelayKinds"/>。</summary>
    public string Kind { get; set; } = RelayKinds.Status;

    /// <summary>发起方名称（教师名或教室名）。</summary>
    public string? From { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    // —— 文字喊话 ——
    public string? Text { get; set; }
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    public bool Interrupt { get; set; } = true;

    // —— 文字的展示参数（见 ShoutDisplayModes / ShoutFontSizes / ShoutHoldDurations）——
    //
    // 和上面几项一样只是原样转发：服务器不理解它们，也不该理解 ——
    // 教室端才是唯一需要照着这些参数把内容画出来的地方。
    public string? Display { get; set; }
    public string? FontSize { get; set; }

    /// <summary>停留时长（毫秒）。负数表示"没指定，用教室端默认值"。</summary>
    public int HoldMs { get; set; } = ShoutHoldDurations.Unspecified;

    /// <summary>
    /// 是否朗读。
    ///
    /// 老教师端不发这个字段，而 System.Text.Json 对没出现的属性不动对象 ——
    /// 初始值 true 会留着，所以旧客户端的喊话照旧朗读，不会被静默改成"只显示不发声"。
    /// </summary>
    public bool Speak { get; set; } = true;

    // —— 音频 ——
    /// <summary>
    /// PCM 分片，Base64。
    /// 不用二进制端点是为了让整条链路只有一种消息形态；
    /// 100 毫秒一批也只有约 4 KB，Base64 的 33% 开销可以接受。
    /// </summary>
    public string? AudioBase64 { get; set; }

    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;

    // —— 图片 ——
    /// <summary>图片标识。分片与收尾靠它对上号。</summary>
    public string? ImageId { get; set; }

    /// <summary>图片总字节数（imageStart 上带）。</summary>
    public int ImageTotalBytes { get; set; }

    /// <summary>图片的 MIME 类型。</summary>
    public string? ImageContentType { get; set; }

    /// <summary>这一片图片数据（base64）。</summary>
    public string? ImageBase64 { get; set; }

    /// <summary>图片的像素尺寸，供教室端在图片到达之前就摆好版面。</summary>
    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    // —— 状态 ——
    public bool Muted { get; set; }
    public string? State { get; set; }

    // —— 停止 ——
    public string? Reason { get; set; }
}

/// <summary>长轮询的返回体。</summary>
/// <param name="Events">本次拿到的事件，可能为空（超时）。</param>
/// <param name="Next">下次轮询应带的 since 值。</param>
/// <param name="TimedOut">是否因为超时返回而非有新事件。</param>
public sealed record EventBatch(IReadOnlyList<RelayEnvelope> Events, long Next, bool TimedOut);
