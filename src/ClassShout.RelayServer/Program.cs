using System.Threading.RateLimiting;
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
builder.Services.AddSingleton<UserSessions>();
builder.Services.AddSingleton<RelaySessions>();
builder.Services.AddSingleton<MessageHub>();

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
var userSessions = app.Services.GetRequiredService<UserSessions>();
var sessions = app.Services.GetRequiredService<RelaySessions>();
var hub = app.Services.GetRequiredService<MessageHub>();
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

    var (profile, error) = users.Register(request.Username, request.Email, request.DisplayName, request.Password);
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

    var teacherName = profile?.DisplayName ?? request.TeacherName;

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
        From = binding.TeacherName,
        Text = request.Text,
        Rate = request.Rate,
        Volume = request.Volume,
        Interrupt = request.Interrupt,
    });

    logger.LogInformation("{Teacher} → {Uuid}：文字喊话 {Length} 字",
        binding.TeacherName, binding.ClassroomUuid, request.Text?.Length ?? 0);

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
        From = binding.TeacherName,
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
        From = binding.TeacherName,
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
        From = binding.TeacherName,
    });

    logger.LogInformation("{Teacher} → {Uuid}：语音结束，累计 {Bytes:N0} 字节",
        binding.TeacherName, binding.ClassroomUuid, binding.AudioBytes);

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
        From = binding.TeacherName,
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
        "1.0.0"));
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

    var (profile, error) = users.Register(request.Username, request.Email, request.DisplayName ?? string.Empty, request.Password);
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
/// 格式：每行 用户名,邮箱,姓名,口令。用户名与邮箱至少填一个（另一个留空即可）。
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

        var fields = line.Split(',').Select(f => f.Trim().Trim('"')).ToArray();
        if (fields.Length < 4)
        {
            failed++;
            details.Add($"第 {lineNumber} 行：需要 4 列（用户名,邮箱,姓名,口令），实际 {fields.Length} 列。");
            continue;
        }

        var (username, email, displayName, password) =
            (Empty(fields[0]), Empty(fields[1]), Empty(fields[2]), fields[3]);

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

        var (profile, error) = users.Register(username, email, displayName ?? string.Empty, password);
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

/// <summary>停用 / 启用账号。</summary>
app.MapPost("/api/console/users/{id}/disabled", (
    string id,
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
    => new(profile.Id, profile.Username, profile.Email, profile.DisplayName, profile.CreatedAt, profile.LastLoginAt, profile.Disabled, false);

/// <summary>内置管理员的档案。它不是用户库里的一条记录，而是由配置文件描述的。</summary>
UserProfileDto ToAdminDto()
    => new(AdminUserId, config.AdminUsername, null, "管理员", config.AdminPasswordGeneratedAt, null, false, true);