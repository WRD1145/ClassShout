using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClassShout.Classroom.Services;
using ClassShout.Core.Audio;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.RelayServer;

namespace ClassShout.EndToEnd;

/// <summary>
/// 端到端联调，跑真实的实现而不是打桩。两种模式：
///   · 默认 —— 局域网直连：UDP 发现、TCP 握手、文字喊话、语音流、停止指令、系统 TTS、音频播放；
///   · 加 relay 开关 —— 公网中继：教室注册、教师绑定、跨网喊话、多班级隔离。
/// </summary>
internal static class Program
{
    // 刻意不用默认端口，避免和正在运行的教室端应用抢端口
    private const int TcpPort = 45990;
    private const int DiscoveryPort = 45991;

    /// <summary>与服务端一致的 JSON 约定（camelCase）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {

        // 中继链路测试需要先自行启动中继服务器；不在这里拉进程，
        // 否则端口、启动时序、清理这些噪音会淹没"协议是否跑通"这个真正的问题。
        var relayIndex = Array.FindIndex(args, a => a.Equals("--relay", StringComparison.OrdinalIgnoreCase));
        if (relayIndex >= 0)
        {
            var url = relayIndex + 1 < args.Length && !args[relayIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[relayIndex + 1]
                : "http://127.0.0.1:8080";

            // 管理员口令用于验证「班级授权」那一段。
            // 服务器可能不在本机（例如部署在 Linux 上），所以这个值必须能显式传入 ——
            // 早期版本只去本地固定路径找配置文件，连远程服务器时会拿错口令，
            // 表现成一堆"授权失败"，而其实只是测试自己没拿到凭据。
            var passwordIndex = Array.FindIndex(args, a => a.Equals("--admin-password", StringComparison.OrdinalIgnoreCase));
            var adminPassword = passwordIndex >= 0 && passwordIndex + 1 < args.Length
                ? args[passwordIndex + 1]
                : null;

            return await RunRelayAsync(url, adminPassword);
        }

        // 纯进程内的并发不变量：不依赖网络，也不依赖中继服务器，
        // 所以只在局域网这条路径上跑一次，不必两个套件各跑一遍。
        await RunMessageQueueConcurrencyAsync();
        var runTts = args.Contains("--tts", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("ClassShout 端到端联调");
        Console.WriteLine($"  TCP 端口       : {TcpPort}");
        Console.WriteLine($"  UDP 发现端口   : {DiscoveryPort}");
        Console.WriteLine($"  系统 TTS 试读  : {(runTts ? "启用" : "跳过（加 --tts 启用）")}");
        Console.WriteLine();

        await using var server = new ClassroomServer(TcpPort) { ClassroomName = "端到端测试教室" };

        // 覆盖状态工厂，验证应用层能把静音/音量等运行态带给教师端
        server.StatusFactory = () => new StatusMessage
        {
            State = "idle",
            ClassroomName = "端到端测试教室",
            Muted = false,
            Volume = 80,
        };

        await using var announcer = new ClassroomAnnouncer(DiscoveryPort)
        {
            Current = new ClassroomAnnouncement
            {
                Id = "e2e-classroom",
                Name = "端到端测试教室",
                Host = NetworkUtility.GetLocalIPv4(),
                Port = TcpPort,
                AppVersion = "1.0.0",
            },
        };

        // 教室端收到的内容都先攒起来，测试后半段统一断言
        var receivedTexts = new List<string>();
        var receivedChunks = new List<byte[]>();
        var audioStarted = new TaskCompletionSource<AudioStartMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionReady = new TaskCompletionSource<TeacherSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SessionOpened += (_, session) =>
        {
            session.TextShoutReceived += (_, message) => { lock (receivedTexts) { receivedTexts.Add(message.Text); } };
            session.AudioStarted += (_, message) => audioStarted.TrySetResult(message);
            session.AudioChunkReceived += (_, payload) => { lock (receivedChunks) { receivedChunks.Add(payload.ToArray()); } };
            session.AudioEnded += (_, _) => audioEnded.TrySetResult();
            session.StopRequested += (_, message) => stopReceived.TrySetResult(message.Reason);
            sessionReady.TrySetResult(session);
        };

        server.Start();
        announcer.Start();

        // ---------- 1. UDP 发现 ----------
        var discovery = new ClassroomDiscovery(DiscoveryPort, TimeSpan.FromSeconds(2));
        var found = await discovery.ScanAsync();
        Check("UDP 发现能扫到教室端", found.Count == 1, $"发现 {found.Count} 个");
        Check("发现结果带回教室名", found.Any(f => f.Name == "端到端测试教室"),
            found.Count > 0 ? $"名称={found[0].Name}" : "无结果");
        Check("发现结果带回正确端口", found.Any(f => f.Port == TcpPort),
            found.Count > 0 ? $"端口={found[0].Port}" : "无结果");

        // ---------- 2. TCP 连接与握手 ----------
        await using var client = new TeacherClient();
        var statusMessage = new TaskCompletionSource<StatusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ControlReceived += message =>
        {
            if (message is StatusMessage status)
            {
                statusMessage.TrySetResult(status);
            }
        };

        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, TcpPort), "端到端测试教师端");
        Check("TCP 连接成功", client.IsConnected, $"IsConnected={client.IsConnected}");

        var session = await sessionReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await statusMessage.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Check("教室端识别出教师端名称", session.ClientName == "端到端测试教师端", $"ClientName={session.ClientName}");
        Check("握手后自动回状态，并带回教室名", statusMessage.Task.Result.ClassroomName == "端到端测试教室",
            $"教室名={statusMessage.Task.Result.ClassroomName}");
        Check("状态带回教室端音量", statusMessage.Task.Result.Volume == 80,
            $"音量={statusMessage.Task.Result.Volume}");

        // ---------- 3. 文字喊话 ----------
        const string shoutText = "同学们请安静，现在开始上课。";
        await client.SendTextAsync(new TextShoutMessage { Text = shoutText, Rate = 1, Volume = 90 });
        await WaitUntilAsync(() => { lock (receivedTexts) { return receivedTexts.Count > 0; } }, TimeSpan.FromSeconds(3));

        lock (receivedTexts)
        {
            Check("教室端收到文字喊话", receivedTexts.Count == 1, $"收到 {receivedTexts.Count} 条");
            Check("文字内容完全一致", receivedTexts.Count > 0 && receivedTexts[0] == shoutText,
                receivedTexts.Count > 0 ? $"内容={receivedTexts[0]}" : "无内容");
        }

        // ---------- 4. 语音流 ----------
        var format = AudioFormat.Default;
        const int chunkCount = 40;
        var chunkSize = format.BytesForDuration(20); // 20 毫秒一片
        var sentChunks = new List<byte[]>(chunkCount);

        for (var i = 0; i < chunkCount; i++)
        {
            // 用可预测的锯齿波填充，便于发现字节错位或乱序
            var chunk = new byte[chunkSize];
            for (var s = 0; s < chunkSize / 2; s++)
            {
                var sample = (short)((s + i * 7) % 1000 - 500);
                chunk[s * 2] = (byte)(sample & 0xFF);
                chunk[s * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            sentChunks.Add(chunk);
        }

        await client.SendAudioStartAsync("e2e-audio-1", format);

        var startMessage = await audioStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check("教室端收到语音开始", true, $"{startMessage.SampleRate} Hz / {startMessage.Channels} 声道 / {startMessage.BitsPerSample} bit");
        Check("语音格式与发送端一致",
            startMessage.SampleRate == format.SampleRate &&
            startMessage.Channels == format.Channels &&
            startMessage.BitsPerSample == format.BitsPerSample,
            "参数一致");

        foreach (var chunk in sentChunks)
        {
            await client.SendAudioAsync(chunk);
        }

        await client.SendAudioEndAsync("e2e-audio-1");
        await audioEnded.Task.WaitAsync(TimeSpan.FromSeconds(3));

        byte[][] receivedSnapshot;
        lock (receivedChunks)
        {
            receivedSnapshot = receivedChunks.ToArray();
        }

        Check("音频分片数量一致", receivedSnapshot.Length == sentChunks.Count,
            $"发出 {sentChunks.Count} 片，收到 {receivedSnapshot.Length} 片");

        var identical = receivedSnapshot.Length == sentChunks.Count;
        var totalBytes = 0;
        for (var i = 0; i < Math.Min(receivedSnapshot.Length, sentChunks.Count); i++)
        {
            totalBytes += receivedSnapshot[i].Length;
            if (!receivedSnapshot[i].AsSpan().SequenceEqual(sentChunks[i]))
            {
                identical = false;
                Console.WriteLine($"      第 {i} 片内容不一致");
                break;
            }
        }

        Check("音频字节逐片完全一致（无错位、无乱序）", identical,
            $"{totalBytes} 字节，时长 {format.DurationMsOf(totalBytes)} 毫秒");

        // ---------- 5. 停止指令 ----------
        await client.SendStopAsync("端到端测试要求停止");
        var stopReason = await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check("教室端收到停止指令", stopReason.Contains("停止"), $"原因={stopReason}");

        // ---------- 6. 音频播放链路 ----------
        try
        {
            using var player = new NAudioLoopbackPlayer();
            player.Start(format);
            foreach (var chunk in sentChunks.Take(5))
            {
                player.Write(chunk);
            }

            await player.CompleteAsync();
            Check("音频播放链路可用（NAudio 送入并播完 100 毫秒）", true, "未抛异常");
        }
        catch (Exception ex)
        {
            Check("音频播放链路可用", false, $"抛出 {ex.GetType().Name}：{ex.Message}");
        }

        // ---------- 7. 系统 TTS ----------
        using var speech = new WindowsSpeechSynthesizer();
        var voices = speech.GetVoices();
        var chineseVoice = speech.GetDefaultChineseVoice();

        Check("系统已安装语音合成引擎", voices.Count > 0, $"共 {voices.Count} 个语音");
        Check("存在中文语音", chineseVoice is not null, chineseVoice ?? "未找到中文语音");

        if (runTts && chineseVoice is not null)
        {
            try
            {
                var spss = DateTime.UtcNow;
                await speech.SpeakAsync("教室端语音测试正常。", new SpeechRequestOptions(1, 100, chineseVoice));
                var elapsed = DateTime.UtcNow - spss;
                Check("系统 TTS 朗读完成", elapsed > TimeSpan.FromMilliseconds(300),
                    $"耗时 {elapsed.TotalSeconds:F2} 秒（太短说明没真正发声）");
            }
            catch (Exception ex)
            {
                Check("系统 TTS 朗读完成", false, $"抛出 {ex.GetType().Name}：{ex.Message}");
            }
        }

        // ---------- 8. 断开 ----------
        await client.DisconnectAsync();
        await Task.Delay(300);
        Check("断开后教室端会话被清理", server.Sessions.Count == 0, $"剩余会话 {server.Sessions.Count} 个");

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    // ======================== 中继链路（跨局域网） ========================

    /// <summary>
    /// 中继链路联调。需要先自行启动中继服务器：
    ///     dotnet run 指定 src\ClassShout.RelayServer，监听 http://127.0.0.1:8080
    /// 本方法不负责起服务器 —— 让测试代码去拉进程会引入端口、时序、清理等一堆噪音，
    /// 而这些与"协议是否跑通"无关。
    /// </summary>
    private static async Task<int> RunRelayAsync(string baseUrl, string? explicitAdminPassword)
    {
        var root = baseUrl.TrimEnd('/');

        Console.WriteLine("ClassShout 中继链路联调（跨局域网）");
        Console.WriteLine($"  服务器 : {root}");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        try
        {
            var health = await http.GetStringAsync($"{root}{RelayPaths.Health}");
            Check("服务器健康检查", health.Contains("\"ok\":true", StringComparison.Ordinal), "返回 ok");
        }
        catch (Exception ex)
        {
            Check("服务器健康检查", false, $"连不上服务器：{ex.Message}");
            Console.WriteLine();
            Console.WriteLine("请先启动中继服务器再运行本测试。");
            return 1;
        }

        var uuid = Guid.NewGuid().ToString("D");
        const string secret = "TEST2026";

        // ---------- 1. 教室端注册 ----------
        var settings = new ClassroomRelaySettings
        {
            ServerUrl = root,
            Uuid = uuid,
            ClassroomName = "中继测试教室",
            Secret = secret,
        };

        var textReceived = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioStartReceived = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioEndReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var classroom = new ClassroomRelayClient(http, settings);
        classroom.ShoutReceived += envelope =>
        {
            switch (envelope.Kind)
            {
                case RelayKinds.TextShout:
                    textReceived.TrySetResult(envelope);
                    break;
                case RelayKinds.AudioStart:
                    audioStartReceived.TrySetResult(envelope);
                    break;
                case RelayKinds.Audio when envelope.AudioBase64 is not null:
                    audioReceived.TrySetResult(Convert.FromBase64String(envelope.AudioBase64));
                    break;
                case RelayKinds.AudioEnd:
                    audioEndReceived.TrySetResult();
                    break;
                case RelayKinds.Stop:
                    stopReceived.TrySetResult();
                    break;
            }
        };

        var registered = await classroom.RegisterAsync(message => Console.WriteLine($"       · {message}"));
        Check("教室端注册成功", registered, $"UUID={uuid[..8]}…");
        Check("服务器标记为新建记录", classroom.WasNewlyRegistered, $"IsNew={classroom.WasNewlyRegistered}");
        Check("UUID 已落盘到设置对象", settings.Uuid == uuid, "一致");

        classroom.StartPolling();
        await Task.Delay(300);

        // ---------- 2. 账号：注册 / 登录 ----------
        // 用全新的设置对象，避免读到本机已有的登录状态而让测试结果不可复现
        var teacherSettings = new TeacherRelaySettings { ServerUrl = root };
        var account = new AccountClient(http, teacherSettings);

        var accountName = "e2e" + Guid.NewGuid().ToString("N")[..8];
        const string accountPassword = "e2e-pass-1234";

        var (regOk, regError) = await account.RegisterAsync(accountName, null, "端到端张老师", accountPassword);
        Check("用户名注册成功", regOk, regError ?? $"用户名={accountName}");
        Check("注册后即处于登录态", account.IsSignedIn, $"显示名={account.DisplayName}");

        await account.LogoutAsync();
        Check("登出后回到未登录", !account.IsSignedIn, "已登出");

        var (badLogin, badLoginError) = await account.LoginAsync(accountName, "wrong-password");
        Check("错口令登录被拒", !badLogin, badLoginError ?? "被拒");

        var (loginOk, loginError) = await account.LoginAsync(accountName, accountPassword);
        Check("正确口令登录成功", loginOk, loginError ?? $"显示名={account.DisplayName}");

        // ---------- 3. 教师端绑定 ----------
        await using var teacher = new TeacherRelayClient(http, teacherSettings);

        var (badOk, badError) = await teacher.BindAsync(uuid, "WRONG-SECRET", "入侵者");
        Check("口令错误时拒绝绑定", !badOk, badError ?? "被拒");

        var (missingOk, missingError) = await teacher.BindAsync(Guid.NewGuid().ToString("D"), secret, "张老师");
        Check("UUID 不存在时拒绝绑定", !missingOk, missingError ?? "被拒");

        // 故意传一个错误的显示名：服务器应当忽略它，改用账号里的姓名
        var (bindOk, bindError) = await teacher.BindAsync(uuid, secret, "这个字符串不该被采用");
        Check("正确 UUID + 口令绑定成功", bindOk, bindError ?? $"教室={teacher.BoundClassroomName}");

        var statusReceived = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        teacher.EventReceived += envelope =>
        {
            if (envelope.Kind == RelayKinds.Status)
            {
                statusReceived.TrySetResult(envelope);
            }
        };

        teacher.StartPolling();
        await Task.Delay(300);

        // ---------- 3. 文字喊话 ----------
        const string shoutText = "中继链路测试：同学们请安静。";
        await teacher.SendTextAsync(shoutText, 1, 90, interrupt: true);

        var text = await textReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("教室端经中继收到文字喊话", text.Text == shoutText, $"内容=「{text.Text}」");
        // 关键一条：带上登录令牌后，服务器应当以账号里的姓名为准，
        // 而不是采用客户端 bind 时传的那个自由字符串 —— 否则姓名可以被随意冒充。
        Check("喊话来源是账号里注册的姓名（而非客户端自填）",
            text.From == "端到端张老师",
            $"From={text.From}");

        // ---------- 4. 语音流（含逐字节校验） ----------
        var format = AudioFormat.Default;
        const int batchCount = 10;
        const int bytesPerBatch = 3200; // 100 毫秒

        await teacher.SendAudioStartAsync(format);
        var start = await audioStartReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check("教室端收到语音开始", start.SampleRate == format.SampleRate,
            $"{start.SampleRate} Hz / {start.Channels} 声道 / {start.BitsPerSample} bit");

        // 造一段可预测的波形，便于发现字节错位
        var expected = new List<byte>(batchCount * bytesPerBatch);
        for (var batch = 0; batch < batchCount; batch++)
        {
            var chunk = new byte[bytesPerBatch];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = (byte)((i + batch * 13) % 251);
            }

            expected.AddRange(chunk);
            teacher.AccumulateAudio(chunk);
            await Task.Delay(120);
        }

        var firstAudio = await audioReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("教室端经中继收到音频分片", firstAudio.Length == bytesPerBatch, $"{firstAudio.Length} 字节");

        var prefixMatches = true;
        for (var i = 0; i < firstAudio.Length; i++)
        {
            if (firstAudio[i] != expected[i])
            {
                prefixMatches = false;
                break;
            }
        }

        Check("音频字节逐字节一致（无错位）", prefixMatches, $"已比对 {firstAudio.Length} 字节");

        await teacher.SendAudioEndAsync();
        await audioEndReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check("教室端收到语音结束", true, "会话已收尾");

        // ---------- 5. 停止指令 ----------
        await teacher.SendStopAsync();
        await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check("教室端收到停止指令", true, "已中断当前播放");

        // ---------- 6. 反向通道：教室端状态回传教师端 ----------
        await classroom.ReportStatusAsync(muted: true, volume: 42, state: "idle");
        var status = await statusReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("教师端收到教室端状态", status.Muted && status.Volume == 42,
            $"静音={status.Muted} 音量={status.Volume}");

        // ---------- 7. 班级授权（控制台快速绑定） ----------
        var adminPassword = ResolveAdminPassword(explicitAdminPassword);
        var adminToken = adminPassword is null
            ? string.Empty
            : await TryAdminLoginAsync(http, root, adminPassword);

        if (string.IsNullOrEmpty(adminToken))
        {
            // 口令是显式给的却登不上 —— 那是真的有问题，报失败。
            // 口令是猜来的（本地配置文件）却登不上 —— 多半是连了别的服务器，跳过而不是误报。
            var explicitPasswordFailed = explicitAdminPassword is not null;
            Check("班级授权流程", !explicitPasswordFailed, explicitPasswordFailed
                ? "** 提供了管理员口令但登录失败，请核对是否与目标服务器一致"
                : "跳过 —— 未提供管理员口令，可用 --admin-password 指定");
        }
        else
        {
            using var adminHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            adminHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            // 授权前：老师看不到任何班级，且免口令绑定应被拒
            var beforeList = await teacher.GetAuthorizedClassroomsAsync();
            Check("授权前老师看不到班级", beforeList.Count == 0, $"已授权 {beforeList.Count} 个");

            var (noGrantOk, noGrantError) = await teacher.BindAsync(uuid, string.Empty, "周老师");
            Check("授权前免口令绑定被拒", !noGrantOk, noGrantError ?? "被拒");

            // 管理员授权
            var grantResponse = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest(teacherSettings.UserId!, uuid),
                JsonOptions);
            var grantBody = await grantResponse.Content.ReadAsStringAsync();
            Check("管理员授权成功", grantResponse.IsSuccessStatusCode, Trim(grantBody));

            // 授权后：老师在列表里看得到，且不传口令就能绑定
            var afterList = await teacher.GetAuthorizedClassroomsAsync();
            Check("授权后老师能看到该班级", afterList.Any(item => item.Uuid == uuid),
                afterList.Count > 0 ? $"共 {afterList.Count} 个，首个={afterList[0].Name}" : "列表为空");

            var (grantedBindOk, grantedBindError) = await teacher.BindAsync(uuid, string.Empty, "这个字符串不该被采用");
            Check("授权后可免口令绑定", grantedBindOk, grantedBindError ?? $"教室={teacher.BoundClassroomName}");

            // 取消授权后应立即失效
            var revokeResponse = await adminHttp.DeleteAsync(
                $"{root}{RelayPaths.ConsoleBindings}?userId={Uri.EscapeDataString(teacherSettings.UserId!)}&uuid={Uri.EscapeDataString(uuid)}");
            Check("取消授权成功", revokeResponse.IsSuccessStatusCode, $"HTTP {(int)revokeResponse.StatusCode}");

            var (revokedBindOk, _) = await teacher.BindAsync(uuid, string.Empty, "周老师");
            Check("取消授权后免口令绑定再次被拒", !revokedBindOk, "已拒绝");

            // 后续步骤要正常发喊话，这里用口令重新绑上
            await teacher.BindAsync(uuid, secret, "周老师");

            // ---------- 7b. 内置管理员 ----------
            //
            // 管理员是唯一能登录控制台的账号，而控制台又正是改管理员口令的地方。
            // 它必须先出现在用户列表里，界面上那句"到用户页里改口令"才有落点。

            var usersResponse = await adminHttp.GetAsync($"{root}/api/console/users");
            var userList = await usersResponse.Content.ReadFromJsonAsync<List<UserProfileDto>>(JsonOptions) ?? [];
            var adminEntry = userList.FirstOrDefault(u => u.IsAdmin);

            Check("用户列表里能看到内置管理员",
                adminEntry is not null && userList[0] == adminEntry,
                adminEntry is null
                    ? $"列表里没有 isAdmin 的账号（共 {userList.Count} 个）"
                    : $"共 {userList.Count} 个，首项={adminEntry.DisplayName}（{adminEntry.Username}）");

            Check("内置管理员不能被停用",
                (await adminHttp.PostAsJsonAsync($"{root}/api/console/users/builtin-admin/disabled",
                    new ConsoleFlagRequest(true), JsonOptions)).StatusCode == System.Net.HttpStatusCode.BadRequest,
                "已拒绝停用内置管理员");

            // 管理员默认对所有班级可用：授权列表里应当直接出现已注册的教室。
            // 这里不用 GetFromJsonAsync —— 它在非 2xx 时直接抛异常，
            // 会让一次断言失败变成整个测试进程崩溃，看不到后面的检查项。
            var adminAuthorizedResponse = await adminHttp.GetAsync($"{root}{RelayPaths.TeacherAuthorized}");
            var adminAuthorized = adminAuthorizedResponse.IsSuccessStatusCode
                ? await adminAuthorizedResponse.Content.ReadFromJsonAsync<List<AuthorizedClassroom>>(JsonOptions) ?? []
                : [];
            Check("管理员无需授权即可看到所有班级",
                adminAuthorized.Any(item => item.Uuid == uuid),
                adminAuthorizedResponse.IsSuccessStatusCode
                    ? $"共 {adminAuthorized.Count} 个"
                    : $"HTTP {(int)adminAuthorizedResponse.StatusCode}");

            // 空口令也能绑上 —— 这是"默认绑定所有班级"最直接的证据
            var adminBindResponse = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.BindTeacher}",
                new TeacherBindRequest(uuid, string.Empty, "不该被采用的名字"),
                JsonOptions);
            var adminBind = await adminBindResponse.Content.ReadFromJsonAsync<TeacherBindResponse>(JsonOptions);
            Check("管理员可以空口令绑定任意班级",
                adminBind is { Ok: true },
                adminBind is { Ok: true }
                    ? $"教室={adminBind.ClassroomName}"
                    : adminBind?.Error ?? "绑定失败");

            var adminGrantResponse = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest("builtin-admin", uuid),
                JsonOptions);
            var adminGrantBody = await adminGrantResponse.Content.ReadAsStringAsync();
            Check("给管理员授权会被明确告知无需授权",
                adminGrantResponse.IsSuccessStatusCode && adminGrantBody.Contains("不需要单独授权"),
                Trim(adminGrantBody));
        }

        // ---------- 8. 多班级隔离 ----------
        var otherSettings = new ClassroomRelaySettings
        {
            ServerUrl = root,
            Uuid = Guid.NewGuid().ToString("D"),
            ClassroomName = "隔壁班",
            Secret = "OTHER123",
        };

        await using var otherClassroom = new ClassroomRelayClient(http, otherSettings);
        var otherGotShout = false;
        otherClassroom.ShoutReceived += _ => otherGotShout = true;

        var otherRegistered = await otherClassroom.RegisterAsync();
        Check("第二间教室也能注册", otherRegistered, $"UUID={otherSettings.Uuid[..8]}…");
        otherClassroom.StartPolling();
        await Task.Delay(500);

        await teacher.SendTextAsync("这条只应发给第一间教室", 1, 80, interrupt: true);
        await Task.Delay(2000);

        Check("另一间教室未收到不属于它的喊话", !otherGotShout, "多班级互相隔离");

        // ---------- 9. 服务端安全加固 ----------
        //
        // 这一节的检查刻意放在最后：限速那一条会把当前 IP 的令牌桶用光，
        // 放在中间会让后面所有需要登录的步骤莫名其妙地拿 429。

        // 管理员账号名必须被保留。UserStore 只看得见自己那张表，
        // 拦不住老师注册一个同名普通账号 —— 那会让控制台出现两个 admin。
        // 这里用 "admin"：与 TryAdminLoginAsync 里登录管理员时用的是同一个假设。
        var squat = await http.PostAsJsonAsync($"{root}{RelayPaths.AuthRegister}",
            new RegisterRequest("admin", null, "冒名管理员", "Squat12345"),
            JsonOptions);
        var squatBody = await squat.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Check("管理员账号名被保留（不能注册同名普通账号）",
            squatBody is { Ok: false },
            squatBody?.Error ?? "居然注册成功了");

        // 音频分片体积上限：不给上限时，持令牌者可以用大包把服务器内存撑爆。
        var oversized = new byte[32 * 1024];
        var bindResponse = await http.PostAsJsonAsync($"{root}{RelayPaths.BindTeacher}",
            new TeacherBindRequest(uuid, secret, "周老师"), JsonOptions);
        var rawBind = await bindResponse.Content.ReadFromJsonAsync<TeacherBindResponse>(JsonOptions);

        if (rawBind is { Ok: true, Token: not null })
        {
            // 用 string.Format 而不是 RelayPaths.Route：后者是给服务端声明路由模板用的
            // （它把 {0} 替换成 {token} 这样的占位符），拿来拼客户端地址会多出一对花括号。
            using var audioRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{root}{string.Format(RelayPaths.TeacherAudio, rawBind.Token)}")
            {
                Content = new ByteArrayContent(oversized),
            };

            var audioResponse = await http.SendAsync(audioRequest);
            Check("超限音频分片被拒（413）",
                audioResponse.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge,
                $"HTTP {(int)audioResponse.StatusCode}");
        }
        else
        {
            Check("超限音频分片被拒（413）", false, "无法取得教师令牌，前置步骤失败");
        }

        // 匿名端点限速：注册与登录每个请求都要跑 10 万次 PBKDF2，
        // 不限速的话单机就能把 CPU 吃干净。
        var throttled = 0;
        for (var i = 0; i < 45; i++)
        {
            var burst = await http.PostAsJsonAsync($"{root}{RelayPaths.AuthLogin}",
                new LoginRequest("nosuchuser", "wrongpassword"), JsonOptions);

            if (burst.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throttled++;
            }
        }

        Check("匿名登录端点有限速（连续请求会被 429）", throttled > 0,
            throttled > 0 ? $"45 次连发中有 {throttled} 次被限流" : "45 次连发全部放行，没有限速");

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 长轮询的丢唤醒竞态。
    ///
    /// 原来的 WaitAsync 先把历史读一遍（取一次锁），确认没有新事件之后，
    /// 再单独取一次锁把自己的等待者登记进去。这两步之间有一个几百纳秒的窗口：
    /// 如果 Publish 恰好落在这个窗口里，它既没有被那次历史检查看见，
    /// 也没有任何等待者可以唤醒 —— 于是这条消息要一直等到 25 秒超时才送出。
    /// 单条文字喊话最容易被它打中，因为一次投递就只有那一条。
    ///
    /// 走 HTTP 根本撞不上这个窗口（窗口太窄），所以直接把 MessageQueue
    /// 拿到进程内来，用多线程把 Publish 往那个窗口上砸。
    ///
    /// 注意这里断言的是**延迟**而不是"有没有送到"。
    ///
    /// 丢唤醒的症状不是消息丢了 —— 超时之后那次兜底的 ReadSince 仍会把事件读出来，
    /// 所以消息最终一定会到达，只是晚了整整一个超时周期（生产环境是 25 秒）。
    /// 只检查"收到了"的话，这个 bug 会大摇大摆地溜过去。
    /// </summary>
    private static async Task RunMessageQueueConcurrencyAsync()
    {
        const int Iterations = 2000;
        var timeout = TimeSpan.FromSeconds(2);
        var slowThreshold = TimeSpan.FromMilliseconds(800);

        var fast = 0;
        var slow = 0;

        for (var i = 0; i < Iterations; i++)
        {
            var queue = new MessageQueue();

            // 先放一条"预热"事件，把序号推过 0。
            //
            // 这一步不是可有可无：since 传 0 的语义是"从此刻开始，不补发历史"，
            // 于是 WaitAsync 会就地把 since 归一到当前的 _sequence。若 Publish
            // 抢在轮询序言之前跑到，那条就被当成历史正确地跳过了 —— 测试会误判成
            // "丢了"，然后老老实实等满整个超时。传一个大于 0 的 since 就没有这个歧义。
            queue.Publish(new RelayEnvelope { Kind = RelayKinds.TextShout, From = "预热", Text = "预热" });
            var since = queue.LastSequence;

            // 让轮询先跑起来，再让 Publish 从另一个线程砸过去
            var poll = Task.Run(() => queue.WaitAsync(since, timeout, CancellationToken.None));

            // 这个自旋刻意取得很小：它让 Publish 有更大机会正好落在
            // "历史检查完"与"等待者登记上"之间，也就是那个丢唤醒窗口里。
            // 落在窗口之前或之后都是正常路径，只有正好落在窗口里才会被这条断言抓住。
            Thread.SpinWait(80 + (i % 240));

            queue.Publish(new RelayEnvelope
            {
                Kind = RelayKinds.TextShout,
                From = "并发测试",
                Text = $"第 {i} 条",
            });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await poll;
            stopwatch.Stop();

            var arrived = result.Events.Any(e => e.Sequence > since);

            if (!arrived || result.TimedOut || stopwatch.Elapsed > slowThreshold)
            {
                slow++;
            }
            else
            {
                fast++;
            }
        }

        Check("长轮询不会丢唤醒（并发 2000 次，按延迟判定）", slow == 0,
            slow == 0
                ? $"{fast}/{Iterations} 条都在 {slowThreshold.TotalMilliseconds:0} 毫秒内送达"
                : $"有 {slow} 条是干等到 {timeout.TotalSeconds:0} 秒超时才送出的，丢唤醒窗口又出现了");
    }

    /// <summary>
    /// 取管理员口令。优先环境变量；否则去服务器项目的输出目录里找 relay-config.json。
    /// 找不到就跳过授权相关的检查 —— 与其猜一个口令让测试变成"看环境脸色"，不如明确跳过。
    /// </summary>
    private static string? ResolveAdminPassword(string? explicitPassword)
    {
        if (!string.IsNullOrWhiteSpace(explicitPassword))
        {
            return explicitPassword;
        }

        var fromEnv = Environment.GetEnvironmentVariable("CLASSSHOUT_ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        // 从测试程序集所在目录回溯到仓库根，再进服务器项目的输出目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ClassShout.RelayServer", "bin", "Debug", "net10.0", "relay-config.json");
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                if (document.RootElement.TryGetProperty("AdminPassword", out var password))
                {
                    return password.GetString();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>尝试以管理员身份登录。失败返回空串，由调用方决定是报错还是跳过。</summary>
    private static async Task<string> TryAdminLoginAsync(HttpClient http, string root, string password)
    {
        try
        {
            var response = await http.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthLogin}",
                new LoginRequest("admin", password),
                JsonOptions);

            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
            return auth is { Ok: true } ? auth.Token ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return string.Empty;
        }
    }

    private static string Trim(string text)
        => text.Length <= 90 ? text : text[..90] + "…";

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name} —— {detail}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name} —— {detail}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(30);
        }
    }
}
