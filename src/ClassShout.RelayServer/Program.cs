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
    var (profile, error) = users.Register(request.Username, request.Email, request.DisplayName, request.Password);
    if (profile is null)
    {
        return Results.Ok(new AuthResponse(false, null, null, error));
    }

    var token = userSessions.Issue(profile.Id);
    return Results.Ok(new AuthResponse(true, token, ToDto(profile), null));
});

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
});

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
});

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

    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
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

// ======================== 管理控制台 WebUI ========================

// 界面用嵌入资源而不是 wwwroot 静态目录：
// 发布产物就是一个单文件服务器，不必关心工作目录与静态文件中间件的配置。
app.MapGet("/", () => Results.Content(WebUi.Page, "text/html; charset=utf-8"));

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

    if (!bindings.Grant(user.Id, classroom.Uuid, config.AdminUsername))
    {
        return Results.Ok(new { ok = true, message = $"「{user.DisplayName}」本来就可以使用「{classroom.Name}」。" });
    }

    return Results.Ok(new { ok = true, message = $"已把「{classroom.Name}」授权给「{user.DisplayName}」。" });
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

    if (!users.SetPassword(request.UserId, request.NewPassword))
    {
        return Results.BadRequest(new { error = "账号不存在或口令不符合要求。" });
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

UserProfileDto ToDto(UserProfile profile)
    => new(profile.Id, profile.Username, profile.Email, profile.DisplayName, profile.CreatedAt, profile.LastLoginAt, profile.Disabled, false);

/// <summary>内置管理员的档案。它不是用户库里的一条记录，而是由配置文件描述的。</summary>
UserProfileDto ToAdminDto()
    => new(AdminUserId, config.AdminUsername, null, "管理员", config.AdminPasswordGeneratedAt, null, false, true);