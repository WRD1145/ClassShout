using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using ClassShout.Classroom.Services;
using ClassShout.Core.Audio;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using ClassShout.RelayServer;
using ClassShout.Teacher.Services;

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

        // Edge 在线语音：默认跳过，因为它依赖外网。
        // 教室网经常是隔离的，把它做成默认项会让回归在正常环境里也失败。
        if (args.Contains("--edge-tts", StringComparer.OrdinalIgnoreCase))
        {
            await CheckEdgeTtsAsync();
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

        // ---------- 3b. 协议版本不符时必须拒收 ----------
        //
        // 版本号存在的意义就是"线路格式可能变了"。以前 IsHandshaken 只被赋值、
        // 从来没人读，于是版本不匹配的教师端照样能喊话 ——
        // 真到不兼容那天，教室端会照着一个读不懂的格式去播放。
        await AssertVersionMismatchRejectedAsync(receivedTexts, shoutText);

        // ---------- 3c. 畸形音频格式不能把教室端带走 ----------
        AssertMalformedAudioFormatRejected();

        // ---------- 3d. 本机喊话记录的条数上限 ----------
        AssertShoutHistoryCap();

        // ---------- 3e. 教室端的设置锁 ----------
        AssertSettingsLock();

        // ---------- 3f. 语音转文字客户端 ----------
        await AssertSttClientAsync();

        // ---------- 3g. 发送队列 ----------
        await AssertShoutQueueAsync();

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

        // 收到的音频信封总数。TrySetResult 只保留第一条，数不出"多收了一条"，
        // 而下面那条"没有 audioStart 的分片必须被丢弃"的断言正需要计数。
        var audioEnvelopeCount = 0;
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
                    Interlocked.Increment(ref audioEnvelopeCount);
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

                // ---------- 7c. 控制台的账号管理与喊话 ----------

            // 手动创建账号
            var createdName = "ctl" + Guid.NewGuid().ToString("N")[..8];
            var createResponse = await adminHttp.PostAsJsonAsync($"{root}/api/console/users",
                new CreateUserRequest(createdName, null, "控制台创建的张老师", "Init12345"), JsonOptions);
            var createBody = await createResponse.Content.ReadAsStringAsync();
            Check("控制台能手动创建账号", createResponse.IsSuccessStatusCode, Trim(createBody));

            // 新建的账号应当立刻出现在用户列表里
            var afterCreate = await adminHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [];
            Check("新建的账号出现在用户列表里",
                afterCreate.Any(u => u.Username == createdName),
                $"列表共 {afterCreate.Count} 个账号");

            // CSV 批量导入：一行合法、一行列数不足、一行重名、一行与管理员冲突
            var importCsv = string.Join('\n',
                "用户名,邮箱,姓名,口令",
                $"csvok{Guid.NewGuid():N}"[..12] + ",,CSV 老师甲,Init12345",
                "只有一列",
                $"{createdName},,重复账号,Init12345",
                "admin,,冒名管理员,Init12345");

            var importResponse = await adminHttp.PostAsJsonAsync($"{root}/api/console/users/import",
                new ImportUsersRequest(importCsv), JsonOptions);
            var import = await importResponse.Content.ReadFromJsonAsync<ImportUsersResponse>(JsonOptions);

            Check("CSV 批量导入逐行处理（成功的建成、失败的不拖累其他行）",
                import is { Created: 1, Failed: 3 },
                import is null ? "解析失败" : $"成功 {import.Created} 条、失败 {import.Failed} 条");

            Check("CSV 导入会挡掉与内置管理员重名的行",
                import?.Details.Any(d => d.Contains("内置管理员")) == true,
                import?.Details.LastOrDefault(d => d.Contains("内置管理员")) ?? "没有相关说明");

            // 管理员直接对教室喊话
            var consoleShoutText = $"控制台喊话测试 {Guid.NewGuid():N}"[..24];
            var shoutResponse = await adminHttp.PostAsJsonAsync($"{root}/api/console/shout",
                new ConsoleShoutRequest(uuid, consoleShoutText), JsonOptions);
            var shoutBody = await shoutResponse.Content.ReadAsStringAsync();
            Check("管理员能从控制台直接喊话", shoutResponse.IsSuccessStatusCode, Trim(shoutBody));

            // 先订阅再发第二条：上面那条是订阅之前发出去的，
            // 极可能与轮询的时间窗擦肩而过，用它做断言会偶发失败。
            var consoleShoutReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shoutText2 = $"控制台喊话之二 {Guid.NewGuid():N}"[..24];

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == shoutText2)
                {
                    consoleShoutReceived.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            await adminHttp.PostAsJsonAsync($"{root}/api/console/shout",
                new ConsoleShoutRequest(uuid, shoutText2), JsonOptions);

            var consoleFrom = await Task.WhenAny(consoleShoutReceived.Task, Task.Delay(6000)) == consoleShoutReceived.Task
                ? consoleShoutReceived.Task.Result
                : null;

            Check("控制台喊话经中继送到了教室端", consoleFrom is not null,
                consoleFrom is null ? "6 秒内没收到" : $"来源显示为 {consoleFrom}");

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

            // 没有 audioStart 打头的分片必须被教室端丢弃。
            //
            // 断线重连时服务器会把缓冲区里的历史整段回放，而长时间离线之后，
            // 回放的第一条很可能是一段"半截音频"—— 它的 audioStart 已经被挤出窗口了。
            // 教室里那段数据没有采样率和声道数，播出去是噪声，或者直接让播放器抛异常。
            // 序号是连续的，本来就能识别出这个缺口。
            var before = Volatile.Read(ref audioEnvelopeCount);

            using var strayRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{root}{string.Format(RelayPaths.TeacherAudio, rawBind.Token)}")
            {
                Content = new ByteArrayContent(new byte[3200]),
            };

            var strayResponse = await http.SendAsync(strayRequest);
            await Task.Delay(1200);

            var after = Volatile.Read(ref audioEnvelopeCount);
            Check("没有 audioStart 打头的音频分片被丢弃",
                strayResponse.IsSuccessStatusCode && after == before,
                after == before
                    ? $"服务器转发了（HTTP {(int)strayResponse.StatusCode}），教室端丢弃，音频信封数仍为 {before}"
                    : $"教室端多收了 {after - before} 条裸音频分片");
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
    /// 长轮询的游标契约：不重投、不跳过、since 传 0 不补发历史。
    ///
    /// 这里刻意**不做**并发时序断言。我试过两版：
    ///   1. 每轮新建队列、单轮询者单发布者 —— 锁无竞争，Publish 撞不进那个窗口；
    ///   2. 多轮询者多发布者压同一个队列 —— 期望靠竞争把窗口撑开。
    /// 第二版在开发机上约四成的运行会报"正好等满一个超时周期"，
    /// 但把修复临时退回去之后它**照样能通过** —— 也就是说它既会误报、
    /// 又抓不到它要抓的东西。期间我先后归因于线程池饥饿（改发布者为专属线程）
    /// 与续体调度（抬高线程池下限），两次都被证伪。
    ///
    /// 一个四成概率误报的断言比没有断言更糟：它会训练所有人忽略失败。
    /// 所以那个竞态的保证靠构造本身 —— 检查历史与登记等待者在那把锁里是原子的，
    /// Publish 用同一把锁，在 MessageHub.cs 里一眼可查。
    /// 这里只留下能确定性验证的部分：游标本身的行为。
    /// </summary>
    private static async Task RunMessageQueueConcurrencyAsync()
    {
        var queue = new MessageQueue();

        // ---- 1. since = 0 表示"从此刻开始"，不补发历史 ----
        for (var i = 0; i < 3; i++)
        {
            queue.Publish(new RelayEnvelope { Kind = RelayKinds.TextShout, From = "旧", Text = $"历史 {i}" });
        }

        var fresh = await queue.WaitAsync(0, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Check("since 传 0 时不补发历史", fresh.Events.Count == 0,
            fresh.Events.Count == 0 ? "历史 3 条全部跳过" : $"竟收到 {fresh.Events.Count} 条历史");

        // ---- 2. 逐条发布、逐条收取：序号既不重复也不跳过 ----
        var cursor = fresh.Next;
        var delivered = new List<long>();
        var stuck = false;

        for (var i = 1; i <= 30; i++)
        {
            queue.Publish(new RelayEnvelope { Kind = RelayKinds.TextShout, From = "测试", Text = $"第 {i} 条" });

            // 每条都应当立刻取到，不该等超时
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var batch = await queue.WaitAsync(cursor, TimeSpan.FromMilliseconds(500), CancellationToken.None);
            stopwatch.Stop();

            if (batch.Events.Count == 0)
            {
                stuck = true;
                break;
            }

            foreach (var envelope in batch.Events)
            {
                delivered.Add(envelope.Sequence);
            }

            cursor = batch.Next;
        }

        Check("逐条喊话都能立刻取到（不等超时）", !stuck,
            stuck ? "有一条没能在超时前取到" : "30 条全部即时送达");

        var expected = Enumerable.Range(1, 30).Select(i => (long)(i + 3)).ToList();
        Check("序号不重复、不跳过", delivered.SequenceEqual(expected),
            delivered.Count == expected.Count
                ? $"共 {delivered.Count} 条，序号连续"
                : $"期望 {expected.Count} 条，实际 {delivered.Count} 条");
    }
    /// <summary>
    /// Edge 在线语音的联调。需要外网，所以只在显式加 --edge-tts 时跑。
    ///
    /// 验证三件事：能拿到音色列表（并且有中文音色）、能合成出音频、
    /// 拿到的确实是 MP3（帧同步头 FF Ex）。
    /// 中间任何一环出错都会表现为"教室端不发声"，而那是现场最难查的一类问题，
    /// 所以宁可在这里断言清楚。
    /// </summary>
    private static async Task CheckEdgeTtsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Edge 在线语音（需要外网）");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        using var edge = new EdgeTtsClient(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var voices = await edge.GetVoicesAsync(timeout.Token);
        var chinese = voices.Where(v => v.Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).ToList();

        Check("能拉到 Edge 音色列表", voices.Count > 0, $"共 {voices.Count} 个音色");
        Check("列表里有中文音色", chinese.Count > 0,
            chinese.Count > 0
                ? $"中文音色 {chinese.Count} 个，例如 {chinese[0].ShortName}"
                : "一个中文音色都没有");

        var audio = await edge.SynthesizeAsync(
            "同学们请安静，现在开始上课。", EdgeTtsClient.DefaultVoice, 0, 0, timeout.Token);

        Check("Edge 合成返回了音频", audio.Length > 2000, $"{audio.Length} 字节");

        var isMp3 = audio.Length >= 3 && audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0;
        Check("返回的是 MP3（帧同步头正确）", isMp3,
            audio.Length >= 3 ? $"前 3 字节 {audio[0]:X2} {audio[1]:X2} {audio[2]:X2}" : "数据太短");

        // 解码这一步是 Linux 教室端朗读的命脉：那边没有 SAPI，
        // 在线语音是唯一的朗读引擎，而它只回 MP3。
        // 用 NLayer 解（纯托管）而不是 NAudio 的 Mp3FileReader，
        // 因为后者走 Windows 的 ACM，在 Linux 上根本不工作。
        var decoded = Mp3AudioDecoder.TryDecode(audio);
        Check("MP3 能解码成 PCM", decoded is { Pcm.Length: > 0 },
            decoded is { } d ? $"{d.SampleRate} Hz / {d.Channels} 声道 / {d.BitsPerSample} bit，{d.Pcm.Length} 字节" : "解码失败");

        if (decoded is { } info)
        {
            // 24 kHz 单声道是接口约定；位深统一转成 16 bit 给播放设备
            Check("解码格式符合接口约定", info.SampleRate == 24000 && info.Channels == 1 && info.BitsPerSample == 16,
                $"{info.SampleRate} Hz / {info.Channels} 声道 / {info.BitsPerSample} bit");

            // 一秒 24 kHz 16 bit 单声道 = 48000 字节，用它估算时长是否合理
            var seconds = info.Pcm.Length / (double)(info.SampleRate * info.Channels * 2);
            Check("解出的时长合理（1~30 秒）", seconds is > 1 and < 30,
                $"约 {seconds:F1} 秒");
        }
    }

    /// <summary>
    /// 语音转文字客户端：请求形状与响应解析。
    ///
    /// 不依赖真的 API 密钥 —— 起一个只用 TcpListener 的本地桩服务，
    /// 把请求原样收下来再断言。这样"multipart 字段名对不对、鉴权头有没有、
    /// WAV 头在不在、返回的 {"text":...} 有没有正确解出来"这些
    /// 真正容易写错的地方都被覆盖到了，而且完全确定。
    ///
    /// 刻意不用 HttpListener：它在 Windows 上要预先注册 URL ACL，
    /// 非管理员跑不起来，而回归脚本不该要求提权。
    /// </summary>
    private static async Task AssertSttClientAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const string expected = "同学们请安静";
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();

            // 先读到头部结束，再按 Content-Length 读完 body
            var buffer = new byte[64 * 1024];
            var collected = new MemoryStream();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) { break; }
                collected.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(collected.ToArray());
            }

            var head = Encoding.ASCII.GetString(collected.ToArray(), 0, Math.Max(headerEnd, 0));
            var contentLength = 0;
            foreach (var line in head.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line[15..].Trim(), out contentLength);
                }
            }

            while (collected.Length < headerEnd + 4 + contentLength)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) { break; }
                collected.Write(buffer, 0, read);
            }

            var body = "{\"text\":\"" + expected + "\"}";
            var response = "HTTP/1.1 200 OK\r\n"
                         + "Content-Type: application/json\r\n"
                         + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
                         + "Connection: close\r\n\r\n" + body;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));

            return collected.ToArray();
        });

        const int sampleCount = 16000; // 1 秒 16 kHz 单声道 16 bit
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i % 251);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var client2 = new SttClient(http);

        var settings = new SttSettings
        {
            Enabled = true,
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            ApiKey = "test-key-123",
            Model = "whisper-1",
            Language = "zh",
        };

        var result = await client2.TranscribeAsync(pcm, AudioFormat.Default, settings);
        var raw = await server;
        listener.Stop();

        // 在**原始字节**上搜 ASCII，而不是把整个请求按 UTF-8 解码。
        // 请求体里有二进制 WAV 数据，整体解码会遇到非法的多字节序列，
        // 解码器替换它们时可能连后面的字节一起吃掉 ——
        // 于是"name=\"model\" 明明在请求里"却断言不出来（这个坑我踩过一次）。
        bool Has(string needle) => ContainsAscii(raw, needle);

        Check("转写请求成功并解析出文字", result.Ok && result.Text == expected,
            result.Ok ? $"识别结果={result.Text}" : result.Error ?? "失败");

        Check("请求打到 /v1/audio/transcriptions", Has("POST /v1/audio/transcriptions"),
            "POST /v1/audio/transcriptions");

        Check("带上 Bearer 鉴权头", Has("Bearer test-key-123"), "Authorization 头存在");

        // 字段名不带引号也合法：.NET 只在名字含特殊字符时才给 Content-Disposition
        // 的 name 加引号，所以这里两种形式都要认 —— 要断言的是"字段在不在"，
        // 而不是某个库的引号习惯。
        Check("multipart 里含 model 与 language 字段",
            (Has("name=model") || Has("name=\"model\""))
            && Has("whisper-1")
            && (Has("name=language") || Has("name=\"language\"")),
            "model=whisper-1，language=zh");

        Check("上传的音频带正确的 WAV 头",
            Has("RIFF") && Has("WAVE") && Has("audio/wav"),
            "RIFF/WAVE 与 audio/wav 都在请求里");

        Check("未配置密钥时给出可读的提示而不是抛异常",
            !(await client2.TranscribeAsync(pcm, AudioFormat.Default,
                new SttSettings { Enabled = true, ApiKey = null })).Ok,
            "缺密钥时返回失败结果");
    }

    /// <summary>
    /// 发送队列：按入队先后一条一条发，位置问得准，失败不阻塞。
    ///
    /// "按发出时间排队"这条需求的核心就是顺序，所以断言直接盯住顺序本身，
    /// 以及界面要显示的那个"前面还有几个"。全确定性，不依赖网络。
    /// </summary>
    private static async Task AssertShoutQueueAsync()
    {
        using var queue = new ShoutQueue();

        var started = new List<string>();
        var finished = new List<string>();
        var positions = new Dictionary<string, int>();

        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Sent += (text, ok) => finished.Add(text + (ok ? "" : "(失败)"));

        // 票据先声明再在闭包里用：闭包是稍后才执行的，那时赋值已经完成。
        long firstTicket = 0;
        long secondTicket = 0;
        long thirdTicket = 0;

        // 第一条故意卡住，好让后面两条在队列里等着，从而问得到位置
        firstTicket = queue.Enqueue("第一条", async _ =>
        {
            started.Add("第一条");
            positions["第一条"] = queue.PositionOf(firstTicket);
            await firstGate.Task;
            return true;
        });

        secondTicket = queue.Enqueue("第二条", _ =>
        {
            started.Add("第二条");
            positions["第二条"] = queue.PositionOf(secondTicket);
            return Task.FromResult(true);
        });

        thirdTicket = queue.Enqueue("第三条", _ =>
        {
            started.Add("第三条");
            // 这一条故意失败，用来验证失败不会卡住队列
            return Task.FromResult(false);
        });

        // 入队后立刻问位置：第二条前面有 1 条（第一条在发），第三条前面有 2 条
        var secondAheadWhileQueued = queue.PositionOf(secondTicket);
        var thirdAheadWhileQueued = queue.PositionOf(thirdTicket);

        Check("排队位置问得准（前面还有几个）",
            secondAheadWhileQueued == 1 && thirdAheadWhileQueued == 2,
            $"第二条前面 {secondAheadWhileQueued} 条，第三条前面 {thirdAheadWhileQueued} 条");

        // 放行第一条，等队列跑完
        firstGate.SetResult();
        await WaitUntilAsync(() => finished.Count == 3, TimeSpan.FromSeconds(10));

        Check("严格按发出时间先入先出",
            started.SequenceEqual(["第一条", "第二条", "第三条"]),
            string.Join(" → ", started));

        Check("轮到自己时前面就已经没人了",
            positions.GetValueOrDefault("第一条") == 0,
            $"第一条开始发送时前面 {positions.GetValueOrDefault("第一条")} 条");

        Check("一条失败不会卡住后面的", finished.Count == 3, string.Join("、", finished));

        Check("队列发空之后计数归零", queue.PendingCount == 0, $"{queue.PendingCount} 条");
    }

    /// <summary>在字节数组里找一段 ASCII 子串。二进制体不能按 UTF-8 解码后再搜。</summary>
    private static bool ContainsAscii(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);

        for (var i = 0; i + needle.Length <= data.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (var i = 3; i < data.Length; i++)
        {
            if (data[i - 3] == 13 && data[i - 2] == 10 && data[i - 1] == 13 && data[i] == 10)
            {
                return i - 3;
            }
        }

        return -1;
    }

    /// <summary>
    /// 教室端的设置锁：可选、默认关闭、只认数字、不以明文落盘。
    ///
    /// 会临时改写本机的 settings-lock.json，所以先把原文件收好、跑完还回去 ——
    /// 端到端工具跑在开发机上，不能把使用者自己设的 PIN 弄丢。
    /// </summary>
    private static void AssertSettingsLock()
    {
        var path = Path.Combine(LocalSettings.Directory, "settings-lock.json");
        var hadFile = File.Exists(path);
        var backup = hadFile ? File.ReadAllText(path) : null;

        try
        {
            SettingsLock.Disable();
            Check("设置锁默认关闭时不拦人", SettingsLock.Verify("0000"),
                "关闭状态下任意输入都放行 —— 这是可选功能，关着的时候不该拦人");

            Check("过短的 PIN 被拒", !SettingsLock.IsWellFormed("123", out _),
                $"至少 {SettingsLock.MinPinLength} 位");
            Check("含字母的 PIN 被拒", !SettingsLock.IsWellFormed("12a4", out _),
                "只允许数字：教室电脑多半是触屏或数字键盘");

            var (setOk, setError) = SettingsLock.SetPin("2468");
            Check("设置 PIN 成功", setOk, setError ?? "已启用");
            Check("启用后正确 PIN 通过", SettingsLock.Verify("2468"), "2468");
            Check("启用后错误 PIN 被拒", !SettingsLock.Verify("2469"), "2469 被拒");
            Check("启用后空 PIN 被拒", !SettingsLock.Verify(string.Empty), "空输入被拒");

            var onDisk = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            Check("PIN 不以明文落盘", !onDisk.Contains("2468"),
                "文件里存的是 PBKDF2 派生值与随机盐");

            SettingsLock.Disable();
            Check("关闭之后不再拦人", SettingsLock.Verify("0000"), "已恢复为未启用");
        }
        finally
        {
            if (hadFile && backup is not null)
            {
                File.WriteAllText(path, backup);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 本机喊话记录：最多留二十条，最新在前，最旧的被挤掉。
    ///
    /// 只压 ShoutHistory.Append 这个纯函数，不碰 LocalSettings ——
    /// 端到端工具跑在开发机上，走磁盘就会把使用者真实的喊话记录覆盖掉。
    /// 条数裁剪这条规则与"存哪儿"无关，纯函数测清楚就够了。
    /// </summary>
    private static void AssertShoutHistoryCap()
    {
        var list = new List<ShoutRecord>();

        for (var i = 1; i <= 25; i++)
        {
            list = ShoutHistory.Append(list, new ShoutRecord($"第 {i} 条", DateTimeOffset.Now, IsVoice: false));
        }

        Check("喊话记录最多保留 20 条", list.Count == ShoutHistory.MaxCount,
            $"写入 25 条后剩 {list.Count} 条（上限 {ShoutHistory.MaxCount}）");

        Check("最新的一条排在最前", list[0].Text == "第 25 条", $"首条={list[0].Text}");

        Check("超出上限后从最旧一端丢弃", list[^1].Text == "第 6 条",
            $"末条={list[^1].Text}（第 1~5 条应已丢弃）");
    }

    /// <summary>
    /// 畸形的 audioStart 不能打崩教室端。
    ///
    /// 采样率、声道数、位深都来自网络对端。NAudio 的 WaveFormat 构造函数会校验
    /// 并在不合法时抛异常，而构造点跑在 UI 线程上（收到 audioStart 后要立刻建播放器）——
    /// 对端只要发一个 Channels = 0，整个教室端进程就没了。
    /// 一个畸形包换一次服务中断，代价完全不对等。
    ///
    /// 这里直接压两层防线：格式自身要能识别出不合法，播放器要抛可捕获的
    /// NotSupportedException 而不是让 NAudio 的 ArgumentException 逃出去。
    /// </summary>
    private static void AssertMalformedAudioFormatRejected()
    {
        var malformed = new[]
        {
            new AudioFormat(16000, 0, 16),      // 声道数为 0
            new AudioFormat(16000, -1, 16),     // 声道数为负
            new AudioFormat(0, 1, 16),          // 采样率为 0
            new AudioFormat(16000, 1, 0),       // 位深为 0
            new AudioFormat(1_000_000, 1, 16),  // 采样率离谱
        };

        Check("合法格式被接受", AudioFormat.Default.IsSupported, AudioFormat.Default.ToString());
        Check("畸形格式一律识别为不支持",
            malformed.All(f => !f.IsSupported),
            string.Join("，", malformed.Select(f => f.ToString())));

        using var player = new NAudioLoopbackPlayer();
        var rejected = true;

        foreach (var format in malformed)
        {
            try
            {
                player.Start(format);
                rejected = false;
            }
            catch (NotSupportedException)
            {
                // 期望路径
            }
            catch (Exception ex)
            {
                rejected = false;
                Console.WriteLine($"       · 意外异常类型：{ex.GetType().Name}");
            }
        }

        Check("播放器拒绝畸形格式而不是崩溃", rejected, "抛的是可捕获的 NotSupportedException");
    }

    /// <summary>
    /// 用一个"版本号对不上"的裸连接去喊话，断言教室端一个字都不收。
    ///
    /// 不用 TeacherClient：它把协议版本写死在 ConnectAsync 里，这里要的正是错版本。
    /// </summary>
    private static async Task AssertVersionMismatchRejectedAsync(List<string> receivedTexts, string baseline)
    {
        int before;
        lock (receivedTexts)
        {
            before = receivedTexts.Count;
        }

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, TcpPort);

        var stream = raw.GetStream();

        await FrameProtocol.WriteAsync(stream, FrameKind.Control, ShoutCodec.Encode(new HelloMessage
        {
            ClientId = Guid.NewGuid().ToString("N"),
            ClientName = "版本不符的教师端",
            ProtocolVersion = ShoutProtocol.Version + 1,
        }), CancellationToken.None);

        await FrameProtocol.WriteAsync(stream, FrameKind.Control, ShoutCodec.Encode(new TextShoutMessage
        {
            Text = "这条不该被任何教室收到",
            Rate = 1,
            Volume = 80,
        }), CancellationToken.None);

        await Task.Delay(800);

        int after;
        lock (receivedTexts)
        {
            after = receivedTexts.Count;
        }

        Check("协议版本不符的教师端被拒收（不会喊进教室）", after == before,
            after == before
                ? $"版本 {ShoutProtocol.Version + 1} 的喊话被丢弃，教室端仍只有 {before} 条"
                : $"教室端多收了 {after - before} 条，版本校验没生效");

        // 顺带确认基线没被这条测试本身搞乱
        Check("版本不符的连接没有污染已有喊话", baseline == receivedTexts[0], "首条内容未变");
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
