using System.Reflection;
using System.Threading.RateLimiting;
using ClassShout.Core.Audio;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.RelayServer;
using Microsoft.AspNetCore.Mvc;

// ============================================================================
//  ClassShout 跨局域网中继服务器
//
//  职责边界：只做「鉴权 + 转发」，完全不理解喊话内容。
//  音频对它就是一串字节，文字对它就是一个字符串 —— 因此服务器不需要随
//  客户端一起升级，也不会因为喊话内容的形态变化而受影响。
//
//  投递方式：HTTP 长轮询。
//  真正的 Webhook（服务器主动 POST 到客户端注册的 URL）在 NAT 之后无法工作 ——
//  公网服务器连不进教室内网。长轮询让客户端主动发起并保持一个 GET，
//  服务器有数据时才响应，效果等价而在任何网络环境下都能用。
//  详见 README 的「为什么不是真正的 Webhook」。
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

var statePath = Environment.GetEnvironmentVariable("CLASSSHOUT_RELAY_STATE")
                ?? Path.Combine(AppContext.BaseDirectory, "relay-state.json");

// 账号单独一个文件：它和教室注册记录的变更时机、备份策略都不一样，
// 混在一起会让"只想重置某个教室"变成一件危险的事。
var userStatePath = Environment.GetEnvironmentVariable("CLASSSHOUT_USER_STATE")
                    ?? Path.Combine(AppContext.BaseDirectory, "relay-users.json");

// 服务器配置（管理员账号）单独一个文件：它的敏感级别与生命周期都和业务数据不同，
// 而运维最常做的事就是"翻出管理员口令"，让它有一个固定且好找的位置。
var configPath = Environment.GetEnvironmentVariable("CLASSSHOUT_CONFIG")
                 ?? Path.Combine(AppContext.BaseDirectory, "relay-config.json");

// 班级授权表：管理员在控制台上指派的"哪位老师可以用哪个班"
var bindingStatePath = Environment.GetEnvironmentVariable("CLASSSHOUT_BINDING_STATE")
                      ?? Path.Combine(AppContext.BaseDirectory, "relay-bindings.json");

builder.Services.AddSingleton(sp => new ClassroomStore(statePath, sp.GetRequiredService<ILogger<ClassroomStore>>()));
builder.Services.AddSingleton(sp => new UserStore(userStatePath, sp.GetRequiredService<ILogger<UserStore>>()));
builder.Services.AddSingleton(sp => new BindingStore(bindingStatePath, sp.GetRequiredService<ILogger<BindingStore>>()));

// 分享链接也要落盘：管理员上午生成、下午才发给老师，中间重启一次就全失效的话没法用。
var shareStatePath = Environment.GetEnvironmentVariable("CLASSSHOUT_SHARE_STATE")
    ?? Path.Combine(AppContext.BaseDirectory, "relay-shares.json");

builder.Services.AddSingleton(sp => new ShareStore(shareStatePath, sp.GetRequiredService<ILogger<ShareStore>>()));

// 老师同步到服务器上的名单与呼叫模板：WebUI 的「呼叫」要用同一份（见 relay-rosters.json）
var rosterStatePath = Environment.GetEnvironmentVariable("CLASSSHOUT_ROSTER_STATE")
    ?? Path.Combine(AppContext.BaseDirectory, "relay-rosters.json");

builder.Services.AddSingleton(sp => new RosterStore(rosterStatePath, sp.GetRequiredService<ILogger<RosterStore>>()));
builder.Services.AddSingleton<UserSessions>();
builder.Services.AddSingleton<RelaySessions>();
builder.Services.AddSingleton<MessageHub>();

// 服务器上的定时喊话：表 + 音频目录都在服务器自己的数据目录里。
// 这是"教师端不必在后台运行"的落点 —— 任务交给一个一直开着的进程看着。
var scheduleStatePath = Environment.GetEnvironmentVariable("CLASSSHOUT_SCHEDULE_STATE")
    ?? Path.Combine(AppContext.BaseDirectory, "relay-schedule.json");

var scheduleAudioPath = Environment.GetEnvironmentVariable("CLASSSHOUT_SCHEDULE_AUDIO")
    ?? Path.Combine(AppContext.BaseDirectory, "relay-schedule-audio");

builder.Services.AddSingleton(sp => new ScheduledShoutStore(
    sp.GetRequiredService<ILogger<ScheduledShoutStore>>(),
    scheduleStatePath,
    scheduleAudioPath));

builder.Services.AddHostedService<ScheduledShoutService>();

// 日志除了走 stdout（systemd 收进 journald），同时落一份到软件目录下的 logs\ ——
// 压缩包里本来就带着那个目录，让它真的有东西，运维就不必先学一遍 journalctl。
// 过滤器见 FileLoggerProvider：只收有用的那些，不收每条长轮询请求；
// 档位由 CLASSSHOUT_LOG_LEVEL 控制（默认信息级），这里把它同步给 AppLog 的落盘过滤，
// 免得"provider 放行了、AppLog 又挡回去"。
builder.Logging.AddProvider(new FileLoggerProvider());
AppLog.Minimum = FileLoggerProvider.Minimum;

// ======================== 限速 ========================
//
// 注册与登录是匿名开放的，而每个请求都要跑 10 万次 PBKDF2 —— 一次请求几十毫秒
// 纯 CPU。不限速的话，一台机器每秒发几百个请求就能把服务器算力吃干净，
// 而攻击者不需要任何凭据，成本几乎为零。
//
// 刻意不设全局限速器：音频上传是约 10 次/秒的高频路径，长轮询又要挂住 25 秒，
// 一个全局桶会把它们一起误伤。只给这几个昂贵的匿名端点挂策略。
//
// 分两层：
//   · 按来源 IP 的令牌桶（下面这里）—— 挡单机洪水和口令爆破；
//   · 全局并发闸门（见 PBKDF2 闸门那段中间件）—— 挡住"换一批 IP 绕开"的情况，
//     保证同时进行的 PBKDF2 计算有上限。
// 只有前者的话，攻击者用一批代理就能把 CPU 占满；只有后者的话，
// 单个 IP 仍可以刷满队列。两层都要。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // 登录 / 注册账号：人工操作的频率，桶给得不大
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 30,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // 教室端注册：同样是 10 万次 PBKDF2，但这是"开学那天几十台机器同时上线"
    // 的正当突发，桶要明显放大，否则会把正常流量挡在门外。
    options.AddPolicy("classroom-register", context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 120,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

var app = builder.Build();

// ======================== 启动前体检 ========================
//
// 必须在解析任何 Store 之前跑。各 Store 的载入逻辑在失败时会降级成空表并继续启动，
// 那意味着服务看起来是好的，实际却在服务一份空注册表 —— 运维发现不了，
// 而老师那边表现为"所有教室都提示未注册"。
// 权限和损坏这类问题没有任何自动修复的余地，唯一正确的做法是拒绝启动并说清楚原因。
if (!StatePreflight.Report(
        [
            new StateFileSpec("教室注册表", statePath),
            new StateFileSpec("用户表", userStatePath),
            new StateFileSpec("服务器配置", configPath),
            new StateFileSpec("班级授权表", bindingStatePath),
        ],
        app.Logger))
{
    return StatePreflight.ConfigErrorExitCode;
}

var store = app.Services.GetRequiredService<ClassroomStore>();
var users = app.Services.GetRequiredService<UserStore>();
var bindings = app.Services.GetRequiredService<BindingStore>();
var shares = app.Services.GetRequiredService<ShareStore>();
var rosters = app.Services.GetRequiredService<RosterStore>();
var userSessions = app.Services.GetRequiredService<UserSessions>();
var sessions = app.Services.GetRequiredService<RelaySessions>();
var hub = app.Services.GetRequiredService<MessageHub>();

// 服务器上的定时喊话：由后台调度服务到点发出去
var schedule = app.Services.GetRequiredService<ScheduledShoutStore>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Relay");

// 内置管理员的 Id。它不是用户库里的一条记录，所以给一个不会与真实用户冲突的固定值。
const string AdminUserId = "builtin-admin";

// 首次启动会生成随机强口令并打进日志 —— 那是运维唯一一次"直接看到"它的机会。
// 这里仍然可能失败（体检之后权限被改掉、磁盘满了），所以不能让它把栈抛到运维脸上。
ServerConfig config;
try
{
    config = ServerConfig.LoadOrCreate(configPath, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<ServerConfig>());
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    logger.LogError(string.Empty);
    logger.LogError("启动中止：服务器配置不可用。");
    logger.LogError("  {Message}", ex.Message);
    logger.LogError("  路径：{Path}", configPath);
    logger.LogError("  请修正该文件与所在目录的权限后重启服务。");
    logger.LogError(string.Empty);
    return StatePreflight.ConfigErrorExitCode;
}

/// <summary>长轮询单次等待上限。太短会空转费流量，太长则断线发现变慢。</summary>
var pollTimeout = TimeSpan.FromSeconds(25);

/// <summary>
/// 多久之内有过活动就算"这间教室还连着"。
///
/// 取 90 秒是因为教室端的长轮询一轮最多等 25 秒，加上网络与重连的余量，
/// 三次都还没回来才判离线 —— 判早了会把正在上课的教室漏掉，
/// 而"集体喊话少发一间"是那种当场就能被发现的尴尬。
/// </summary>
var OnlineWindow = TimeSpan.FromSeconds(90);

/// <summary>
/// 单个音频分片的体积上限。正常一批是 100 毫秒，约 3200 字节；
/// 给到 16 KB 已是很宽的余量，同时把"持令牌者用大包撑爆内存"这条路口封死。
/// </summary>
const int MaxAudioChunkBytes = 16 * 1024;

logger.LogInformation("注册表：{Path}（已有 {Count} 条记录）", statePath, store.Count);
logger.LogInformation("用户表：{Path}（已有 {Count} 个账号）", userStatePath, users.Count);

// ======================== 健康检查 ========================

app.MapGet(RelayPaths.Health, () => Results.Ok(new
{
    ok = true,
    service = "ClassShout.RelayServer",

    // 带上版本号：升级完服务器之后，"新版本到底部署上去了没有"是第一件要确认的事，
    // 而在此之前只能去翻程序目录里的文件时间。
    version = ReadServerVersion(),
    protocol = 1,
    classrooms = store.Count,
    users = users.Count,
    time = DateTimeOffset.UtcNow,
}));

// ======================== 账号：注册 / 登录 / 查询 / 登出 ========================

app.MapPost(RelayPaths.AuthRegister, (RegisterRequest request) =>
{
    // 管理员账号名要保留。UserStore 只知道自己那张表，看不见配置文件里的管理员，
    // 所以这一条得在这里拦：否则老师注册一个同名的普通账号，
    // 控制台的用户列表里就会出现两个「admin」，而登录接口先试管理员再试用户库 ——
    // 两个同名账号会让人完全分不清自己正在用哪一个。
    if (IsAdminIdentity(request.Username) || IsAdminIdentity(request.Email))
    {
        return Results.Ok(new AuthResponse(false, null, null,
            "该账号名由服务器管理员保留，请换一个。"));
    }

    var (profile, error) = users.Register(request.Username, request.Email, request.Subject, request.DisplayName, request.Password);
    if (profile is null)
    {
        return Results.Ok(new AuthResponse(false, null, null, error));
    }

    var token = userSessions.Issue(profile.Id);
    return Results.Ok(new AuthResponse(true, token, ToDto(profile), null));
})
.RequireRateLimiting("auth");

app.MapPost(RelayPaths.AuthLogin, (LoginRequest request) =>
{
    var account = (request.Account ?? string.Empty).Trim();

    // 先看是不是内置管理员：它不在用户库里，凭据来自配置文件
    if (config.VerifyAdmin(account, request.Password ?? string.Empty))
    {
        var adminToken = userSessions.Issue(AdminUserId, isBuiltInAdmin: true);
        logger.LogInformation("管理员登录成功：{Account}", config.AdminUsername);

        return Results.Ok(new AuthResponse(true, adminToken, ToAdminDto(), null));
    }

    var (profile, error) = users.Login(account, request.Password ?? string.Empty);
    if (profile is null)
    {
        logger.LogWarning("登录失败：{Account}", account);

        // 管理员账号名写错时给出更明确的提示，否则运维会一直以为口令错了
        if (string.Equals(account, config.AdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new AuthResponse(false, null, null, "管理员口令不正确。初始口令见服务器日志与配置文件 relay-config.json。"));
        }

        return Results.Ok(new AuthResponse(false, null, null, error));
    }

    var token = userSessions.Issue(profile.Id);
    logger.LogInformation("用户登录：{Display}", profile.DisplayName);
    return Results.Ok(new AuthResponse(true, token, ToDto(profile), null));
})
.RequireRateLimiting("auth");

app.MapGet(RelayPaths.AuthMe, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (userSessions.IsAdminSession(authToken))
    {
        return Results.Ok(ToAdminDto());
    }

    var profile = ResolveUser(authToken);
    return profile is null
        ? Results.Unauthorized()
        : Results.Ok(ToDto(profile));
});

app.MapPost(RelayPaths.AuthLogout, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    userSessions.Revoke(authToken);
    return Results.Ok(new { ok = true });
});

// ======================== 教师端：自己的任教科目 ========================
//
// 老师自己改自己那份：他在哪个班教什么，本人最清楚，不该事事都找管理员。
// 只能改自己的 —— 令牌决定改谁，请求体里没有"改谁"这个字段，
// 所以即便令牌泄露也改不了别人（而贴到喊话上的来源仍然由服务器算，冒充不了）。

app.MapGet(RelayPaths.AuthSubjects, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);

    return profile is null
        ? Results.Unauthorized()
        : Results.Ok(new TeachingSubjectsDto(
            profile.Subject,
            TeachingSubjects.AsNullable(profile.SubjectByClassroom)));
});

app.MapPost(RelayPaths.AuthSubjects, (
    TeachingSubjectsDto request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    if (!users.SetSubject(profile.Id, request.Subject, request.SubjectByClassroom))
    {
        return Results.Json(new { error = "服务器写不进去，改动未生效。" }, statusCode: 500);
    }

    var updated = users.FindById(profile.Id);

    logger.LogInformation("教师 {Display} 更新了自己的任教科目：默认 {Subject}，按班级 {Count} 条",
        profile.DisplayName, request.Subject ?? "(空)", updated?.SubjectByClassroom?.Count ?? 0);

    return Results.Ok(new TeachingSubjectsDto(
        updated?.Subject,
        TeachingSubjects.AsNullable(updated?.SubjectByClassroom)));
});

// ======================== 教师端：把定时喊话交给服务器 ========================
//
// 为什么值得单独一套端点：本机定时的边界是"应用得开着"，而老师把手机划掉、
// 或者干脆关机过周末，是很正常的事。任务交给服务器之后，到点由服务器自己发。
//
// 归账号所有（用登录令牌），而不是挂某一条教室绑定上：一条定时可以发给好几个班，
// 而绑定令牌是"这一间教室"的。

/// <summary>这位老师能不能往这间教室发。与绑定用的是同一套授权规则。</summary>
bool CanShoutInto(UserProfile profile, string uuid)
    => profile.Id == AdminUserId || bindings.ClassroomsOf(profile.Id)
        .Any(authorized => string.Equals(authorized, uuid, StringComparison.OrdinalIgnoreCase));

app.MapGet(RelayPaths.AuthSchedule, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var items = schedule.OfOwner(profile.Id)
        .OrderBy(item => item.IsPending ? 0 : 1)
        .ThenBy(item => item.SendAt)
        .Select(ToScheduleDto)
        .ToList();

    return Results.Ok(items);
});

app.MapPost(RelayPaths.AuthSchedule, (
    ScheduleShoutRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    return CreateSchedule(profile, request, audio: null, audioSeconds: 0, audioFormat: null);
}).RequireRateLimiting("auth");

/// <summary>
/// 带语音的定时：音频用 multipart 传，字段与上面的 JSON 同名。
///
/// 为什么不塞进 JSON：一段 30 秒的语音是约 1 MB 的 PCM，base64 之后还要再大三成，
/// 而 multipart 本来就是为了传文件存在的。
/// </summary>
app.MapPost(RelayPaths.AuthSchedule + "/voice", async (
    HttpRequest http,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    if (!http.HasFormContentType)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "语音要以 multipart/form-data 提交。"));
    }

    var form = await http.ReadFormAsync();

    var sendAtRaw = form["sendAt"].ToString();
    if (!DateTimeOffset.TryParse(sendAtRaw, out var sendAt))
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "缺少或读不懂发送时间。"));
    }

    var targets = form["targetUuids"].ToString()
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "没有收到语音文件。"));
    }

    using var stream = new MemoryStream();
    await file.CopyToAsync(stream);

    if (!WavCodec.TryDecode(stream.ToArray(), out var format, out var pcm))
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "这段语音读不出来（需要 PCM 的 WAV）。"));
    }

    var seconds = format.DurationMsOf(pcm.Length) / 1000.0;
    if (seconds > ServerScheduledShout.MaxVoiceSeconds)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null,
            $"语音最长 {ServerScheduledShout.MaxVoiceSeconds} 秒，这条是 {seconds:0.#} 秒。"));
    }

    var request = new ScheduleShoutRequest(form["text"].ToString(), sendAt, targets);
    return CreateSchedule(profile, request, pcm, seconds, format);
}).RequireRateLimiting("auth").DisableAntiforgery();

app.MapDelete(string.Format(RelayPaths.AuthScheduleItem, "{id}"), (
    string id,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var error = schedule.Cancel(id, profile.Id, asAdmin: profile.Id == AdminUserId);
    return error is null
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { error });
});

IResult CreateSchedule(
    UserProfile profile,
    ScheduleShoutRequest request,
    byte[]? audio,
    double audioSeconds,
    AudioFormat? audioFormat)
{
    if (request.SendAt <= DateTimeOffset.UtcNow)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "这个时间已经过去了，请选一个将来的时间。"));
    }

    // 只排未来的两小时以内？不限制那么死，但太远的排进来多半是打错了年份
    if (request.SendAt > DateTimeOffset.UtcNow.AddDays(60))
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "最远只能排到 60 天以后。"));
    }

    if (request.TargetUuids.Count == 0)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "请至少选择一个班级。"));
    }

    if (audio is null && string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, "文字定时需要内容。"));
    }

    var unknown = request.TargetUuids
        .Where(uuid => store.Get(uuid) is null)
        .ToList();

    if (unknown.Count > 0)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, $"服务器上没有这些班级：{string.Join("、", unknown)}"));
    }

    var forbidden = request.TargetUuids
        .Where(uuid => !CanShoutInto(profile, uuid))
        .ToList();

    if (forbidden.Count > 0)
    {
        // 与绑定同一条规则：没被授权的班不能喊，定时也不例外 ——
        // 否则"排一条以后发的"就成了绕过授权的一条侧门。
        return Results.BadRequest(new ScheduleShoutResponse(false, null,
            $"这些班级还没有授权给你：{string.Join("、", forbidden.Select(uuid => store.Get(uuid)?.Name ?? uuid))}"));
    }

    var item = new ServerScheduledShout
    {
        OwnerUserId = profile.Id,
        OwnerDisplayName = profile.DisplayName,
        SendAt = request.SendAt.ToUniversalTime(),
        Kind = audio is null ? ScheduledShoutKinds.Text : ScheduledShoutKinds.Voice,
        Text = request.Text,
        AudioSeconds = audioSeconds,
        AudioSampleRate = audioFormat?.SampleRate ?? 0,
        AudioChannels = audioFormat?.Channels ?? 0,
        AudioBitsPerSample = audioFormat?.BitsPerSample ?? 0,
        Display = request.Display,
        FontSize = request.FontSize,
        HoldMs = request.HoldMs,
        Speak = request.Speak,
        TargetUuids = request.TargetUuids.ToList(),
    };

    if (audio is not null)
    {
        item.AudioFile = schedule.SaveAudio(item.Id, audio);
        if (item.AudioFile is null)
        {
            return Results.Json(new ScheduleShoutResponse(false, null, "服务器暂时写不进磁盘，这条定时没有排上。"),
                statusCode: 500);
        }
    }

    var error = schedule.Add(item);
    if (error is not null)
    {
        return Results.BadRequest(new ScheduleShoutResponse(false, null, error));
    }

    logger.LogInformation("新的服务器定时：{Owner} 排了 {Kind}，{Count} 个班，{SendAt:u}",
        profile.DisplayName, item.Kind, item.TargetUuids.Count, item.SendAt);

    return Results.Ok(new ScheduleShoutResponse(true, ToScheduleDto(item)));
}

ScheduledShoutDto ToScheduleDto(ServerScheduledShout item)
    => new(
        item.Id,
        item.Kind,
        item.Text,
        item.AudioSeconds,
        item.SendAt,
        item.Status,
        item.HandledAt,
        item.Error,
        item.TargetUuids,
        item.TargetUuids.Select(uuid => store.Get(uuid)?.Name ?? uuid).ToList(),
        item.Results,
        item.Display,
        item.FontSize,
        item.HoldMs,
        item.Speak);

// ======================== 教室端：注册与查询 ========================

app.MapGet(RelayPaths.Route(RelayPaths.LookupClassroom), (string uuid) =>
{
    var record = store.Get(uuid);
    return Results.Ok(new ClassroomLookupResponse(record is not null, record?.Name));
});

app.MapPost(RelayPaths.RegisterClassroom, (ClassroomRegisterRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Uuid))
    {
        return Results.Ok(new ClassroomRegisterResponse(false, false, string.Empty, string.Empty, null, null,
            "缺少 UUID。"));
    }

    var (record, isNew, plainSecret, error) = store.RegisterOrVerify(request.Uuid, request.Name, request.Secret);
    if (record is null)
    {
        logger.LogWarning("教室注册被拒：{Uuid} —— {Error}", request.Uuid, error);
        return Results.Ok(new ClassroomRegisterResponse(false, false, request.Uuid, request.Name, null, null, error));
    }

    // 换新令牌并作废旧令牌：同一教室重新上线时，旧会话不应还能收消息
    sessions.RevokeClassroomTokens(record.Uuid);
    var token = sessions.IssueClassroomToken(record.Uuid);

    logger.LogInformation(isNew
        ? "新教室注册：{Name}（{Uuid}）"
        : "教室重新上线：{Name}（{Uuid}）", record.Name, record.Uuid);

    // 通知已绑定的教师：教室上线了
    NotifyTeachers(record.Uuid, new RelayEnvelope
    {
        Kind = RelayKinds.ClassroomOnline,
        From = record.Name,
        State = "online",
    });

    return Results.Ok(new ClassroomRegisterResponse(true, isNew, record.Uuid, record.Name, plainSecret, token, null));
})
// 这里只挂并发闸门，不挂按 IP 的令牌桶：教室端注册同样是 10 万次 PBKDF2，
// 但它是"开学那天几十台机器同时上线"这种正当突发，按 IP 限流会把它们挡在门外。
// 并发闸门已经足够 —— CPU 被算力活占满是真正的风险，而它同时最多只放行几个。
.RequireRateLimiting("classroom-register");

// ======================== 教师端：已授权教室 ========================

/// <summary>
/// 列出「管理员在控制台上授权给我使用的教室」。
/// 有了这个列表，老师手机上一点就能绑定，不必抄 UUID、也不必传口令。
/// </summary>
app.MapGet(RelayPaths.TeacherAuthorized, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var result = new List<AuthorizedClassroom>();

    // 内置管理员默认对所有班级可用，不需要逐条授权。
    // 把它做成"隐式全通"而不是启动时写一堆授权记录，是因为后者会在每次新教室
    // 注册时都要求回头补一条 —— 一个永远追不上的循环。
    var uuids = profile.Id == AdminUserId
        ? store.ListForConsole().Select(r => r.Uuid).ToList()
        : bindings.ClassroomsOf(profile.Id);

    foreach (var uuid in uuids)
    {
        var record = store.Get(uuid);
        if (record is null)
        {
            // 教室注册被删掉了，跳过 —— 悬空授权在控制台上会被清理
            continue;
        }

        result.Add(new AuthorizedClassroom(
            record.Uuid,
            record.Name,
            sessions.TeacherCountOf(record.Uuid) > 0,
            record.LastSeenAt));
    }

    return Results.Ok(result);
});

// ======================== 教师：在网页上给自己的班喊话 ========================
//
// 为什么要有这一块：老师不一定装着 App、也不一定带着手机 —— 站在教室那台电脑前
// 打开网页就能喊一句，比"回办公室拿手机"省事得多。控制台原本只对管理员开放，
// 普通老师登进来只会看到一句"该账号不是管理员"。
//
// 权限与 App 完全一致：只喊得了「管理员授权给自己」的班级（内置管理员不受限）。
// 来源也照旧由服务器算（科目按各个班取），客户端填不了。

app.MapGet(RelayPaths.TeacherClassrooms, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var cutoff = DateTimeOffset.UtcNow - OnlineWindow;

    var list = ShoutableClassrooms(profile)
        .Select(record => new TeacherClassroomDto(
            record.Uuid,
            record.Name,
            record.LastSeenAt >= cutoff,
            record.LastSeenAt))
        .OrderBy(item => item.Name, StringComparer.CurrentCulture)
        .ToList();

    return Results.Ok(list);
});

/// <summary>
/// 老师从网页给自己的（一个或多个）班喊一句话。
///
/// 与教师端 App 走的是同一条转发通路（同一个 MessageHub、同一个信封格式），
/// 所以教室端不必为"来自网页"多一种情况。
/// </summary>
app.MapPost(RelayPaths.TeacherShout, (
    TeacherWebShoutRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new TeacherWebShoutResponse(false, 0, [], "喊话内容不能为空。"));
    }

    var allowed = ShoutableClassrooms(profile).ToDictionary(record => record.Uuid, StringComparer.OrdinalIgnoreCase);

    if (request.TargetUuids.Count == 0)
    {
        return Results.BadRequest(new TeacherWebShoutResponse(false, 0, [], "请至少选择一个班级。"));
    }

    var text = request.Text.Trim();
    var sent = 0;
    var results = new List<TeacherShoutResult>();

    foreach (var uuid in request.TargetUuids.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!allowed.TryGetValue(uuid, out var classroom))
        {
            // 不是"这个班不存在"，而是"这个班不归你喊" —— 与绑定同一条规则，
            // 换一条路（网页）进来也一样拦。
            results.Add(new TeacherShoutResult(uuid, uuid, false, "这个班级没有授权给你。"));
            continue;
        }

        hub.Publish(MessageHub.ClassroomKey(classroom.Uuid), new RelayEnvelope
        {
            Kind = RelayKinds.TextShout,

            // 来源由服务器算，科目按**这个班**取 —— 与 App 那条路同一个规则
            From = profile.ShoutNameFor(classroom.Uuid),
            Text = text,
            Rate = request.Rate,
            Volume = request.Volume,
            Interrupt = request.Interrupt,
            Display = request.Display,
            FontSize = request.FontSize,
            HoldMs = request.HoldMs,
            Speak = request.Speak,
        });

        sent++;
        results.Add(new TeacherShoutResult(classroom.Uuid, classroom.Name, true, null));
    }

    logger.LogInformation("老师 {Teacher} 从网页向 {Count} 个班级喊话：{Text}",
        profile.DisplayName, sent, text);

    var message = sent switch
    {
        0 => "一条都没发出去。",
        1 => $"已向「{results.First(r => r.Ok).ClassroomName}」喊话。",
        _ => $"已向 {sent} 个班级喊话。",
    };

    return Results.Ok(new TeacherWebShoutResponse(sent > 0, sent, results, message));
}).RequireRateLimiting("auth");

// ======================== 教师端：名单与呼叫（WebUI 也用这一份） ========================
//
// 老师把名单与呼叫模板同步到服务器，WebUI 才能"呼叫"：那份名单本来只在他手机里，
// 而网页跑在服务器上。拼装用的是 Core 里的 CallComposer —— 与客户端**同一段代码**，
// 所以「要求与客户端一致」不是靠对齐参数，而是结构上就只有一份实现。

app.MapGet(RelayPaths.TeacherRoster, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var record = rosters.Get(profile.Id);

    return Results.Ok(new TeacherRosterSnapshot(
        record?.Rosters ?? [],
        record?.ActiveRosterId,
        record?.Templates ?? [],
        record?.ActiveTemplateId,
        record?.UpdatedAt));
});

app.MapPut(RelayPaths.TeacherRoster, (
    TeacherRosterUpload upload,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var current = rosters.Get(profile.Id) ?? new TeacherRosterRecord { UserId = profile.Id };

    var next = new TeacherRosterRecord
    {
        UserId = profile.Id,
        Rosters = upload.Rosters is not null ? upload.Rosters.ToList() : current.Rosters,
        ActiveRosterId = upload.ActiveRosterId ?? current.ActiveRosterId,
        Templates = upload.Templates is not null ? upload.Templates.ToList() : current.Templates,
        ActiveTemplateId = upload.ActiveTemplateId ?? current.ActiveTemplateId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // 也可以直接贴一份 CSV：用的就是客户端导入名单那套解析器（同一个 RosterCsv），
    // 表头、空行、引号、从 Excel 直接粘贴都照样认。
    if (!string.IsNullOrWhiteSpace(upload.CsvText))
    {
        var parsed = RosterCsv.Parse(upload.CsvText, upload.RosterName ?? "学生名单");
        if (!parsed.Ok)
        {
            return Results.BadRequest(new { ok = false, error = "这份名单一行都没能解析出来。", skipped = parsed.SkippedLines });
        }

        var imported = parsed.Roster!;
        next.Rosters.RemoveAll(r => string.Equals(r.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
        next.Rosters.Add(imported);
        next.ActiveRosterId = imported.Id;
    }

    if (next.Rosters.Count > 0 && next.Rosters.All(r => r.Id != next.ActiveRosterId))
    {
        next.ActiveRosterId = next.Rosters[0].Id;
    }

    if (next.Templates.Count > 0 && next.Templates.All(t => t.Id != next.ActiveTemplateId))
    {
        next.ActiveTemplateId = next.Templates[0].Id;
    }

    if (!rosters.Save(next))
    {
        return Results.Ok(new { ok = false, error = "名单没能存到服务器上（磁盘不可写？），这次同步没有生效。" });
    }

    logger.LogInformation("{User} 同步了名单：{Rosters} 份、模板 {Templates} 个",
        profile.DisplayName, next.Rosters.Count, next.Templates.Count);

    return Results.Ok(new { ok = true, rosters = next.Rosters.Count, templates = next.Templates.Count });
});

/// <summary>
/// WebUI 上拼一次呼叫：与客户端「呼叫」页同一套规则 ——
/// 需要名单、可以多选学生、含"小组成员"组件时按组归并，展示参数也一并带上。
/// </summary>
app.MapPost(RelayPaths.TeacherCall, (
    TeacherCallRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Unauthorized();
    }

    var record = rosters.Get(profile.Id);
    var roster = record is null
        ? null
        : record.Rosters.FirstOrDefault(r => r.Id == record.ActiveRosterId) ?? record.Rosters.FirstOrDefault();

    if (roster is null || roster.Students.Count == 0)
    {
        return Results.BadRequest(new TeacherCallResponse(
            false, 0, [], [],
            "服务器上还没有你的名单：先在教师端「名单」页导入，再点「同步到服务器」。"));
    }

    var template = request.Components is { Count: > 0 }
        ? new CallTemplate { Name = "（本次拼装）", Components = request.Components.ToList() }
        : record!.Templates.FirstOrDefault(t => t.Id == request.TemplateId)
          ?? record.Templates.FirstOrDefault(t => t.Id == record.ActiveTemplateId)
          ?? record.Templates.FirstOrDefault();

    if (template is null || template.Components.Count == 0)
    {
        return Results.BadRequest(new TeacherCallResponse(
            false, 0, [], [], "还没有可用的呼叫模板：先在教师端「呼叫」页拼一个，再点「同步到服务器」。"));
    }

    if (request.StudentIds.Count == 0)
    {
        return Results.BadRequest(new TeacherCallResponse(false, 0, [], [], "一个学生都没选。"));
    }

    if (request.TargetUuids.Count == 0 && !request.PreviewOnly)
    {
        return Results.BadRequest(new TeacherCallResponse(false, 0, [], [], "请至少选择一个班级。"));
    }

    // 学生按**名单里的顺序**取，而不是按前端传过来的顺序：
    // 组内成员、多人一条的句子顺序都跟着名单走，两种客户端拼出来的话才会一样。
    var wanted = request.StudentIds.ToHashSet(StringComparer.Ordinal);
    var students = roster.Students.Where(s => wanted.Contains(s.Id)).ToList();

    if (students.Count == 0)
    {
        return Results.BadRequest(new TeacherCallResponse(
            false, 0, [], [], "选中的学生在服务器上的名单里找不到 —— 可能名单更新过，请刷新页面重选。"));
    }

    // 预览可以先不勾班级：整句话里与班级有关的只有"来源里的科目"一处，
    // 这时按老师的默认科目拼一份给他看就行（真正发送仍然必须勾班级）。
    if (request.PreviewOnly && request.TargetUuids.Count == 0)
    {
        var previewMessages = CallComposer.Compose(template, students, roster, profile.ShoutNameFor(null));

        return Results.Ok(new TeacherCallResponse(
            true,
            0,
            previewMessages,
            [],
            previewMessages.Count == 0
                ? "这套模板拼不出内容。"
                : $"预览（按你的默认科目「{profile.ShoutNameFor(null)}」拼的）：会喊出 {previewMessages.Count} 条。"));
    }

    var allowed = ShoutableClassrooms(profile).ToDictionary(record => record.Uuid, StringComparer.OrdinalIgnoreCase);
    var results = new List<TeacherShoutResult>();
    var allMessages = new List<string>();
    var sent = 0;

    foreach (var uuid in request.TargetUuids.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!allowed.TryGetValue(uuid, out var classroom))
        {
            results.Add(new TeacherShoutResult(uuid, uuid, false, "这个班级没有授权给你。"));
            continue;
        }

        // 每个班各拼一遍：来源里的科目是**按班**取的（"数学张老师"/"物理张老师"），
        // 与 App 那条路同一个规则。
        var messages = CallComposer.Compose(template, students, roster, profile.ShoutNameFor(classroom.Uuid));

        if (messages.Count == 0)
        {
            results.Add(new TeacherShoutResult(classroom.Uuid, classroom.Name, false, "这套模板拼不出内容。"));
            continue;
        }

        foreach (var text in messages)
        {
            if (request.PreviewOnly)
            {
                // 预览：只把拼出来的句子回给界面（网页上先看一眼"到底会喊成什么样"），
                // 一条都不投递 —— 拼装仍然只有 CallComposer 这一份实现。
                continue;
            }

            hub.Publish(MessageHub.ClassroomKey(classroom.Uuid), new RelayEnvelope
            {
                Kind = RelayKinds.TextShout,
                From = profile.ShoutNameFor(classroom.Uuid),
                Text = text,
                Rate = request.Rate,
                Volume = request.Volume,
                Interrupt = request.Interrupt,
                Display = request.Display,
                FontSize = request.FontSize,
                HoldMs = request.HoldMs,
                Speak = request.Speak,
            });

            if (!allMessages.Contains(text, StringComparer.Ordinal))
            {
                allMessages.Add(text);
            }
        }

        sent++;
        results.Add(new TeacherShoutResult(classroom.Uuid, classroom.Name, true, null));
    }

    if (request.PreviewOnly)
    {
        return Results.Ok(new TeacherCallResponse(
            true,
            0,
            allMessages,
            results,
            allMessages.Count == 0 ? "这套模板拼不出内容。" : $"预览：会喊出 {allMessages.Count} 条。"));
    }

    logger.LogInformation("老师 {Teacher} 从网页呼叫：{Students} 位学生、{Messages} 条、{Count} 个班",
        profile.DisplayName, students.Count, allMessages.Count, sent);

    var message = sent switch
    {
        0 => "一条都没发出去。",
        1 => $"已向「{results.First(r => r.Ok).ClassroomName}」呼叫 {allMessages.Count} 条。",
        _ => $"已向 {sent} 个班级各呼叫 {allMessages.Count} 条。",
    };

    return Results.Ok(new TeacherCallResponse(sent > 0, sent, allMessages, results, message));
}).RequireRateLimiting("auth");

/// <summary>这位老师可以喊话的班级（内置管理员 = 全部）。</summary>
List<ClassroomRecord> ShoutableClassrooms(UserProfile profile)
{
    if (profile.Id == AdminUserId)
    {
        return store.ListForConsole().ToList();
    }

    return bindings.ClassroomsOf(profile.Id)
        .Select(uuid => store.Get(uuid))
        .OfType<ClassroomRecord>()
        .ToList();
}

// ======================== 教师端：绑定 ========================

app.MapPost(RelayPaths.BindTeacher, (
    TeacherBindRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var record = store.Get(request.Uuid);
    if (record is null)
    {
        return Results.Ok(new TeacherBindResponse(false, null, null,
            $"服务器上没有 UUID 为 {request.Uuid} 的教室。请核对教师端填写的 UUID 是否与教室端显示的一致。"));
    }

    // 已登录时以账号里的姓名为准：那个名字是注册时确认过的，
    // 比客户端自己传的字符串可信，也保证了教室端弹窗上显示的是"哪位老师"。
    var profile = ResolveUser(authToken);

    // 管理员在控制台上把该班级授权给了这位老师 → 不必再要口令。
    // 口令一旦转发就会扩散，而授权始终收在服务器上，这也是控制台快速绑定的意义。
    // 内置管理员则更进一步：它默认对所有班级可用，连授权这一步都省掉。
    var authorized = profile is not null
                     && (profile.Id == AdminUserId || bindings.IsAuthorized(profile.Id, request.Uuid));

    if (!authorized && !store.Verify(request.Uuid, request.Secret))
    {
        logger.LogWarning("绑定失败（口令错误）：{Uuid} ← {Teacher}", request.Uuid, request.TeacherName);
        var hint = profile is null
            ? "口令不正确。"
            : "口令不正确。若该班级已由管理员在控制台上授权给你，请改用「已授权教室」列表一键绑定。";
        return Results.Ok(new TeacherBindResponse(false, null, null, hint));
    }

        // 喊话来源用"科目 + 姓名"（"数学张老师"）：同一间教室一天里有好几位老师来喊，
    // 只报姓名往往对不上人。没填科目就还是只报姓名。
    // 科目按**这间教室**取：同一位老师在不同班可能教不同科目。
    var teacherName = profile is null ? request.TeacherName : profile.ShoutNameFor(record.Uuid);

    var binding = sessions.BindTeacher(record.Uuid, teacherName, profile?.Id);
    store.Touch(record.Uuid);

    logger.LogInformation("教师端已绑定：{Teacher}{Account} → {Classroom}（{Uuid}）",
        teacherName,
        profile is null ? "（未登录）" : $"（账号 {profile.Username ?? profile.Email}）",
        record.Name, record.Uuid);

    return Results.Ok(new TeacherBindResponse(true, binding.Token, record.Name, null));
});

app.MapDelete(RelayPaths.Route(RelayPaths.TeacherUnbind, "token"), (string token) =>
{
    sessions.UnbindTeacher(token);
    hub.Remove(MessageHub.TeacherKey(token));
    return Results.Ok(new { ok = true });
});

// ======================== 教师端：发喊话 ========================

/// <summary>
/// 这条喊话在教室里显示成谁说的。
///
/// 绑定成功时服务器已经把"科目+姓名"记在会话上了，但那份快照会过时：
/// 管理员刚在控制台给某位老师补上任教科目，教室里却还是只显示姓名 ——
/// 要等老师下次打开教师端重新绑定才会变。既然绑定记录里留着账号 Id，
/// 这里就按账号**当前**的信息现算一遍；只有账号查不到（未登录或已被删）
/// 才退回绑定时的快照。
///
/// 注意科目是**按这条喊话要去的那个班**取的：一位老师在不同班教不同科目，
/// 用账号上那份默认科目会给其中一个班贴错科目。
/// </summary>
string FromOf(TeacherBinding binding)
    => binding.UserId is { } userId && users.FindById(userId) is { } profile
        ? profile.ShoutNameFor(binding.ClassroomUuid)
        : binding.TeacherName;

app.MapPost(RelayPaths.Route(RelayPaths.TeacherText, "token"), (string token, TextShoutRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.TextShout,
        From = FromOf(binding),
        Text = request.Text,
        Rate = request.Rate,
        Volume = request.Volume,
        Interrupt = request.Interrupt,

        // 展示参数原样转发。服务器不解释它们 ——
        // 照它们把内容画出来的是教室端，理解一遍只会多一处会过时的地方。
        Display = request.Display,
        FontSize = request.FontSize,
        HoldMs = request.HoldMs,
        Speak = request.Speak,
    });

    logger.LogInformation("{Teacher} → {Uuid}：文字喊话 {Length} 字",
        FromOf(binding), binding.ClassroomUuid, request.Text?.Length ?? 0);

    return Results.Ok(new { ok = true });
});

app.MapPost(RelayPaths.Route(RelayPaths.TeacherAudioStart, "token"), (string token, AudioStartRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.AudioStart,
        From = FromOf(binding),
        SampleRate = request.SampleRate,
        Channels = request.Channels,
        BitsPerSample = request.BitsPerSample,
    });

    return Results.Ok(new { ok = true });
});

// 音频走裸二进制请求体而不是 JSON + Base64：
// 上传方向是高频路径（约 10 次/秒），能省掉 33% 的编码开销就省掉。
app.MapPost(RelayPaths.Route(RelayPaths.TeacherAudio, "token"), async (string token, HttpRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    // 分片体积上限。
    //
    // 正常的分片是 100 毫秒一批：16 kHz × 2 字节 × 0.1 秒 = 3200 字节。
    // 这里给到 16 KB（约 500 毫秒音频）已经是很宽的余量。
    //
    // 不设上限的后果不是"多收点数据"这么轻：pcm 会先整段进内存，
    // 再被 Convert.ToBase64String 放大 33%，然后作为一条历史消息留在
    // 广播历史里。持令牌者只要持续发大包，就能把服务器撑爆。
    if (request.ContentLength is > MaxAudioChunkBytes)
    {
        return Results.Json(
            new { error = $"音频分片超过上限 {MaxAudioChunkBytes} 字节。" },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    using var buffer = new MemoryStream();

    // Content-Length 可能缺失（chunked）也可能是假的，所以读取本身也要设闸，
    // 不能只信上面那个头部。
    var chunk = new byte[8 * 1024];
    int read;
    while ((read = await request.Body.ReadAsync(chunk)) > 0)
    {
        if (buffer.Length + read > MaxAudioChunkBytes)
        {
            return Results.Json(
                new { error = $"音频分片超过上限 {MaxAudioChunkBytes} 字节。" },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        buffer.Write(chunk, 0, read);
    }

    var pcm = buffer.ToArray();

    if (pcm.Length == 0)
    {
        return Results.Ok(new { ok = true, bytes = 0 });
    }

    binding.AudioBytes += pcm.Length;

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.Audio,
        From = FromOf(binding),
        AudioBase64 = Convert.ToBase64String(pcm),
    });

    return Results.Ok(new { ok = true, bytes = pcm.Length });
});

app.MapPost(RelayPaths.Route(RelayPaths.TeacherAudioEnd, "token"), (string token) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.AudioEnd,
        From = FromOf(binding),
    });

    logger.LogInformation("{Teacher} → {Uuid}：语音结束，累计 {Bytes:N0} 字节",
        FromOf(binding), binding.ClassroomUuid, binding.AudioBytes);

    return Results.Ok(new { ok = true });
});

// ======================== 教师端：图片喊话 ========================
//
// 和音频一样三段式。这里刻意不复用音频那条路：两者的分片大小差一个数量级
// （音频 3 KB、图片 10 KB），而服务器要按各自的形状设上限。

app.MapPost(RelayPaths.Route(RelayPaths.TeacherImageStart, "token"), (string token, ImageStartRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    // 声明里的字节数是**教室端**照着分配缓冲的依据，所以先在这里卡一道上限：
    // 不卡的话，持令牌者报一个 4 GB 的 TotalBytes 就能让每间教室都去申请一块巨型缓冲。
    if (request.TotalBytes is <= 0 or > ShoutProtocol.MaxImageBytes)
    {
        return Results.Json(
            new { error = $"图片大小不合法（{request.TotalBytes} 字节，上限 {ShoutProtocol.MaxImageBytes}）。" },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.ImageStart,
        From = FromOf(binding),
        ImageId = request.Id,
        ImageTotalBytes = request.TotalBytes,
        ImageContentType = request.ContentType,
        ImageWidth = request.Width,
        ImageHeight = request.Height,
        Text = request.Text,
        Display = request.Display,
        FontSize = request.FontSize,
        HoldMs = request.HoldMs,
        Speak = request.Speak,
    });

    logger.LogInformation("{Teacher} → {Uuid}：图片开始，{Bytes:N0} 字节",
        FromOf(binding), binding.ClassroomUuid, request.TotalBytes);

    return Results.Ok(new { ok = true });
});

app.MapPost(RelayPaths.Route(RelayPaths.TeacherImageChunk, "token"), async (string token, HttpRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    // 和音频分片同样的闸：base64 后的请求体不许超过 16 KB。
    // 上限不是"少收点数据"的问题 —— 分片会整段进内存、再进广播历史，
    // 不设限就等于让持令牌者决定服务器用多少内存。
    if (request.ContentLength is > MaxAudioChunkBytes)
    {
        return Results.Json(
            new { error = $"图片分片超过上限 {MaxAudioChunkBytes} 字节。" },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    using var buffer = new MemoryStream();
    var chunk = new byte[8 * 1024];
    int read;
    while ((read = await request.Body.ReadAsync(chunk)) > 0)
    {
        if (buffer.Length + read > MaxAudioChunkBytes)
        {
            return Results.Json(
                new { error = $"图片分片超过上限 {MaxAudioChunkBytes} 字节。" },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        buffer.Write(chunk, 0, read);
    }

    var body = System.Text.Json.JsonSerializer.Deserialize<ImageChunkRequest>(
        buffer.ToArray(), System.Text.Json.JsonSerializerOptions.Web);

    if (body is null || string.IsNullOrWhiteSpace(body.DataBase64))
    {
        return Results.BadRequest(new { error = "图片分片是空的。" });
    }

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.Image,
        From = FromOf(binding),
        ImageId = body.Id,
        ImageBase64 = body.DataBase64,
    });

    return Results.Ok(new { ok = true, bytes = body.DataBase64.Length });
});

app.MapPost(RelayPaths.Route(RelayPaths.TeacherImageEnd, "token"), (string token, ImageChunkRequest request) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.ImageEnd,
        From = FromOf(binding),
        ImageId = request.Id,
    });

    return Results.Ok(new { ok = true });
});

app.MapPost(RelayPaths.Route(RelayPaths.TeacherStop, "token"), (string token) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    hub.Publish(MessageHub.ClassroomKey(binding.ClassroomUuid), new RelayEnvelope
    {
        Kind = RelayKinds.Stop,
        From = FromOf(binding),
        Reason = "教师端请求停止",
    });

    return Results.Ok(new { ok = true });
});

// ======================== 教师端：长轮询收状态 ========================

app.MapGet(RelayPaths.Route(RelayPaths.TeacherEvents, "token"), async (
    string token,
    long since,
    CancellationToken cancellationToken) =>
{
    if (!sessions.TryGetTeacher(token, out var binding))
    {
        return Results.Unauthorized();
    }

    TouchTeacher(binding);

    var result = await hub.Queue(MessageHub.TeacherKey(token)).WaitAsync(since, pollTimeout, cancellationToken);
    return Results.Ok(new EventBatch(result.Events, result.Next, result.TimedOut));
});

// ======================== 教室端：长轮询收喊话（Webhook 投递通道） ========================

app.MapGet(RelayPaths.Route(RelayPaths.ClassroomEvents), async (
    string uuid,
    long since,
    [FromHeader(Name = RelayPaths.TokenHeader)] string? token,
    CancellationToken cancellationToken) =>
{
    if (!sessions.ValidateClassroomToken(uuid, token))
    {
        return Results.Unauthorized();
    }

    store.Touch(uuid);

    var result = await hub.Queue(MessageHub.ClassroomKey(uuid)).WaitAsync(since, pollTimeout, cancellationToken);
    return Results.Ok(new EventBatch(result.Events, result.Next, result.TimedOut));
});

// ======================== 教室端：上报状态 ========================

app.MapPost(RelayPaths.Route(RelayPaths.ClassroomStatus), (
    string uuid,
    ClassroomStatusRequest request,
    [FromHeader(Name = RelayPaths.TokenHeader)] string? token) =>
{
    if (!sessions.ValidateClassroomToken(uuid, token))
    {
        return Results.Unauthorized();
    }

    store.Touch(uuid);

    NotifyTeachers(uuid, new RelayEnvelope
    {
        Kind = RelayKinds.Status,
        Muted = request.Muted,
        Volume = request.Volume,
        State = request.State,
    });

    return Results.Ok(new { ok = true });
});

// ======================== 安全响应头 ========================

// 放在所有端点之前，覆盖包括 API 在内的每一个响应。
//
// 这里最要紧的是 CSP：控制台页面会渲染由匿名接口写入的教室名和老师显示名，
// 转义只要有一处写坏就是存储型 XSS，而管理员的令牌就在 sessionStorage 里。
// script-src 'self' 让"转义写坏"不再是"代码被执行" —— 页面里已经没有任何
// 内联脚本和内联事件处理器，注入的 <script> 或 onclick 都会被浏览器拒绝。
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] = WebUi.ContentSecurityPolicy;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";

    await next();
});

// ======================== PBKDF2 并发闸门 ========================

// 按来源 IP 的令牌桶挡不住"换一批代理再来"的情况，而 CPU 才是真正的瓶颈。
// 这里给会跑 PBKDF2 的三个端点统一套一个全局信号量：同时进行的慢哈希有上限，
// 超出的排队，排不下就直接拒绝。
//
// 宁可拒绝一部分注册请求，也不能让线程池被算力活占满 ——
// 那会把转发音频、长轮询这些正常请求一起拖死，故障面反而更大。
var pbkdf2Gate = new SemaphoreSlim(Math.Max(4, Environment.ProcessorCount * 2));
var pbkdf2Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    RelayPaths.AuthLogin,
    RelayPaths.AuthRegister,
    RelayPaths.RegisterClassroom,
};

app.Use(async (context, next) =>
{
    if (!pbkdf2Paths.Contains(context.Request.Path.Value ?? string.Empty))
    {
        await next();
        return;
    }

    // 最多排 3 秒。客户端是等着结果的，排太久不如直接让它稍后重试。
    if (!await pbkdf2Gate.WaitAsync(TimeSpan.FromSeconds(3)))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    try
    {
        await next();
    }
    finally
    {
        pbkdf2Gate.Release();
    }
});

app.UseRateLimiter();

// 界面用嵌入资源而不是 wwwroot 静态目录：
// 发布产物就是一个单文件服务器，不必关心工作目录与静态文件中间件的配置。
// ======================== 管理控制台 WebUI ========================

app.MapGet("/", () => Results.Content(WebUi.Page, "text/html; charset=utf-8"));

// 样式与脚本拆成独立文件，是上面那条 CSP 能成立的前提。
app.MapGet("/app.css", () => Results.Content(WebUi.Css, "text/css; charset=utf-8"));
app.MapGet("/app.js", () => Results.Content(WebUi.Js, "text/javascript; charset=utf-8"));

app.MapGet("/favicon.ico", () => Results.StatusCode(204));

/// <summary>概览统计。</summary>
// 版本号从程序集里读，值来自 Directory.Build.props 的 <Version>。
//
// 这里原本硬编码着 "1.0.0"：发布 1.1.0 之后控制台仍然显示 1.0.0，而运维正是
// 靠这一行判断"新版本到底部署上去了没有"。让它跟着构建走，就不会再漂移。
static string ReadServerVersion()
{
    var informational = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // 开启源码链接时 InformationalVersion 形如 "1.1.0+abc1234"，控制台只显示语义版本部分
    if (!string.IsNullOrWhiteSpace(informational))
    {
        return informational.Split('+')[0];
    }

    return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
}

var serverVersion = ReadServerVersion();

app.MapGet("/api/console/overview", ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new ConsoleOverview(
        store.Count,
        users.Count,
        sessions.TeacherCountTotal(),
        bindings.Count,
        config.AdminPasswordIsInitial,
        DateTimeOffset.UtcNow,
        serverVersion));
});

/// <summary>教室列表。只返回 UUID 与名称等公开信息，绝不返回口令或其派生值。</summary>
app.MapGet("/api/console/classrooms", ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(store.ListForConsole().Select(record => new ConsoleClassroom(
        record.Uuid,
        record.Name,
        record.RegisteredAt,
        record.LastSeenAt,
        sessions.OnlineTeacherCountOf(record.Uuid, DateTimeOffset.UtcNow - OnlineWindow),
        sessions.TeacherCountOf(record.Uuid))));
});

/// <summary>老师忘记口令时删掉注册记录，教室端下次启动即可重新注册。</summary>
app.MapDelete("/api/console/classrooms/{uuid}", (
    string uuid,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    var removed = store.Remove(uuid);
    sessions.RevokeClassroomTokens(uuid);
    hub.Remove(MessageHub.ClassroomKey(uuid));

    // 教室没了，指向它的授权就是悬空记录，一并清掉
    var revoked = bindings.RevokeClassroom(uuid);

    logger.LogWarning("管理员删除了教室注册：{Uuid}（同时清理 {Count} 条授权）", uuid, revoked);
    return Results.Ok(new { ok = removed });
});

// 管理员排在第一位。它是唯一能登录控制台的账号，而控制台又正好是"改管理员口令"
// 的地方 —— 之前它不出现在这个列表里，界面上那条"到「用户」页里尽快修改"的提示
// 就成了让人找不到入口的空话。
app.MapGet("/api/console/users", ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(users.List().Select(ToDto).Prepend(ToAdminDto()));
});

/// <summary>手动创建一个账号。</summary>
app.MapPost("/api/console/users", (
    CreateUserRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    // 管理员建号时同样要挡住与内置管理员重名 —— 否则控制台列表里会出现两个 admin
    if (IsAdminIdentity(request.Username) || IsAdminIdentity(request.Email))
    {
        return Results.BadRequest(new { error = "该账号名由服务器内置管理员保留，请换一个。" });
    }

    var (profile, error) = users.Register(request.Username, request.Email, request.Subject, request.DisplayName ?? string.Empty, request.Password);
    if (profile is null)
    {
        return Results.BadRequest(new { error = error ?? "创建失败。" });
    }

    logger.LogInformation("管理员创建账号：{Display}（{Username}）", profile.DisplayName, profile.Username ?? "-");
    return Results.Ok(new { ok = true, message = $"已创建账号「{profile.DisplayName}」。", user = ToDto(profile) });
}).RequireRateLimiting("auth");

/// <summary>
/// 按 CSV 批量创建账号，用于开学时一次录入一批老师。
///
/// 格式：每行 用户名,邮箱,姓名,口令[,任教科目]。用户名与邮箱至少填一个（另一个留空即可），
/// 科目可留空。
/// 允许空行，允许以 # 开头的注释行，允许一行带表头 —— 管理员多半是从 Excel 里
/// 直接复制出来的，格式太严会逼着他手工清理。
///
/// 逐行独立处理：某一行不合格只跳过那一行，不整批失败。
/// 一次导入几十条时，"第 7 行邮箱格式不对"远比"整批失败"有用。
/// </summary>
app.MapPost("/api/console/users/import", (
    ImportUsersRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    var details = new List<string>();
    var created = 0;
    var failed = 0;
    var lineNumber = 0;

    foreach (var rawLine in (request.Csv ?? string.Empty).Split('\n'))
    {
        lineNumber++;
        var line = rawLine.Trim().TrimEnd('\r');

        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        // 表头行直接跳过：管理员从 Excel 复制时几乎一定带着它
        if (lineNumber == 1 && (line.Contains("用户名") || line.Contains("username", StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        // 认引号：Excel 在字段里带逗号时会自动加引号，硬切会让后面几列全部错位。
        // 与学生名单的导入共用同一份实现（见 CsvLine）。
        var fields = CsvLine.Split(line);
        if (fields.Length < 4)
        {
            failed++;
            details.Add($"第 {lineNumber} 行：至少需要 4 列（用户名,邮箱,姓名,口令），实际 {fields.Length} 列。");
            continue;
        }

        // 第 5 列是可选科目：各校名单里未必有这一列，没有就不填
        var (username, email, displayName, password) =
            (Empty(fields[0]), Empty(fields[1]), Empty(fields[2]), fields[3]);

        var subject = fields.Length >= 5 ? Empty(fields[4]) : null;

        if (username is null && email is null)
        {
            failed++;
            details.Add($"第 {lineNumber} 行：用户名与邮箱至少要填一个。");
            continue;
        }

        if (IsAdminIdentity(username) || IsAdminIdentity(email))
        {
            failed++;
            details.Add($"第 {lineNumber} 行：账号名与内置管理员冲突。");
            continue;
        }

        var (profile, error) = users.Register(username, email, subject, displayName ?? string.Empty, password);
        if (profile is null)
        {
            failed++;
            details.Add($"第 {lineNumber} 行：{error}");
            continue;
        }

        created++;
        details.Add($"第 {lineNumber} 行：已创建「{profile.DisplayName}」。");
    }

    logger.LogInformation("管理员批量导入账号：成功 {Created} 条，失败 {Failed} 条", created, failed);
    return Results.Ok(new ImportUsersResponse(created, failed, details));

    static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}).RequireRateLimiting("auth");

/// <summary>
/// 管理员在控制台上直接对某个班级喊一句话。
///
/// 为什么要有它：老师在教室里调试、或者管理员临时通知一句（"请各班打开广播"），
/// 手里未必有手机端。控制台本来就能看到所有班级，顺手能喊一句最省事。
///
/// 走的是和教师端完全相同的那条转发通路（同一个 MessageHub、同一个信封格式），
/// 不另开一条 —— 否则教室端就得分两种情况处理，久而久之必然分叉。
/// 来源写"控制台"，让教室端的弹窗如实显示这句话是谁说的。
/// </summary>
app.MapPost("/api/console/shout", (
    ConsoleShoutRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "喊话内容不能为空。" });
    }

    var classroom = store.Get(request.Uuid);
    if (classroom is null)
    {
        return Results.BadRequest(new { error = "教室不存在，请先让教室端连接一次服务器完成注册。" });
    }

    hub.Publish(MessageHub.ClassroomKey(classroom.Uuid), new RelayEnvelope
    {
        Kind = RelayKinds.TextShout,
        From = $"{config.AdminUsername}（控制台）",
        Text = request.Text.Trim(),
        Rate = request.Rate,
        Volume = request.Volume,
        Interrupt = request.Interrupt,
    });

    // 刻意不额外通知教师端：教师端的信封处理只认状态与上下线，
    // 收到一条 TextShout 也不会做任何事，多发一份只是噪音。
    logger.LogInformation("管理员对教室 {Name}（{Uuid}）喊话：{Text}", classroom.Name, classroom.Uuid, request.Text.Trim());
    return Results.Ok(new { ok = true, message = $"已向「{classroom.Name}」喊话。" });
}).RequireRateLimiting("auth");

/// <summary>
/// 集体喊话：一次发给所有**在线**教室。
///
/// 判"在线"用的是教室记录上的最近活动时间，而不是另立一张在线表：
/// 教室端一直在长轮询（一轮约半分钟），而每次轮询服务器都会 Touch 一下记录 ——
/// 所以"最近一分钟内有过动静"就是"还连着"的准确代理，不需要额外的心跳协议。
///
/// 离线教室刻意**不发**：消息队列有历史上限（约几分钟的内容），
/// 发给一间已经关机的教室，它下次开机时可能收到一条几小时前的"临时通知" ——
/// 那种迟到比没收到更糟。
/// </summary>
app.MapPost(RelayPaths.ConsoleBroadcast, (
    BroadcastShoutRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "喊话内容不能为空。" });
    }

    var text = request.Text.Trim();
    var cutoff = DateTimeOffset.UtcNow - OnlineWindow;
    var online = store.ListForConsole().Where(record => record.LastSeenAt >= cutoff).ToList();

    if (online.Count == 0)
    {
        return Results.Ok(new { ok = true, count = 0, message = "当前没有在线教室，没有发送。" });
    }

    var from = $"{config.AdminUsername}（控制台）";

    foreach (var classroom in online)
    {
        hub.Publish(MessageHub.ClassroomKey(classroom.Uuid), new RelayEnvelope
        {
            Kind = RelayKinds.TextShout,
            From = from,
            Text = text,
            Rate = request.Rate,
            Volume = request.Volume,
            Interrupt = request.Interrupt,
            Display = request.Display,
            FontSize = request.FontSize,
            HoldMs = request.HoldMs,
            Speak = request.Speak,
        });
    }

    logger.LogInformation("管理员集体喊话：{Count} 间在线教室，内容 {Text}", online.Count, text);

    return Results.Ok(new
    {
        ok = true,
        count = online.Count,
        names = online.Select(record => record.Name).ToList(),
        message = $"已向 {online.Count} 间在线教室喊话。",
    });
}).RequireRateLimiting("auth");

// ======================== 分享链接一键绑定 ========================
//
// 排课之后要把"这位老师教这几个班"告诉老师，原来只有两条路：管理员在控制台逐个授权，
// 或者把 UUID 与口令抄给他。前者要点很多下，后者要让口令在聊天软件里流传。
// 分享链接是第三条路：管理员勾几个班、生成一条链接发给老师，老师点开就绑好了。
//
// 链接是**凭据**，所以兑现时必须先登录 —— 谁绑的始终有据可查，而不是匿名扩散；
// 同时它有过期时间、也能在控制台上撤销。

app.MapPost(RelayPaths.ConsoleShare, (
    ShareClassroomRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken,
    HttpContext context) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    var uuids = (request.Uuids ?? []).Where(uuid => !string.IsNullOrWhiteSpace(uuid)).Distinct().ToList();
    if (uuids.Count == 0)
    {
        return Results.BadRequest(new { error = "请至少选一个班级。" });
    }

    var known = uuids.Where(uuid => store.Get(uuid) is not null).ToList();
    if (known.Count == 0)
    {
        return Results.BadRequest(new { error = "这些班级都不存在，请先让教室端连接一次服务器完成注册。" });
    }

    var lifetime = TimeSpan.FromHours(Math.Clamp(request.ValidHours, 1, 24 * 30));
    var link = shares.Create(known, config.AdminUsername, lifetime);

    // 地址由请求推出来：服务器自己不一定知道对外是哪个域名（可能在反向代理后面）。
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

    logger.LogInformation("管理员生成了分享链接：{Count} 个班级，{Hours} 小时有效",
        known.Count, lifetime.TotalHours);

    return Results.Ok(new
    {
        ok = true,
        token = link.Token,
        url = $"{baseUrl}{string.Format(RelayPaths.SharePage, link.Token)}",
        appUrl = $"classshout://claim?token={link.Token}&server={Uri.EscapeDataString(baseUrl)}",
        expiresAt = link.ExpiresAt,
        count = known.Count,
        message = $"已生成分享链接（{known.Count} 个班级，{lifetime.TotalHours:0} 小时内有效）。",
    });
}).RequireRateLimiting("auth");

/// <summary>分享链接的公开信息。不需要登录 —— 老师点开链接时还没登录。</summary>
app.MapGet(RelayPaths.Route(RelayPaths.ShareInfo, "token"), (string token, HttpContext context) =>
{
    var link = shares.Get(token);
    if (link is null)
    {
        return Results.Ok(new ShareInfoResponse(false, Error: "这条分享链接无效或已过期。请让管理员重新生成一条。"));
    }

    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    var cutoff = DateTimeOffset.UtcNow - OnlineWindow;

    var classrooms = link.ClassroomUuids
        .Select(uuid => store.Get(uuid))
        .OfType<ClassroomRecord>()
        .Select(record => new SharedClassroom(record.Uuid, record.Name, record.LastSeenAt >= cutoff))
        .ToList();

    return Results.Ok(new ShareInfoResponse(
        true,
        link.Token,
        baseUrl,
        link.ExpiresAt,
        link.CreatedBy,
        classrooms));
});

/// <summary>兑现：把这批班级授权给当前登录的账号。之后老师在"已授权教室"里点一下就绑上了。</summary>
app.MapPost(RelayPaths.Route(RelayPaths.ShareClaim, "token"), (
    string token,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    var link = shares.Get(token);
    if (link is null)
    {
        return Results.Ok(new ShareClaimResponse(false, Error: "这条分享链接无效或已过期。"));
    }

    var profile = ResolveUser(authToken);
    if (profile is null)
    {
        return Results.Ok(new ShareClaimResponse(false, Error: "请先在教师端登录账号，再打开这条链接。"));
    }

    var cutoff = DateTimeOffset.UtcNow - OnlineWindow;
    var granted = 0;
    var classrooms = new List<SharedClassroom>();

    foreach (var uuid in link.ClassroomUuids)
    {
        var record = store.Get(uuid);
        if (record is null)
        {
            // 教室被管理员删掉了：跳过它，但不让整条链接失败 ——
            // 链接里通常有好几个班，为一个已经注销的班把整件事搞砸没有道理。
            continue;
        }

        var (ok, alreadyExists) = bindings.Grant(profile.Id, uuid, $"{link.CreatedBy}（分享链接）");
        if (ok && !alreadyExists)
        {
            granted++;
        }

        classrooms.Add(new SharedClassroom(record.Uuid, record.Name, record.LastSeenAt >= cutoff));
    }

    shares.MarkClaimed(link, profile.Id);

    logger.LogInformation("分享链接被兑现：{User} 绑定 {Count} 个班级（新增 {Granted}）",
        profile.DisplayName, classrooms.Count, granted);

    return Results.Ok(new ShareClaimResponse(
        true,
        granted,
        classrooms,
        $"已把 {classrooms.Count} 个班级加到你的账号下。"));
}).RequireRateLimiting("auth");

/// <summary>
/// 分享链接的落地页。
///
/// 直接用浏览器打开时看到的那一页：说明这条链接会给到什么、并给一个"用教师端打开"的按钮。
/// 写成服务端拼的静态页而不是塞进控制台的 SPA：点链接的老师没有登录态，
/// 也不该被拉到管理界面上去。
/// </summary>
app.MapGet(RelayPaths.Route(RelayPaths.SharePage, "token"), (string token, HttpContext context) =>
{
    var link = shares.Get(token);
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

    var body = link is null
        ? "<p class=\"bad\">这条分享链接无效或已过期。请让管理员重新生成一条。</p>"
        : BuildSharePageBody(link, baseUrl, store.ListForConsole(), OnlineWindow);

    var html = $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ClassShout 班级分享</title>
          <style>
            body { font-family: system-ui, -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif;
                   margin: 0; padding: 32px 20px; background: #fbf8fd; color: #1b1b1f; }
            .card { max-width: 560px; margin: 0 auto; background: #fff; border-radius: 16px;
                    padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,.08); }
            h1 { font-size: 20px; margin: 0 0 12px; }
            ul { padding-left: 20px; }
            .bad { color: #b3261e; }
            .muted { color: #49454f; font-size: 14px; }
            .btn { display: block; text-align: center; margin: 20px 0 8px; padding: 14px;
                   background: #6750a4; color: #fff; border-radius: 9999px;
                   text-decoration: none; font-weight: 600; }
            code { background: #f0edf1; padding: 2px 6px; border-radius: 6px; font-size: 13px; }
          </style>
        </head>
        <body>
          <div class="card">
            <h1>ClassShout 班级分享</h1>
            {{body}}
          </div>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
});

/// <summary>拼落地页正文。</summary>
static string BuildSharePageBody(
    ShareLinkRecord link,
    string baseUrl,
    IReadOnlyList<ClassroomRecord> registered,
    TimeSpan onlineWindow)
{
    var cutoff = DateTimeOffset.UtcNow - onlineWindow;

    var items = link.ClassroomUuids
        .Select(uuid => registered.FirstOrDefault(record =>
            string.Equals(record.Uuid, uuid, StringComparison.OrdinalIgnoreCase)))
        .OfType<ClassroomRecord>()
        .Select(record =>
            $"<li>{System.Net.WebUtility.HtmlEncode(record.Name)}"
            + (record.LastSeenAt >= cutoff ? " <span class=\"muted\">（在线）</span>" : string.Empty)
            + "</li>")
        .ToList();

    var appUrl = $"classshout://claim?token={link.Token}&server={Uri.EscapeDataString(baseUrl)}";

    return $"""
        <p class="muted">由 <strong>{System.Net.WebUtility.HtmlEncode(link.CreatedBy)}</strong> 分享，
           有效期至 {link.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}。</p>
        <p>打开这条链接后，下列班级会加到你账号的「管理员分配的班级」里，点一下就绑定：</p>
        <ul>{(items.Count == 0 ? "<li class=\"muted\">（这些班级已被移除）</li>" : string.Concat(items))}</ul>
        <a class="btn" href="{appUrl}">用 ClassShout 教师端打开</a>
        <p class="muted">没装教师端？在教师端里打开「设备」页，把这条链接粘进「用分享链接绑定」也可以。
           链接需要在教师端里登录账号后使用 —— 这样"谁绑了这几个班"才有据可查。</p>
        <p class="muted">令牌：<code>{link.Token}</code></p>
        """;
}

/// <summary>停用 / 启用账号。</summary>
app.MapPost("/api/console/users/{id}/disabled", (    string id,
    ConsoleFlagRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    // 内置管理员不是用户库里的一条记录，停用它等于把自己锁在控制台外面
    if (id == AdminUserId)
    {
        return Results.BadRequest(new { error = "内置管理员账号不能被停用。" });
    }

    var ok = users.SetDisabled(id, request.Value);
    if (ok && request.Value)
    {
        // 停用后立刻撤销该账号的所有登录令牌，否则已登录的客户端还能继续用
        userSessions.RevokeAllOf(id);

        // 同时收回班级授权：账号都停用了，它不该还留着访问权限
        bindings.RevokeUser(id);
    }

    return Results.Ok(new { ok });
});

/// <summary>
/// 改一位老师的任教科目。
///
/// 注册页上科目是选填的，老师随手留空之后就没有地方再补了 —— 教师端没有"改资料"
/// 这一页，控制台原来也没有这个动作。于是教室里的喊话来源永远只有姓名，
/// 而"是哪位老师说的"恰恰是同一间教室一天里好几位老师来喊时最需要的信息。
/// 管理员在开学初对着名单补一次，比让老师重新注册一遍合理得多。
/// </summary>
app.MapPost("/api/console/users/{id}/subject", (
    string id,
    ConsoleSubjectRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    // 内置管理员不是老师，也没有"任教科目"可言
    if (id == AdminUserId)
    {
        return Results.BadRequest(new { error = "内置管理员不是老师账号，没有任教科目。" });
    }

    if (!users.SetSubject(id, request.Value, request.ByClassroom))
    {
        return Results.NotFound(new { error = "找不到这个账号，或服务器写不进去。" });
    }

    logger.LogInformation("管理员修改账号 {Id} 的任教科目：{Subject}", id, request.Value ?? "(清空)");
    return Results.Ok(new { ok = true });
});

/// <summary>班级授权列表。</summary>
app.MapGet(RelayPaths.ConsoleBindings, ([FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    var result = bindings.All().Select(binding =>
    {
        var user = users.FindById(binding.UserId);
        var classroom = store.Get(binding.ClassroomUuid);

        return new ConsoleBinding(
            binding.UserId,
            user?.DisplayName ?? "（已删除的账号）",
            binding.ClassroomUuid,
            classroom?.Name ?? "（已删除的教室）",
            binding.GrantedBy,
            binding.GrantedAt);
    });

    return Results.Ok(result);
});

/// <summary>把某个班级授权给某位老师。授权后该老师无需口令即可绑定。</summary>
app.MapPost(RelayPaths.ConsoleBindings, (
    GrantBindingRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    // 管理员本来就对所有班级可用，给它授权既无意义，写进去还会在列表里
    // 显得像是真的多了一条权限记录。直接说明白，而不是含糊地回一句"账号不存在"。
    if (request.UserId == AdminUserId)
    {
        return Results.Ok(new { ok = true, message = $"「{config.AdminUsername}」是内置管理员，默认就可以使用所有班级，不需要单独授权。" });
    }

    var user = users.FindById(request.UserId);
    if (user is null)
    {
        return Results.BadRequest(new { error = "账号不存在。" });
    }

    var classroom = store.Get(request.Uuid);
    if (classroom is null)
    {
        return Results.BadRequest(new { error = "教室不存在，请先让教室端连接一次服务器完成注册。" });
    }

    var (granted, alreadyExists) = bindings.Grant(user.Id, classroom.Uuid, config.AdminUsername);

    if (granted)
    {
        return Results.Ok(new { ok = true, message = $"已把「{classroom.Name}」授权给「{user.DisplayName}」。" });
    }

    if (alreadyExists)
    {
        return Results.Ok(new { ok = true, message = $"「{user.DisplayName}」本来就可以使用「{classroom.Name}」。" });
    }

    // 写盘失败。这里必须报失败 —— 界面说"已授权"、重启后授权消失，
    // 是那种要到第二天上课才发现的问题。
    return Results.Json(
        new { error = "授权未能写入磁盘，未生效。请检查服务器磁盘空间与文件权限。" },
        statusCode: StatusCodes.Status500InternalServerError);
});

/// <summary>取消授权。</summary>
app.MapDelete(RelayPaths.ConsoleBindings, (
    string userId,
    string uuid,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    var removed = bindings.Revoke(userId, uuid);

    // 取消授权后，已建立的绑定会话也要断开，否则"取消"不会立刻生效
    if (removed)
    {
        foreach (var token in sessions.TeacherTokensOf(uuid))
        {
            if (sessions.TryGetTeacher(token, out var binding) && binding.UserId == userId)
            {
                sessions.UnbindTeacher(token);
                hub.Remove(MessageHub.TeacherKey(token));
            }
        }
    }

    return Results.Ok(new { ok = removed });
});

/// <summary>
/// 重置口令。既能重置普通用户，也能改管理员自己的口令
/// （UserId 传内置管理员的 Id 时写回配置文件）。
/// </summary>
app.MapPost("/api/console/password", (
    ResetPasswordRequest request,
    [FromHeader(Name = RelayPaths.AuthTokenHeader)] string? authToken) =>
{
    if (!userSessions.IsAdminSession(authToken))
    {
        return Results.Unauthorized();
    }

    if (!AccountRules.IsValidPassword(request.NewPassword))
    {
        return Results.BadRequest(new { error = $"口令至少 {AccountRules.MinPasswordLength} 位。" });
    }

    if (request.UserId == AdminUserId)
    {
        if (!config.UpdateAdminPassword(request.NewPassword, logger))
        {
            return Results.Json(
                new { error = "口令未能写入磁盘（权限或磁盘问题），本次修改已放弃，管理员口令保持不变。" },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 改完口令后旧会话仍然有效会让"改密码"失去意义，全部作废要求重新登录
        userSessions.RevokeAdminSessions();
        return Results.Ok(new { ok = true, message = "管理员口令已更新，请用新口令重新登录。" });
    }

    var (passwordOk, passwordError) = users.SetPassword(request.UserId, request.NewPassword);
    if (!passwordOk)
    {
        return Results.BadRequest(new { error = passwordError ?? "口令未修改。" });
    }

    // 同理：口令变了，之前签发的令牌不该继续可用
    userSessions.RevokeAllOf(request.UserId);

    var target = users.FindById(request.UserId);
    logger.LogWarning("管理员重置了账号口令：{Display}", target?.DisplayName ?? request.UserId);
    return Results.Ok(new { ok = true, message = $"已重置「{target?.DisplayName}」的口令。" });
});

logger.LogWarning("管理控制台已就绪：http://<服务器地址>{Port}/  账号 {Admin}（口令见配置文件 {Path}）",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "8080",
    config.AdminUsername,
    config.Path);

app.Run();

// 正常退出（收到停止信号）返回 0。上面那些 return 返回的是配置错误码，
// systemd 靠它区分"重启也没用"和"进程意外挂了"。
return 0;

// ======================== 局部函数 ========================

void TouchTeacher(TeacherBinding binding) => binding.LastSeenAt = DateTimeOffset.UtcNow;

/// <summary>把一条事件推给某教室当前绑定的全部教师。</summary>
void NotifyTeachers(string uuid, RelayEnvelope envelope)
{
    var tokens = sessions.TeacherTokensOf(uuid);
    if (tokens.Count == 0)
    {
        return;
    }

    hub.PublishToTeachers(tokens, envelope);
}

/// <summary>由登录令牌解析出用户档案；令牌无效或已过期返回 null。</summary>
UserProfile? ResolveUser(string? authToken)
{
    var userId = userSessions.Resolve(authToken);
    if (userId is null)
    {
        return null;
    }

    // 内置管理员不在用户库里 —— 它是由配置文件描述的。这里给它一份内存中的档案，
    // 这样"管理员也能像老师一样绑定教室喊话"就不必在每个端点上各写一遍特判，
    // 而且它默认对所有班级可用这件事（见 TeacherAuthorized 与 BindTeacher）才落得下去。
    // 账号名取自配置而不是写死 "admin"，运维改过管理员账号名时显示才对得上。
    return userId == AdminUserId
        ? new UserProfile(AdminUserId, config.AdminUsername, null, "管理员", config.AdminPasswordGeneratedAt, null, false)
        : users.FindById(userId);
}

/// <summary>该用户名或邮箱是否指向内置管理员。大小写不敏感，邮箱还要比本地部分。</summary>
bool IsAdminIdentity(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var candidate = value.Trim();

    if (string.Equals(candidate, config.AdminUsername, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // 邮箱的本地部分才是登录名：admin@x 这种显然要挡，
    // 而 someone@admin 只是域名里恰好有这个词，不该被误伤。
    var at = candidate.IndexOf('@');
    return at > 0
           && string.Equals(candidate[..at], config.AdminUsername, StringComparison.OrdinalIgnoreCase);
}

UserProfileDto ToDto(UserProfile profile)
    => new(
        profile.Id,
        profile.Username,
        profile.Email,
        profile.DisplayName,
        profile.CreatedAt,
        profile.LastLoginAt,
        profile.Disabled,
        false,
        profile.Subject,
        profile.SubjectByClassroom);

/// <summary>内置管理员的档案。它不是用户库里的一条记录，而是由配置文件描述的。</summary>
UserProfileDto ToAdminDto()
    => new(AdminUserId, config.AdminUsername, null, "管理员", config.AdminPasswordGeneratedAt, null, false, true);
