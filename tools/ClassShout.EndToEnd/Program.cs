using System.Linq;
using System.Text;
using System.IO.Compression;
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
using ClassShout.Teacher.ViewModels;

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
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            // 回归工具自己崩掉时，最要紧的是把那句话打出来。
            //
            // 不包这一层的话，进程直接以 -532462766（未处理异常）退出，
            // 而输出里只有一个退出码 —— 真正的原因（哪一条断言在等什么超时）
            // 全都丢了，偏偏这类失败又往往是偶发的、不复现的。
            Console.WriteLine();
            Console.WriteLine($"回归工具自身出错：{ex.GetType().Name}：{ex.Message}");
            Console.WriteLine(ex.StackTrace);

            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
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
        var imageStarted = new TaskCompletionSource<ImageStartMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageEnded = new TaskCompletionSource<ImageEndMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedImageParts = new List<byte[]>();

        server.SessionOpened += (_, session) =>
        {
            session.TextShoutReceived += (_, message) => { lock (receivedTexts) { receivedTexts.Add(message.Text); } };
            session.AudioStarted += (_, message) => audioStarted.TrySetResult(message);
            session.AudioChunkReceived += (_, payload) => { lock (receivedChunks) { receivedChunks.Add(payload.ToArray()); } };
            session.AudioEnded += (_, _) => audioEnded.TrySetResult();
            session.StopRequested += (_, message) => stopReceived.TrySetResult(message.Reason);

            session.ImageStarted += (_, message) => imageStarted.TrySetResult(message);
            session.ImageChunkReceived += (_, payload) =>
            {
                lock (receivedImageParts)
                {
                    receivedImageParts.Add(payload.ToArray());
                }
            };
            session.ImageEnded += (_, message) => imageEnded.TrySetResult(message);

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

        // 连接地址必须取**回包的来源地址**，而不是教室端自己在包里写的 Host：
        // 装了 WSL / Docker / VPN 的机器上，教室端挑出来的那个地址常常是虚拟网卡的，
        // 别的设备根本连不上 —— 表现成"搜得到、连不上"，而两端都看不出原因。
        Check("连接地址取回包的来源地址（不是教室端自报的 Host）",
            found.Count > 0 && found[0].Host is "127.0.0.1" or "::1",
            found.Count > 0 ? $"Host={found[0].Host}" : "无结果");

        // 探测目标里要有本机地址与回环：教室端与教师端跑在同一台机器上时
        // （用桌面端当教师端就是这么用的），广播可能被 VPN / WSL 的默认路由吞掉。
        var targets = NetworkUtility.DiscoveryTargets();

        Check("广播探测也发给 127.0.0.1（同一台机器上的教室端一定收得到）",
            targets.Contains(IPAddress.Loopback),
            string.Join("、", targets.Select(t => t.ToString())));

        Check("广播探测包含各网段的广播地址",
            targets.Contains(IPAddress.Broadcast) && targets.Count >= 3,
            $"共 {targets.Count} 个目标");

        // —— 教室端该报哪个地址：装了 WSL / Docker / VPN 的机器上很容易报错 ——
        var realLan = NetworkUtility.Score(
            "以太网", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.5", hasGateway: true);
        var wslAdapter = NetworkUtility.Score(
            "vEthernet (WSL)", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.28.0.1", hasGateway: true);

        Check("地址打分：有网关的真实网卡排在虚拟网卡前面",
            realLan > wslAdapter,
            $"以太网 {realLan} 分 > vEthernet (WSL) {wslAdapter} 分");

        Check("地址打分：没有网关的真网卡也比虚拟网卡强（回到只有一张网卡的机器上也对）",
            NetworkUtility.Score(
                "以太网", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.5", hasGateway: false)
            > wslAdapter,
            "物理网卡 50 分 > 虚拟网卡负分");

        Check("地址打分：没拿到 DHCP 的 169.254 地址会被压下去",
            NetworkUtility.Score(
                "Wi-Fi", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "169.254.10.20", hasGateway: false)
            < NetworkUtility.Score(
                "Wi-Fi", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "10.0.0.20", hasGateway: false),
            "169.254 的排名低于正常地址");

        Check("名字像虚拟网卡的能被认出来",
            NetworkUtility.LooksVirtual("vEthernet (WSL)")
            && NetworkUtility.LooksVirtual("Docker Desktop")
            && NetworkUtility.LooksVirtual("Tailscale")
            && !NetworkUtility.LooksVirtual("以太网")
            && !NetworkUtility.LooksVirtual("Wi-Fi"),
            "vEthernet / Docker / Tailscale 认得出，以太网与 Wi-Fi 不误判");

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

        // ---------- 3g. 投给 ClassIsland 的那条通知 ----------
        await AssertClassIslandNoticeAsync();

        // ---------- 3h. 一次喊话的展示参数 ----------
        AssertShoutDisplayPlan();

        // ---------- 3i. 定时通知 ----------
        await AssertSchedulerAsync();

        // ---------- 3j. 分享链接的解析 ----------
        AssertShareLinkParsing();

        // ---------- 3k. 学生名单 ----------
        AssertRosterParsing();

        // ---------- 3l. 组件式呼叫的拼装 ----------
        AssertCallComposer();

        // ---------- 3m. 表格（Excel）导入 ----------
        AssertRosterExcelImport();

        // ---------- 3m2. 随机叫人与时间因子 ----------
        AssertRandomCall();

        // ---------- 3n. 常用语的自定义 ----------
        AssertPhraseSettings();

        // ---------- 3o. 按班级的任教科目 ----------
        AssertTeachingSubjects();

        // ---------- 3p. WAV 编解码（定时语音靠它落盘） ----------
        AssertWavCodec();

        // ---------- 3q. 定时模型的摘要与形态 ----------
        AssertScheduledShoutModel();

        // ---------- 3r. 检查更新与镜像源 ----------
        await AssertUpdateCheckerAsync();

        // ---------- 3s. 开发者模式的连点手势 ----------
        AssertDeveloperTapGesture();

        // ---------- 3t. 日志按天分文件、只留 7 天 ----------
        AssertLogRetention();
        AssertLogLevels();
        AssertShoutTargetCard();
        await AssertVoiceTimerResetsAsync();

        // ---------- 3n. 发送队列 ----------
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

        // ---------- 4b. 图片喊话 ----------
        //
        // 图片走的是"先声明、再分片、最后收尾"那条路，所以这里要压三件事：
        // 声明里的字节数对不对、分片是不是按声明的大小切、以及拼回来是不是原图。
        // 字节刻意用不可压缩的随机数据：用全 0 的话，"拼错了"也可能看起来是对的。
        var imageBytes = new byte[150 * 1024];
        for (var i = 0; i < imageBytes.Length; i++)
        {
            imageBytes[i] = (byte)((i * 31 + 7) % 251);
        }

        await client.SendImageAsync(
            new ImageStartMessage { Text = "看这张图", FontSize = ShoutFontSizes.Large, HoldMs = ShoutHoldDurations.OneMinute },
            imageBytes);

        var imageEnd = await imageEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var imageStart = await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Check("教室端收到图片声明，并带上说明文字与总字节数",
            imageStart.Text == "看这张图" && imageStart.TotalBytes == imageBytes.Length,
            $"说明=「{imageStart.Text}」，声明 {imageStart.TotalBytes} 字节");

        Check("图片的展示参数跟着声明一起到达",
            imageStart.FontSize == ShoutFontSizes.Large && imageStart.HoldMs == ShoutHoldDurations.OneMinute,
            $"字号={imageStart.FontSize}，停留={imageStart.HoldMs}ms");

        byte[][] imageParts;
        lock (receivedImageParts)
        {
            imageParts = receivedImageParts.ToArray();
        }

        var expectedParts = (imageBytes.Length + ShoutProtocol.DefaultImageChunkSize - 1) / ShoutProtocol.DefaultImageChunkSize;

        Check("图片按声明的大小切片",
            imageParts.Length == expectedParts,
            $"发出 {imageBytes.Length} 字节，切成 {imageParts.Length} 片（期望 {expectedParts} 片）");

        var joined = new byte[imageParts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in imageParts)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }

        Check("拼回来的图片与发出的一致",
            joined.Length == imageBytes.Length && joined.AsSpan().SequenceEqual(imageBytes),
            $"{joined.Length} 字节" + (joined.Length == imageBytes.Length ? "，逐字节相同" : "，长度不符"));

        Check("图片的收尾消息带回同一个 id", imageEnd.Id == imageStart.Id,
            $"id={(imageEnd.Id.Length > 8 ? imageEnd.Id[..8] + "…" : imageEnd.Id)}");

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

        var (regOk, regError) = await account.RegisterAsync(accountName, null, "张老师", accountPassword, subject: "数学");
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

        // ---------- 3b. 绑定后存下来的那间教室 ----------
        //
        // 老师教好几个班，"切过去就能喊"靠的就是这条记录：教室名、服务器地址、口令。
        // 少了任何一项，切换时都得重新找管理员要一遍信息。
        var saved = teacherSettings.RecentClassrooms;
        Check("绑定后记下了这间教室", saved.Count == 1 && saved[0].Uuid == uuid,
            saved.Count == 0 ? "列表是空的" : $"共 {saved.Count} 条");

        Check("记录里带着口令（否则下次切换要重新找管理员要）",
            saved.Count > 0 && saved[0].Secret == secret,
            saved.Count == 0 ? "无记录" : (string.IsNullOrEmpty(saved[0].Secret) ? "没有口令" : "口令已存"));

        Check("记录里带着服务器地址（换服务器后仍能切回来）",
            saved.Count > 0 && saved[0].ServerUrl == root,
            saved.Count == 0 ? "无记录" : $"地址={saved[0].ServerUrl}");

        Check("记录里带着教室名", saved.Count > 0 && !string.IsNullOrWhiteSpace(saved[0].Name),
            saved.Count == 0 ? "无记录" : $"教室名={saved[0].Name}");

        // 再绑一次同一间：不能变成两条
        await teacher.BindAsync(uuid, secret, "张老师");
        Check("重复绑定同一间教室不会在列表里出现两条",
            teacherSettings.RecentClassrooms.Count == 1,
            $"共 {teacherSettings.RecentClassrooms.Count} 条");

        AssertSavedClassroomList();

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
            text.From == "数学张老师",
            $"From={text.From}");

        Check("来源里带上了任教科目（同一间教室一天里有好几位老师来喊）",
            text.From?.StartsWith("数学", StringComparison.Ordinal) == true,
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

            // 手动创建账号（顺手填上任教科目，与管理员开学时录账号的做法一致）
            var createdName = "ctl" + Guid.NewGuid().ToString("N")[..8];
            var createResponse = await adminHttp.PostAsJsonAsync($"{root}/api/console/users",
                new CreateUserRequest(createdName, null, "控制台创建的张老师", "Init12345", Subject: "数学"), JsonOptions);
            var createBody = await createResponse.Content.ReadAsStringAsync();
            Check("控制台能手动创建账号", createResponse.IsSuccessStatusCode, Trim(createBody));

            // 新建的账号应当立刻出现在用户列表里
            var afterCreate = await adminHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [];
            var createdProfile = afterCreate.FirstOrDefault(u => u.Username == createdName);

            Check("新建的账号出现在用户列表里",
                createdProfile is not null,
                $"列表共 {afterCreate.Count} 个账号");

            // 控制台建号时填的科目要真的落到账号上：它会拼在喊话来源前面，
            // 而"界面上有个输入框、后台却把它丢了"正是最容易发生的一种漏
            Check("控制台建号时填的任教科目存下来了",
                createdProfile?.Subject == "数学",
                createdProfile is null ? "账号没建出来" : $"科目={createdProfile.Subject ?? "(空)"}");

            // CSV 批量导入：两行合法（其中一行的姓名里带逗号、按 Excel 的习惯套着引号），
            // 外加列数不足、重名、与管理员冲突各一行
            var csvUserA = $"csvok{Guid.NewGuid():N}"[..12];
            var csvUserB = $"csvqt{Guid.NewGuid():N}"[..12];

            var importCsv = string.Join('\n',
                "用户名,邮箱,姓名,口令,任教科目",
                $"{csvUserA},,CSV 老师甲,Init12345,语文",
                $"{csvUserB},,\"CSV, 老师乙\",Init12345,数学",
                "只有一列",
                $"{createdName},,重复账号,Init12345",
                "admin,,冒名管理员,Init12345");

            var importResponse = await adminHttp.PostAsJsonAsync($"{root}/api/console/users/import",
                new ImportUsersRequest(importCsv), JsonOptions);
            var import = await importResponse.Content.ReadFromJsonAsync<ImportUsersResponse>(JsonOptions);

            Check("CSV 批量导入逐行处理（成功的建成、失败的不拖累其他行）",
                import is { Created: 2, Failed: 3 },
                import is null ? "解析失败" : $"成功 {import.Created} 条、失败 {import.Failed} 条");

            Check("CSV 导入会挡掉与内置管理员重名的行",
                import?.Details.Any(d => d.Contains("内置管理员")) == true,
                import?.Details.LastOrDefault(d => d.Contains("内置管理员")) ?? "没有相关说明");

            var afterImport = await adminHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [];

            Check("CSV 导入的第 5 列被当成任教科目",
                afterImport.FirstOrDefault(u => u.Username == csvUserA)?.Subject == "语文",
                afterImport.FirstOrDefault(u => u.Username == csvUserA)?.Subject ?? "(空)");

            // Excel 遇到字段里有逗号会自己加引号。硬切的话这一行会从引号里的
            // 那个逗号裂开：姓名只剩前半截，科目挪到第 5 列之外直接丢掉 ——
            // 而且是静默的，管理员只会发现"有几个人的科目没录上"。
            var quotedProfile = afterImport.FirstOrDefault(u => u.Username == csvUserB);

            Check("CSV 里带引号的字段不会被逗号切开",
                quotedProfile?.DisplayName == "CSV, 老师乙" && quotedProfile.Subject == "数学",
                quotedProfile is null
                    ? "账号没建出来"
                    : $"姓名=「{quotedProfile.DisplayName}」科目={quotedProfile.Subject ?? "(空)"}");

            // 补填任教科目：注册时它是选填的，老师随手留空之后，教师端没有"改资料"
            // 这一页、控制台原来也没有这个动作 —— 于是喊话来源里永远只有姓名。
            var teacherProfile = afterImport.FirstOrDefault(u => u.Username == accountName);

            Check("控制台上能找到刚注册的那位老师",
                teacherProfile is not null,
                teacherProfile is null ? "没找到" : $"id={teacherProfile.Id}");

            var setSubjectResponse = await adminHttp.PostAsJsonAsync(
                $"{root}/api/console/users/{teacherProfile?.Id}/subject",
                new ConsoleSubjectRequest("化学"),
                JsonOptions);

            Check("控制台能补填任教科目",
                setSubjectResponse.IsSuccessStatusCode,
                Trim(await setSubjectResponse.Content.ReadAsStringAsync()));

            var afterSubject = await adminHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [];

            Check("补填的科目进了用户列表",
                afterSubject.FirstOrDefault(u => u.Username == accountName)?.Subject == "化学",
                afterSubject.FirstOrDefault(u => u.Username == accountName)?.Subject ?? "(空)");

            // 改完立刻影响喊话来源 —— 这才是这个动作存在的全部意义：
            // 名单上补两个字，教室里从此听得出是谁在说话。
            var subjectShoutText = $"改科目后的喊话 {Guid.NewGuid():N}"[..26];
            var subjectShoutReceived = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == subjectShoutText)
                {
                    subjectShoutReceived.TrySetResult(envelope);
                }
            };

            await teacher.SendTextAsync(subjectShoutText, 1, 90, interrupt: true);

            var subjectShout = await Task.WhenAny(subjectShoutReceived.Task, Task.Delay(15000)) == subjectShoutReceived.Task
                ? subjectShoutReceived.Task.Result
                : null;

            Check("改完科目后喊话来源立刻变成「化学张老师」",
                subjectShout?.From == "化学张老师",
                $"From={subjectShout?.From ?? "(15 秒内没收到)"}");

            // 留空即清掉：来源回到只报姓名，而不是留下一个空的「张老师」前缀
            await adminHttp.PostAsJsonAsync(
                $"{root}/api/console/users/{teacherProfile?.Id}/subject",
                new ConsoleSubjectRequest("   "),
                JsonOptions);

            var afterClear = await adminHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [];

            Check("科目留空即清掉（不会留下空白科目）",
                afterClear.FirstOrDefault(u => u.Username == accountName)?.Subject is null,
                afterClear.FirstOrDefault(u => u.Username == accountName)?.Subject ?? "(已清空)");

            var adminSubjectResponse = await adminHttp.PostAsJsonAsync(
                $"{root}/api/console/users/builtin-admin/subject",
                new ConsoleSubjectRequest("数学"),
                JsonOptions);

            Check("内置管理员没有任教科目可言（会被明确拒绝）",
                (int)adminSubjectResponse.StatusCode == 400,
                $"HTTP {(int)adminSubjectResponse.StatusCode}");

            var missingSubjectResponse = await adminHttp.PostAsJsonAsync(
                $"{root}/api/console/users/nonexistent-id/subject",
                new ConsoleSubjectRequest("数学"),
                JsonOptions);

            Check("给不存在的账号改科目返回 404（而不是假装成功）",
                (int)missingSubjectResponse.StatusCode == 404,
                $"HTTP {(int)missingSubjectResponse.StatusCode}");

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

        // ---------- 8b. 一次发给多个班级 ----------
        //
        // 老师教好几个班时，最常要的一句话是"两个班都通知一下"。
        // 这里压的就是那条路：逐个经中继绑定并发送，两边都要真的收到。
        var firstTexts = new List<string>();
        classroom.ShoutReceived += envelope =>
        {
            if (envelope.Kind == RelayKinds.TextShout)
            {
                firstTexts.Add(envelope.Text ?? string.Empty);
            }
        };

        var otherTexts = new List<string>();
        otherClassroom.ShoutReceived += envelope =>
        {
            if (envelope.Kind == RelayKinds.TextShout)
            {
                otherTexts.Add(envelope.Text ?? string.Empty);
            }
        };

        var multiTargets = new List<BoundClassroom>
        {
            new(uuid, "中继测试教室", DateTimeOffset.UtcNow, root, secret),
            new(otherSettings.Uuid, "隔壁班", DateTimeOffset.UtcNow, root, otherSettings.Secret),
        };

        const string multiText = "两个班都通知一下：明天带实验报告。";

        var broadcaster = new ClassroomBroadcaster(http, teacherSettings);
        var multiResults = await broadcaster.SendTextAsync(
            multiTargets,
            new TextShoutMessage { Text = multiText, Rate = 1, Volume = 90 },
            _ => "数学张老师");

        await Task.Delay(2500);

        Check("多班喊话：两间都报告发送成功",
            multiResults.Count == 2 && multiResults.All(r => r.Ok),
            string.Join("、", multiResults.Select(r => $"{r.Classroom.Name}={(r.Ok ? "成功" : r.Error)}")));

        Check("多班喊话：第一间收到了",
            firstTexts.Contains(multiText),
            firstTexts.Count == 0 ? "第一间一条都没收到" : $"第一间收到 {firstTexts.Count} 条");

        Check("多班喊话：第二间也收到了",
            otherTexts.Contains(multiText),
            otherTexts.Count == 0 ? "第二间一条都没收到" : $"第二间收到 {otherTexts.Count} 条");

        // 教室名要能对上：结果里报的是哪一间，老师看到的就该是哪一间
        Check("多班喊话的结果带上教室名（失败时能说清是哪一间）",
            multiResults.All(r => !string.IsNullOrWhiteSpace(r.Classroom.Name)),
            string.Join("、", multiResults.Select(r => r.Classroom.Name)));

        await broadcaster.ResetAsync();

        // ---------- 8b. 按班级的任教科目 ----------
        //
        // 一位老师在不同班教不同科目：信息技术老师给一个班上信息课、顺手给另一个班带数学。
        // 名字是服务器贴的（客户端冒充不了），所以"哪个班用哪个科目"也必须在服务器上算对 ——
        // 算错的话教室里看到的科目就永远是错的那一半，而界面上什么都看不出来。
        if (!string.IsNullOrEmpty(adminToken))
        {
            using var subjectHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            subjectHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            var accountProfile = (await subjectHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(u => u.Username == accountName);

            var subjectResponse = await subjectHttp.PostAsJsonAsync(
                $"{root}/api/console/users/{accountProfile?.Id}/subject",
                new ConsoleSubjectRequest("数学", new Dictionary<string, string?>
                {
                    [uuid] = "化学",
                    [otherSettings.Uuid] = "物理",
                }),
                JsonOptions);

            Check("控制台能按班级分别指定科目",
                subjectResponse.IsSuccessStatusCode,
                Trim(await subjectResponse.Content.ReadAsStringAsync()));

            // 两间教室各收一条，看它们各自看到的来源
            var perClassText = $"按班级科目 {Guid.NewGuid():N}"[..22];
            var firstFrom = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondFrom = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == perClassText)
                {
                    firstFrom.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            otherClassroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == perClassText)
                {
                    secondFrom.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            var perClassBroadcaster = new ClassroomBroadcaster(http, teacherSettings);
            await perClassBroadcaster.SendTextAsync(
                multiTargets,
                new TextShoutMessage { Text = perClassText, Rate = 1, Volume = 90 },
                _ => "这个名字应当被服务器覆盖");

            var firstSeen = await Task.WhenAny(firstFrom.Task, Task.Delay(8000)) == firstFrom.Task
                ? firstFrom.Task.Result
                : null;

            var secondSeen = await Task.WhenAny(secondFrom.Task, Task.Delay(8000)) == secondFrom.Task
                ? secondFrom.Task.Result
                : null;

            Check("同一个班用的是它自己那份科目",
                firstSeen == "化学张老师",
                $"第一间看到的是 {firstSeen ?? "(没收到)"}");

            Check("同一个老师的另一间教室用的是另一份科目",
                secondSeen == "物理张老师",
                $"第二间看到的是 {secondSeen ?? "(没收到)"}");

            await perClassBroadcaster.ResetAsync();

            // 没单独指定的班级回落到默认科目
            await subjectHttp.PostAsJsonAsync(
                $"{root}/api/console/users/{accountProfile?.Id}/subject",
                new ConsoleSubjectRequest("数学", new Dictionary<string, string?>()),
                JsonOptions);

            var fallbackText = $"默认科目 {Guid.NewGuid():N}"[..20];
            var fallbackFrom = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == fallbackText)
                {
                    fallbackFrom.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            var fallbackBroadcaster = new ClassroomBroadcaster(http, teacherSettings);
            await fallbackBroadcaster.SendTextAsync(
                [multiTargets[0]],
                new TextShoutMessage { Text = fallbackText, Rate = 1, Volume = 90 },
                _ => "ignored");

            var fallbackSeen = await Task.WhenAny(fallbackFrom.Task, Task.Delay(8000)) == fallbackFrom.Task
                ? fallbackFrom.Task.Result
                : null;

            Check("没单独指定的班级回落到默认科目",
                fallbackSeen == "数学张老师",
                $"看到的是 {fallbackSeen ?? "(没收到)"}");

            await fallbackBroadcaster.ResetAsync();
        }

        // ---------- 8c. 老师从网页给自己的班喊话 ----------
        //
        // 老师不一定装着 App、也不一定带着手机 —— 站在教室那台电脑前打开网页就能喊一句。
        // 权限必须与 App 完全一致：只喊得了管理员授权给自己的班级，
        // 否则"网页"就成了绕过授权的一条侧门。
        if (!string.IsNullOrEmpty(adminToken))
        {
            using var webHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            webHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, teacherSettings.AuthToken);

            // 此刻老师对 uuid 的授权已经被上一段撤掉了 → 列表里应当是空的
            var beforeGrant = await webHttp.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}", JsonOptions) ?? [];

            Check("老师登进网页时看不到任何未授权的班级",
                beforeGrant.Count == 0,
                $"看到 {beforeGrant.Count} 个");

            var noTarget = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherShout}",
                new TeacherWebShoutRequest([], "空目标"),
                JsonOptions);

            Check("网页喊话：没勾班级会被拒",
                (int)noTarget.StatusCode == 400,
                $"HTTP {(int)noTarget.StatusCode}");

            var emptyText = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherShout}",
                new TeacherWebShoutRequest([uuid], "   "),
                JsonOptions);

            Check("网页喊话：空内容会被拒",
                (int)emptyText.StatusCode == 400,
                $"HTTP {(int)emptyText.StatusCode}");

            // 未授权就往里发：要明确拒绝，而不是发出去
            var forbiddenText = $"越权的网页喊话 {Guid.NewGuid():N}"[..24];
            var forbiddenArrived = false;

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == forbiddenText)
                {
                    forbiddenArrived = true;
                }
            };

            var forbidden = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherShout}",
                new TeacherWebShoutRequest([uuid], forbiddenText),
                JsonOptions);

            var forbiddenBody = await forbidden.Content.ReadFromJsonAsync<TeacherWebShoutResponse>(JsonOptions);

            await Task.Delay(1500);

            Check("网页喊话：没授权的班级发不出去，并说明原因",
                forbiddenBody is { Ok: false, Sent: 0 } &&
                forbiddenBody.Results.Any(r => r.Error?.Contains("没有授权") == true),
                forbiddenBody is null ? "没有结果" : string.Join("；", forbiddenBody.Results.Select(r => r.Error ?? "ok")));

            Check("网页喊话：越权的那条确实没有进教室", !forbiddenArrived, "教室里没有出现这条");

            // 授权之后再来一次：这回要真的送到，且来源是老师自己的名字
            using var adminWebHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            adminWebHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            var teacherProfileId = (await adminWebHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(u => u.Username == accountName)?.Id;

            await adminWebHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest(teacherProfileId ?? string.Empty, uuid),
                JsonOptions);

            var afterGrant = await webHttp.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}", JsonOptions) ?? [];

            Check("授权之后网页上就能看到这个班了",
                afterGrant.Any(item => item.Uuid == uuid),
                string.Join("、", afterGrant.Select(item => $"{item.Name}{(item.Online ? "(在线)" : "(离线)")}")));

            var webText = $"网页喊话 {Guid.NewGuid():N}"[..22];
            var webReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == webText)
                {
                    webReceived.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            var webShout = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherShout}",
                new TeacherWebShoutRequest([uuid], webText, Volume: 88, Speak: false),
                JsonOptions);

            var webBody = await webShout.Content.ReadFromJsonAsync<TeacherWebShoutResponse>(JsonOptions);

            var webFrom = await Task.WhenAny(webReceived.Task, Task.Delay(10_000)) == webReceived.Task
                ? webReceived.Task.Result
                : null;

            Check("网页喊话能送到教室（来源是老师自己，不是客户端自填）",
                webFrom == "数学张老师",
                $"From={webFrom ?? "(10 秒内没收到)"}");

            Check("网页喊话的结果逐间报回来",
                webBody is { Ok: true, Sent: 1 } && webBody.Results.Count == 1 && webBody.Results[0].Ok,
                webBody?.Message ?? "(没有结果)");

            // 内置管理员也能用同一套接口（它的班级是全部）
            var adminList = await adminWebHttp.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}", JsonOptions) ?? [];

            Check("内置管理员用同一套接口时看到的是全部班级",
                adminList.Count >= 2,
                $"看到 {adminList.Count} 个");

            // ---------- 8d. 名单同步与网页呼叫 ----------
            //
            // WebUI 的「呼叫」要能用**与客户端同一套规则**拼句子，前提是名单在服务器上。
            // 这里走一遍完整链路：客户端同步名单 → 网页呼叫 → 教室里真的收到拼好的话。
            var callRoster = new StudentRoster
            {
                Name = "三年二班",
                Students =
                [
                    new Student { Id = "s1", Name = "张三", StudentNo = "20250101", ShortName = "小张", Group = "A组" },
                    new Student { Id = "s2", Name = "李四", StudentNo = "20250102", ShortName = "小李", Group = "A组" },
                    new Student { Id = "s3", Name = "王五", StudentNo = "20250103", Group = "B组" },
                ],
            };

            var callTemplate = new CallTemplate
            {
                Id = "t1",
                Name = "来办公室",
                Components =
                [
                    MessageComponent.Of(MessageComponentKinds.StudentName),
                    MessageComponent.Of(MessageComponentKinds.Text, " 来 "),
                    MessageComponent.Of(MessageComponentKinds.Teacher),
                    MessageComponent.Of(MessageComponentKinds.Text, " 办公室"),
                ],
            };

            var upload = await webHttp.PutAsJsonAsync(
                $"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([callRoster], callRoster.Id, [callTemplate], callTemplate.Id),
                JsonOptions);

            Check("网页呼叫：名单与模板能同步到服务器",
                upload.IsSuccessStatusCode,
                $"HTTP {(int)upload.StatusCode}");

            var synced = await webHttp.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}", JsonOptions);

            Check("网页呼叫：同步上去的名单能读回来（学生与小组都在）",
                synced is { Rosters.Count: 1 } && synced.Rosters[0].Students.Count == 3
                && synced.Rosters[0].Students.Any(s => s.Name == "张三" && s.Group == "A组")
                && synced.Templates.Count == 1,
                synced is null ? "(没读到)" : $"{synced.Rosters.Count} 份名单、{synced.Templates.Count} 个模板");

            // 一位学生一条：与客户端"每个学生一条"的规则一致
            var callTexts = new List<string>();
            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text?.Contains("办公室") == true)
                {
                    lock (callTexts)
                    {
                        callTexts.Add(envelope.Text);
                    }
                }
            };

            var call = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherCall}",
                new TeacherCallRequest([uuid], ["s1", "s3"], TemplateId: "t1"),
                JsonOptions);

            var callBody = await call.Content.ReadFromJsonAsync<TeacherCallResponse>(JsonOptions);
            await Task.Delay(1200);

            Check("网页呼叫：拼出来的话与客户端同一套规则（姓名 + 教师（含科目））",
                callBody is { Ok: true, Sent: 1 }
                && callBody.Messages.Count == 2
                && callBody.Messages.Contains("张三 来 数学张老师 办公室")
                && callBody.Messages.Contains("王五 来 数学张老师 办公室"),
                callBody is null ? "(没有结果)" : string.Join(" / ", callBody.Messages));

            Check("网页呼叫：两条都真的进了教室",
                callTexts.Count == 2,
                callTexts.Count == 0 ? "(一条都没收到)" : string.Join(" / ", callTexts));

            // 含"小组成员"组件时按小组归并：两位同组学生只发一条，且列出整组
            var groupTemplate = new CallTemplate
            {
                Id = "t2",
                Name = "叫整组",
                Components =
                [
                    MessageComponent.Of(MessageComponentKinds.Text, "请 "),
                    MessageComponent.Of(MessageComponentKinds.Group),
                    MessageComponent.Of(MessageComponentKinds.Text, " 来 "),
                    MessageComponent.Of(MessageComponentKinds.Teacher),
                    MessageComponent.Of(MessageComponentKinds.Text, " 办公室"),
                ],
            };

            await webHttp.PutAsJsonAsync(
                $"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload(Templates: [callTemplate, groupTemplate], ActiveTemplateId: "t2"),
                JsonOptions);

            lock (callTexts)
            {
                callTexts.Clear();
            }

            var groupCall = await webHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherCall}",
                new TeacherCallRequest([uuid], ["s1", "s2"]),
                JsonOptions);

            var groupBody = await groupCall.Content.ReadFromJsonAsync<TeacherCallResponse>(JsonOptions);
            await Task.Delay(1200);

            Check("网页呼叫：含「小组成员」组件时按组归并成一条（与客户端同一条规则）",
                groupBody is { Ok: true } && groupBody.Messages.Count == 1
                && groupBody.Messages[0] == "请 张三、李四 来 数学张老师 办公室",
                groupBody is null ? "(没有结果)" : string.Join(" / ", groupBody.Messages));

            // 没同步过名单的账号：要说清该做什么，而不是"发送失败"
            var emptyRosterCall = await adminWebHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.TeacherCall}",
                new TeacherCallRequest([uuid], []),
                JsonOptions);

            var emptyBody = await emptyRosterCall.Content.ReadFromJsonAsync<TeacherCallResponse>(JsonOptions);

            Check("网页呼叫：没同步名单时说清该先去做什么（而不是一句发送失败）",
                emptyBody is { Ok: false } && emptyBody.Message.Contains("名单"),
                emptyBody?.Message ?? "(没有说明)");

            using var anonymousRoster = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            Check("网页呼叫：没登录一律拒绝",
                (await anonymousRoster.PutAsJsonAsync(
                    $"{root}{RelayPaths.TeacherRoster}",
                    new TeacherRosterUpload(Rosters: []),
                    JsonOptions)).StatusCode == System.Net.HttpStatusCode.Unauthorized,
                "HTTP 401");

            // 未登录一律拒绝
            using var anonymous = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var anon = await anonymous.GetAsync($"{root}{RelayPaths.TeacherClassrooms}");

            Check("网页喊话接口没登录一律拒绝",
                (int)anon.StatusCode == 401,
                $"HTTP {(int)anon.StatusCode}");

            // 恢复现场：后面"定时不能绕过授权"那一段要靠"这位老师没有授权"这个前提，
            // 所以这一段用完必须把授权撤回去 —— 测试之间不能互相留状态。
            using var revokeRequest = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{root}{RelayPaths.ConsoleBindings}?userId={Uri.EscapeDataString(teacherProfileId ?? string.Empty)}&uuid={Uri.EscapeDataString(uuid)}");

            await adminWebHttp.SendAsync(revokeRequest);

            var afterRevoke = await webHttp.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}", JsonOptions) ?? [];

            Check("撤销授权之后网页上又看不到了（现场已恢复）",
                afterRevoke.Count == 0,
                $"看到 {afterRevoke.Count} 个");

            // 还要把老师的绑定会话也恢复：撤销授权的实现会顺带解绑这个班里属于他的会话，
            // 而后面（中继图片、多班那些）用的就是这一条绑定 ——
            // 不恢复的话，它们会因为令牌失效而静默地什么都收不到。
            var (rebindOk, rebindError) = await teacher.BindAsync(uuid, secret, "数学张老师");

            Check("撤销授权之后重新绑定回来（后续用例还要用这条链路）",
                rebindOk,
                rebindError ?? "已重新绑定");
        }

        // ---------- 8d. 服务器上的定时喊话 ----------
        //
        // 这是"教师端不在后台运行也能发"的那条路：任务存在服务器上，到点由服务器自己发。
        // 自检用的服务器把检查间隔调到了 200 毫秒（CLASSSHOUT_SCHEDULE_TICK_MS），
        // 所以这里能真的等它到点，而不必假装时间过去了。
        if (!string.IsNullOrEmpty(adminToken))
        {
            using var scheduleHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            scheduleHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            // —— 越权：没被授权的班级不能排定时（否则它就是绕过授权的一条侧门）——
            using var teacherScheduleHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            teacherScheduleHttp.DefaultRequestHeaders.TryAddWithoutValidation(
                RelayPaths.AuthTokenHeader, teacherSettings.AuthToken);

            var unauthorized = await teacherScheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthSchedule}",
                new ScheduleShoutRequest("越权的定时", DateTimeOffset.UtcNow.AddSeconds(5), [uuid]),
                JsonOptions);

            var unauthorizedBody = await unauthorized.Content.ReadAsStringAsync();

            Check("没被授权的班级排不了定时（不能绕过授权）",
                (int)unauthorized.StatusCode == 400 && unauthorizedBody.Contains("还没有授权"),
                $"HTTP {(int)unauthorized.StatusCode}：{Trim(unauthorizedBody)}");

            var unknownTarget = await scheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthSchedule}",
                new ScheduleShoutRequest("发给不存在的班", DateTimeOffset.UtcNow.AddSeconds(5), [Guid.NewGuid().ToString()]),
                JsonOptions);

            var unknownBody = await unknownTarget.Content.ReadAsStringAsync();

            Check("服务器上没有的班级排不了定时",
                (int)unknownTarget.StatusCode == 400 && unknownBody.Contains("没有这些班级"),
                $"HTTP {(int)unknownTarget.StatusCode}：{Trim(unknownBody)}");

            // —— 文字定时：到点后教室端真的收到，且来源是老师那一份名字 ——
            var scheduledText = $"服务器定时 {Guid.NewGuid():N}"[..20];
            var scheduledReceived = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == scheduledText)
                {
                    scheduledReceived.TrySetResult(envelope);
                }
            };

            var textCreate = await scheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthSchedule}",
                new ScheduleShoutRequest(
                    scheduledText,
                    DateTimeOffset.UtcNow.AddSeconds(2),
                    [uuid],
                    Display: ShoutDisplayModes.Popup,
                    FontSize: ShoutFontSizes.Large,
                    HoldMs: 30_000,
                    Speak: false),
                JsonOptions);

            var created = await textCreate.Content.ReadFromJsonAsync<ScheduleShoutResponse>(JsonOptions);

            Check("能排一条服务器定时",
                textCreate.IsSuccessStatusCode && created is { Ok: true, Item: not null },
                created?.Error ?? $"id={created?.Item?.Id}");

            Check("排进去的时候带回目标班级名（老师要看得出排给了谁）",
                created?.Item?.TargetNames.Count == 1 && created.Item.TargetNames[0] == "中继测试教室",
                string.Join("、", created?.Item?.TargetNames ?? []));

            Check("排进去的时候把展示参数一并存下",
                created?.Item is { Display: ShoutDisplayModes.Popup, FontSize: ShoutFontSizes.Large, HoldMs: 30_000, Speak: false },
                $"{created?.Item?.Display} / {created?.Item?.FontSize} / {created?.Item?.HoldMs}ms / 朗读={created?.Item?.Speak}");

            var delivered = await Task.WhenAny(scheduledReceived.Task, Task.Delay(20_000)) == scheduledReceived.Task
                ? scheduledReceived.Task.Result
                : null;

            Check("到点后教室端收到了这条定时喊话（应用根本不在场）",
                delivered is not null,
                delivered is null ? "20 秒内没收到" : $"内容=「{delivered.Text}」");

            Check("定时喊话的来源是排这条的人（管理员就是管理员）",
                delivered?.From == "管理员",
                $"From={delivered?.From ?? "(没收到)"}");

            Check("定时喊话把展示参数一起带到了教室端",
                delivered is { Display: ShoutDisplayModes.Popup, HoldMs: 30_000, Speak: false },
                $"{delivered?.Display} / {delivered?.HoldMs}ms / 朗读={delivered?.Speak}");

            // 发完之后状态要变成"已发出"，并留下逐间的结果
            var afterFire = await scheduleHttp.GetFromJsonAsync<List<ScheduledShoutDto>>(
                $"{root}{RelayPaths.AuthSchedule}", JsonOptions) ?? [];

            var fired = afterFire.FirstOrDefault(item => item.Id == created?.Item?.Id);

            Check("发完之后状态变成已发出，并留下逐间结果",
                fired is { Status: ServerScheduleStatus.Sent } && fired.Results.Count > 0,
                fired is null ? "列表里找不到这条" : $"{fired.Status}：{string.Join("；", fired.Results)}");

            // —— 语音定时：录好的一段 PCM 到点被原样放出去 ——
            var clipFormat = AudioFormat.Default;
            var clipSeconds = 0.5;
            var clipPcm = new byte[clipFormat.BytesForDuration((int)(clipSeconds * 1000))];

            for (var i = 0; i < clipPcm.Length; i++)
            {
                clipPcm[i] = (byte)(i % 251);
            }

            var audioStartSeen = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var audioEndSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var audioChunks = new List<byte[]>();

            classroom.ShoutReceived += envelope =>
            {
                switch (envelope.Kind)
                {
                    case RelayKinds.AudioStart:
                        audioStartSeen.TrySetResult(envelope);
                        break;

                    case RelayKinds.Audio when !string.IsNullOrEmpty(envelope.AudioBase64):
                        lock (audioChunks)
                        {
                            audioChunks.Add(Convert.FromBase64String(envelope.AudioBase64));
                        }

                        break;

                    case RelayKinds.AudioEnd:
                        audioEndSeen.TrySetResult(true);
                        break;
                }
            };

            using var voiceContent = new MultipartFormDataContent
            {
                { new StringContent(DateTimeOffset.UtcNow.AddSeconds(2).ToString("O")), "sendAt" },
                { new StringContent(uuid), "targetUuids" },
                { new StringContent("这条是定时的语音"), "text" },
            };

            var audioPart = new ByteArrayContent(WavCodec.Encode(clipFormat, clipPcm));
            audioPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            voiceContent.Add(audioPart, "audio", "clip.wav");

            var voiceCreate = await scheduleHttp.PostAsync($"{root}{RelayPaths.AuthSchedule}/voice", voiceContent);
            var voiceBody = await voiceCreate.Content.ReadAsStringAsync();
            var voiceCreated = JsonSerializer.Deserialize<ScheduleShoutResponse>(voiceBody, JsonOptions);

            Check("能排一条带语音的服务器定时",
                voiceCreate.IsSuccessStatusCode && voiceCreated is { Ok: true },
                voiceCreated?.Error ?? Trim(voiceBody));

            Check("语音时长被记下来（列表里要显示得出来）",
                voiceCreated?.Item is { } voiceItem &&
                string.Equals(voiceItem.Kind, ScheduledShoutKinds.Voice, StringComparison.Ordinal) &&
                Math.Abs(voiceItem.AudioSeconds - clipSeconds) < 0.1,
                $"{voiceCreated?.Item?.Kind} / {voiceCreated?.Item?.AudioSeconds:0.##} 秒");

            var startEnvelope = await Task.WhenAny(audioStartSeen.Task, Task.Delay(20_000)) == audioStartSeen.Task
                ? audioStartSeen.Task.Result
                : null;

            await Task.WhenAny(audioEndSeen.Task, Task.Delay(15_000));

            Check("到点后教室端收到了语音开始",
                startEnvelope is { SampleRate: > 0, Channels: > 0, BitsPerSample: > 0 },
                startEnvelope is null
                    ? "20 秒内没收到"
                    : $"{startEnvelope.SampleRate} Hz / {startEnvelope.Channels} 声道 / {startEnvelope.BitsPerSample} bit");

            Check("语音的采集格式与录的时候一致",
                startEnvelope is not null &&
                startEnvelope.SampleRate == clipFormat.SampleRate &&
                startEnvelope.Channels == clipFormat.Channels &&
                startEnvelope.BitsPerSample == clipFormat.BitsPerSample,
                startEnvelope is null ? "(没收到)" : $"{startEnvelope.SampleRate}/{startEnvelope.Channels}/{startEnvelope.BitsPerSample}");

            byte[] reassembled;
            lock (audioChunks)
            {
                reassembled = audioChunks.SelectMany(chunk => chunk).ToArray();
            }

            Check("定时语音的字节逐字节一致（没有被截断或改写）",
                reassembled.Length == clipPcm.Length && reassembled.AsSpan().SequenceEqual(clipPcm),
                $"发出 {clipPcm.Length} 字节，收到 {reassembled.Length} 字节");

            Check("定时语音分了多片推（按实时节奏，而不是一口气塞进去）",
                audioChunks.Count > 1,
                $"共 {audioChunks.Count} 片");

            Check("语音发完之后收了尾（教室端知道该结束播放了）",
                audioEndSeen.Task.IsCompleted,
                audioEndSeen.Task.IsCompleted ? "已收尾" : "没收到 audioEnd");

            // —— 老师自己排的那条：来源要用**他的**名字（而且按班级取科目）——
            //
            // 这一段才是真实用法：老师在自己的手机上排一条，然后关掉手机。
            var teacherProfile = (await scheduleHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(u => u.Username == accountName);

            await scheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest(teacherProfile?.Id ?? string.Empty, uuid),
                JsonOptions);

            var byTeacherText = $"老师排的定时 {Guid.NewGuid():N}"[..22];
            var byTeacherReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == byTeacherText)
                {
                    byTeacherReceived.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            var teacherCreate = await teacherScheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthSchedule}",
                new ScheduleShoutRequest(byTeacherText, DateTimeOffset.UtcNow.AddSeconds(2), [uuid]),
                JsonOptions);

            Check("授权之后老师能自己排定时",
                teacherCreate.IsSuccessStatusCode,
                Trim(await teacherCreate.Content.ReadAsStringAsync()));

            var teacherFrom = await Task.WhenAny(byTeacherReceived.Task, Task.Delay(20_000)) == byTeacherReceived.Task
                ? byTeacherReceived.Task.Result
                : null;

            Check("老师排的定时用的是他自己的名字（服务器贴的，客户端冒充不了）",
                teacherFrom == "数学张老师",
                $"From={teacherFrom ?? "(20 秒内没收到)"}");

            // —— 取消：取消掉的不该发出去 ——
            var cancelTarget = await scheduleHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.AuthSchedule}",
                new ScheduleShoutRequest(
                    $"这条会被取消 {Guid.NewGuid():N}"[..24],
                    DateTimeOffset.UtcNow.AddSeconds(6),
                    [uuid]),
                JsonOptions);

            var cancelItem = (await cancelTarget.Content.ReadFromJsonAsync<ScheduleShoutResponse>(JsonOptions))?.Item;

            var cancelledText = cancelItem?.Text ?? string.Empty;
            var cancelledArrived = false;

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == cancelledText)
                {
                    cancelledArrived = true;
                }
            };

            var cancelResponse = await scheduleHttp.DeleteAsync(
                $"{root}{string.Format(RelayPaths.AuthScheduleItem, cancelItem?.Id)}");

            Check("能取消服务器上的一条定时", cancelResponse.IsSuccessStatusCode, $"HTTP {(int)cancelResponse.StatusCode}");

            await Task.Delay(7000);

            Check("取消掉的定时不会到点发出去", !cancelledArrived, "教室里没有出现这条");

            var afterCancel = await scheduleHttp.GetFromJsonAsync<List<ScheduledShoutDto>>(
                $"{root}{RelayPaths.AuthSchedule}", JsonOptions) ?? [];

            Check("取消之后列表里标成已取消",
                afterCancel.FirstOrDefault(item => item.Id == cancelItem?.Id)?.Status == ServerScheduleStatus.Cancelled,
                afterCancel.FirstOrDefault(item => item.Id == cancelItem?.Id)?.Status ?? "(找不到)");
        }

        // ---------- 8e. 控制台集体喊话 ----------
        //
        // "所有在线教室"这个概念得真的压一遍：判在线的依据是教室记录上的最近活动时间，
        // 而两间教室此刻都在长轮询 —— 少发一间是那种当场就会被发现的尴尬。
        if (!string.IsNullOrEmpty(adminToken))
        {
            using var adminHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            adminHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            var broadcastText = $"集体喊话测试 {Guid.NewGuid():N}"[..22];

            var broadcastFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var broadcastSecond = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            classroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == broadcastText)
                {
                    broadcastFirst.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            otherClassroom.ShoutReceived += envelope =>
            {
                if (envelope.Kind == RelayKinds.TextShout && envelope.Text == broadcastText)
                {
                    broadcastSecond.TrySetResult(envelope.From ?? string.Empty);
                }
            };

            var broadcastResponse = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBroadcast}",
                new BroadcastShoutRequest(broadcastText),
                JsonOptions);

            var broadcastBody = await broadcastResponse.Content.ReadAsStringAsync();

            Check("管理员可以发起集体喊话",
                broadcastResponse.IsSuccessStatusCode && broadcastBody.Contains("\"count\":2"),
                Trim(broadcastBody));

            var firstGot = await Task.WhenAny(broadcastFirst.Task, Task.Delay(6000)) == broadcastFirst.Task;
            var secondGot = await Task.WhenAny(broadcastSecond.Task, Task.Delay(6000)) == broadcastSecond.Task;

            Check("集体喊话送到了第一间在线教室", firstGot,
                firstGot ? $"来源={broadcastFirst.Task.Result}" : "6 秒内没收到");

            Check("集体喊话也送到了第二间在线教室", secondGot,
                secondGot ? $"来源={broadcastSecond.Task.Result}" : "6 秒内没收到");

            // 空的集体喊话要被挡住：一条空的集体喊话会让每间教室都收到一次没声音的"提醒"
            var emptyResponse = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleBroadcast}",
                new BroadcastShoutRequest("   "),
                JsonOptions);

            Check("空的集体喊话被拒",
                !emptyResponse.IsSuccessStatusCode,
                $"HTTP {(int)emptyResponse.StatusCode}");

            // ---------- 8d. 分享链接一键绑定 ----------
            //
            // 这条路上最要紧的两件事：链接是凭据（所以兑现必须登录），
            // 以及兑现之后老师确实拿到了这几个班（而不是"接口返回成功但什么都没发生"）。
            var shareUuids = new List<string> { uuid, otherSettings.Uuid };

            var shareCreate = await adminHttp.PostAsJsonAsync(
                $"{root}{RelayPaths.ConsoleShare}",
                new ShareClassroomRequest(shareUuids),
                JsonOptions);

            var shareBody = await shareCreate.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            var shareToken = shareBody.TryGetProperty("token", out var tokenElement)
                ? tokenElement.GetString()
                : null;

            Check("管理员能生成分享链接",
                shareCreate.IsSuccessStatusCode && !string.IsNullOrEmpty(shareToken),
                Trim(shareBody.ToString()));

            Check("分享链接里带上了班级数量",
                shareBody.TryGetProperty("count", out var countElement) && countElement.GetInt32() == 2,
                $"count={(shareBody.TryGetProperty("count", out var c2) ? c2.GetInt32() : -1)}");

            // 公开信息：老师点开链接时还没登录，这一步必须不需要凭据
            var shareInfo = await http.GetFromJsonAsync<ShareInfoResponse>(
                $"{root}{string.Format(RelayPaths.ShareInfo, shareToken)}", JsonOptions);

            Check("分享链接的公开信息不需要登录就能看",
                shareInfo is { Ok: true } && shareInfo.Classrooms?.Count == 2,
                shareInfo is null ? "没有响应" : $"{shareInfo.Classrooms?.Count ?? 0} 个班级");

            Check("公开信息里没有口令之类的凭据",
                shareInfo?.Classrooms?.All(item => !string.IsNullOrWhiteSpace(item.Name)) == true,
                string.Join("、", shareInfo?.Classrooms?.Select(item => item.Name) ?? []));

            // 未登录兑现要被明确拒绝：链接是凭据，但"谁绑的"必须有据可查
            var anonymousClaim = await http.PostAsync(
                $"{root}{string.Format(RelayPaths.ShareClaim, shareToken)}", content: null);

            var anonymousBody = await anonymousClaim.Content.ReadFromJsonAsync<ShareClaimResponse>(JsonOptions);

            Check("未登录时兑现分享链接被拒，并告诉老师该先做什么",
                anonymousBody is { Ok: false } && anonymousBody.Error?.Contains("登录") == true,
                anonymousBody?.Error ?? "居然通过了");

            // 用之前那个已登录的教师账号兑现
            var claimRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{root}{string.Format(RelayPaths.ShareClaim, shareToken)}");

            claimRequest.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, teacherSettings.AuthToken);

            var claimResponse = await http.SendAsync(claimRequest);
            var claimBody = await claimResponse.Content.ReadFromJsonAsync<ShareClaimResponse>(JsonOptions);

            Check("登录后兑现分享链接成功",
                claimBody is { Ok: true } && claimBody.Classrooms?.Count == 2,
                claimBody?.Error ?? $"授权了 {claimBody?.Classrooms?.Count ?? 0} 个班级");

            Check("兑现时会报告新增了几个班级",
                claimBody?.Granted >= 0,
                $"新增 {claimBody?.Granted ?? -1} 个");

            // 关键一条：兑现之后这些班要真的出现在这个账号的"已授权教室"里，
            // 否则老师那边看到的还是空列表 —— 接口返回成功而什么都没发生。
            //
            // 这个接口要教师令牌，所以不能拿默认的 http 直接请求：
            // 401 会让 GetFromJsonAsync 抛异常，而那看起来像"测试崩了"而不是"授权没生效"。
            using var authorizedRequest = new HttpRequestMessage(
                HttpMethod.Get, $"{root}{RelayPaths.TeacherAuthorized}");

            authorizedRequest.Headers.TryAddWithoutValidation(
                RelayPaths.AuthTokenHeader, teacherSettings.AuthToken);

            using var authorizedResponse = await http.SendAsync(authorizedRequest);

            var authorizedAfterClaim = await authorizedResponse.Content
                .ReadFromJsonAsync<List<AuthorizedClassroom>>(JsonOptions);

            Check("兑现之后这些班级出现在账号的授权列表里",
                authorizedAfterClaim?.Count >= 2,
                $"授权列表里有 {authorizedAfterClaim?.Count ?? 0} 个班级（HTTP {(int)authorizedResponse.StatusCode}）");

            // 无效令牌
            var bogusClaim = await http.PostAsync(
                $"{root}{string.Format(RelayPaths.ShareClaim, "0123456789abcdef0123456789abcdef")}", content: null);

            var bogusBody = await bogusClaim.Content.ReadFromJsonAsync<ShareClaimResponse>(JsonOptions);

            Check("无效的分享令牌被拒",
                bogusBody is { Ok: false },
                bogusBody?.Error ?? "居然通过了");
        }

        // ---------- 8c. 中继链路上的图片 ----------
        //
        // 中继那条路的分片大小和局域网不一样（服务器对单个请求体有 16 KiB 上限，
        // base64 还要再放大三分之一），所以必须单独压一遍：
        // 直接用局域网那个分片大小的话，每一片都会被服务器以 413 拒掉，
        // 而表现只是"图片发不出去"，不会告诉你是分片太大。
        var relayImage = new byte[64 * 1024];
        for (var i = 0; i < relayImage.Length; i++)
        {
            relayImage[i] = (byte)((i * 17 + 3) % 253);
        }

        var relayImageStart = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayImageEnd = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayImageParts = new List<byte[]>();

        classroom.ShoutReceived += envelope =>
        {
            switch (envelope.Kind)
            {
                case RelayKinds.ImageStart:
                    relayImageStart.TrySetResult(envelope);
                    break;

                case RelayKinds.Image when envelope.ImageBase64 is not null:
                    lock (relayImageParts)
                    {
                        relayImageParts.Add(Convert.FromBase64String(envelope.ImageBase64));
                    }

                    break;

                case RelayKinds.ImageEnd:
                    relayImageEnd.TrySetResult(envelope);
                    break;
            }
        };

        var relayImageOk = await teacher.SendImageAsync(
            new ImageStartMessage { Text = "这是中继发来的图", FontSize = ShoutFontSizes.Medium },
            relayImage);

        var relayStart = await relayImageStart.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await relayImageEnd.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Check("中继链路的图片发送成功", relayImageOk, relayImageOk ? "已发出" : "发送失败");

        Check("中继链路的图片声明带回字节数",
            relayStart.ImageTotalBytes == relayImage.Length,
            $"声明 {relayStart.ImageTotalBytes} 字节，实际 {relayImage.Length} 字节");

        byte[][] relayParts;
        lock (relayImageParts)
        {
            relayParts = relayImageParts.ToArray();
        }

        var relayExpectedParts = (relayImage.Length + ShoutProtocol.RelayImageChunkSize - 1) / ShoutProtocol.RelayImageChunkSize;

        Check("中继链路的图片按服务器允许的分片大小切片",
            relayParts.Length == relayExpectedParts,
            $"{relayImage.Length} 字节切成 {relayParts.Length} 片（期望 {relayExpectedParts} 片，每片 {ShoutProtocol.RelayImageChunkSize / 1024} KB）");

        var relayJoined = new byte[relayParts.Sum(p => p.Length)];
        var relayOffset = 0;
        foreach (var part in relayParts)
        {
            part.CopyTo(relayJoined, relayOffset);
            relayOffset += part.Length;
        }

        Check("中继链路上拼回来的图片与发出的一致",
            relayJoined.AsSpan().SequenceEqual(relayImage),
            $"{relayJoined.Length} 字节");

        Check("中继链路的图片带上了说明文字",
            relayStart.Text == "这是中继发来的图",
            relayStart.Text ?? "(没有说明)");

        // ---------- 8f. 账号角色（教师 / 班主任） ----------
        //
        // 角色这一层只管"有没有资格当班主任"，具体管哪几间班是班级授权上另一条标记 ——
        // 两者同时成立才算数。所以它必须能从三处读回来：注册响应、登录响应、GET /api/auth/me。
        // 少任何一处，客户端就只能自己猜"该不该显示「我的班级」"，
        // 而猜错的两头都是事故：把入口给了不该有的人，或者该有的人根本找不到入口。
        if (string.IsNullOrEmpty(adminToken))
        {
            // 与上面几节一样：没有管理员口令就造不出班主任账号、也伪造不了绑定。
            // 但要说一声 —— 否则"全部通过"会让人以为这些用例真的跑过了。
            Check("角色与班主任这一节能跑起来", explicitAdminPassword is null,
                explicitAdminPassword is null
                    ? "跳过 —— 未提供管理员口令，这一节里的账号与绑定都造不出来"
                    : "** 提供了管理员口令却登录失败，请核对是否与目标服务器一致");
        }
        else
        {
            using var consoleHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            consoleHttp.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            var headName = "head" + Guid.NewGuid().ToString("N")[..8];
            const string headPassword = "head-pass-1234";

            var headRegister = await consoleHttp.PostAsJsonAsync($"{root}{RelayPaths.AuthRegister}",
                new RegisterRequest(headName, null, "李班主任", headPassword, "语文"), JsonOptions);
            var headAuth = await headRegister.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);

            Check("注册响应里的 user 带着角色（新账号默认是普通教师）",
                headAuth is { Ok: true } && headAuth.User?.Role == UserRoles.Teacher,
                headAuth?.User is null
                    ? headAuth?.Error ?? "(没有账号信息)"
                    : $"role={headAuth.User.Role}，姓名={headAuth.User.DisplayName}");

            var headToken = headAuth?.Token ?? string.Empty;
            var headId = headAuth?.User?.Id ?? string.Empty;

            using var headApi = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            headApi.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, headToken);

            var headMeBefore = await headApi.GetFromJsonAsync<UserProfileDto>($"{root}{RelayPaths.AuthMe}", JsonOptions);

            Check("GET /api/auth/me 明确报出角色（默认教师）",
                headMeBefore?.Role == UserRoles.Teacher,
                headMeBefore is null ? "(读不到账号信息)" : $"role={headMeBefore.Role}，账号={headMeBefore.Username}");

            // 写坏的值必须被拒。"认不出来的值一律当教师"是服务端的收口，但它不该
            // 悄悄替管理员做决定：存下一个谁也没打算设的角色，管理员只会以为改成功了。
            var badRole = await consoleHttp.PostAsJsonAsync($"{root}/api/console/users/{headId}/role",
                new ConsoleRoleRequest("principal"), JsonOptions);
            var badRoleBody = await badRole.Content.ReadFromJsonAsync<ConsoleRoleResult>(JsonOptions);

            Check("坏的角色值被拒（400，并说明只能是教师或班主任）",
                (int)badRole.StatusCode == 400 && !string.IsNullOrWhiteSpace(badRoleBody?.Error),
                $"HTTP {(int)badRole.StatusCode}：{Trim(badRoleBody?.Error ?? "(没有说明)")}");

            // 内置管理员不是用户库里的一条记录，也就没有"角色"可言。能给它改角色的接口，
            // 等于给了一条"给自己换个更大权限"的入口。
            var adminRole = await consoleHttp.PostAsJsonAsync($"{root}/api/console/users/builtin-admin/role",
                new ConsoleRoleRequest(UserRoles.HeadTeacher), JsonOptions);
            var adminRoleBody = await adminRole.Content.ReadFromJsonAsync<ConsoleRoleResult>(JsonOptions);

            Check("内置管理员没有角色可改（400）",
                (int)adminRole.StatusCode == 400,
                $"HTTP {(int)adminRole.StatusCode}：{Trim(adminRoleBody?.Error ?? "(没有说明)")}");

            var setRole = await consoleHttp.PostAsJsonAsync($"{root}/api/console/users/{headId}/role",
                new ConsoleRoleRequest(UserRoles.HeadTeacher), JsonOptions);
            var setRoleBody = await setRole.Content.ReadFromJsonAsync<ConsoleRoleResult>(JsonOptions);

            Check("管理员能把账号设为班主任（回的是规范化之后的角色与中文标签）",
                setRole.IsSuccessStatusCode && setRoleBody is { Ok: true, Role: UserRoles.HeadTeacher, Label: "班主任" },
                $"HTTP {(int)setRole.StatusCode}：role={setRoleBody?.Role ?? "(空)"}，label={setRoleBody?.Label ?? "(空)"}");

            var headMeAfter = await headApi.GetFromJsonAsync<UserProfileDto>($"{root}{RelayPaths.AuthMe}", JsonOptions);

            Check("改完角色之后 me 立刻改口（不必重新登录、也不必重启客户端）",
                headMeAfter?.Role == UserRoles.HeadTeacher,
                headMeAfter is null ? "(读不到账号信息)" : $"role={headMeAfter.Role}");

            // 角色是账号上的字段，所以重新登录一次也应当在登录响应里原样回来 ——
            // 否则会出现"重开一次 App 我又变回普通教师"。
            var headLogin = await consoleHttp.PostAsJsonAsync($"{root}{RelayPaths.AuthLogin}",
                new LoginRequest(headName, headPassword), JsonOptions);
            var headLoginBody = await headLogin.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);

            Check("登录响应里的 user 也带着角色（重新登录仍是班主任）",
                headLoginBody is { Ok: true } && headLoginBody.User?.Role == UserRoles.HeadTeacher,
                headLoginBody?.User is null
                    ? headLoginBody?.Error ?? "(没有账号信息)"
                    : $"role={headLoginBody.User.Role}");

            // ---------- 8g. 班主任绑定：一次动作，两件事 ----------
            //
            // 「他是不是班主任」这一层在账号上，「他管哪几间班」这一层在授权上。
            // 所以管理员在控制台上勾一次"班主任"，必须把两件事一起做掉：
            // 只加标记不改角色，界面上他还是"教师"，出了事没人认；
            // 只改角色不加标记，他立刻顶着班主任的头衔却什么都管不了。
            var headGrant = await consoleHttp.PostAsJsonAsync($"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest(headId, uuid, AsHeadTeacher: true), JsonOptions);
            var headGrantBody = await headGrant.Content.ReadAsStringAsync();

            Check("管理员把某个班授权给这位老师，并一次指定为班主任",
                headGrant.IsSuccessStatusCode,
                $"HTTP {(int)headGrant.StatusCode}：{Trim(headGrantBody)}");

            var bindingsAfterHead = await consoleHttp.GetFromJsonAsync<List<ConsoleBinding>>(
                $"{root}{RelayPaths.ConsoleBindings}", JsonOptions) ?? [];
            var headBindingRow = bindingsAfterHead.FirstOrDefault(row => row.UserId == headId && row.Uuid == uuid);

            Check("授权列表里能看出这条是「班主任」授权",
                headBindingRow is { AsHeadTeacher: true },
                headBindingRow is null
                    ? $"没有找到 {headId} → {uuid[..8]}… 这条记录"
                    : $"{headBindingRow.UserDisplayName} → {headBindingRow.ClassroomName}，asHeadTeacher={headBindingRow.AsHeadTeacher}");

            // ---------- 8h. 按账号查班级（客户端问「我能喊哪几间」的那条路） ----------
            //
            // accountId 是客户端**自述**的身份：它让"这条请求是谁发的"在客户端那边也说得清，
            // 但它绝不是授权依据 —— 拿别人的账号 Id 来问必须当场拒绝，
            // 否则任何人换个参数就能看到别人的班级。
            using var teacherApi = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            teacherApi.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, teacherSettings.AuthToken);

            var ownList = await teacherApi.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}?accountId={Uri.EscapeDataString(teacherSettings.UserId!)}", JsonOptions) ?? [];
            var ownEntry = ownList.FirstOrDefault(item => item.Uuid == uuid);

            Check("带上自己的账号 Id 能拿到班级列表",
                ownEntry is not null,
                ownList.Count == 0
                    ? "列表是空的（这位老师此刻没有被授权任何班级）"
                    : string.Join("、", ownList.Select(item => $"{item.Name}[{item.Role}/{(item.Online ? "在线" : "离线")}]")));

            Check("任课老师在这间班里的角色是普通教师，并且管不了权限",
                ownEntry is { Role: UserRoles.Teacher, CanManage: false },
                ownEntry is null ? "(列表里没有这一间)" : $"role={ownEntry.Role}，canManage={ownEntry.CanManage}");

            Check("在线状态跟着教室记录走（这一间此刻正在长轮询）",
                ownEntry?.Online == true,
                ownEntry is null ? "(列表里没有这一间)" : $"online={ownEntry.Online}，lastSeen={ownEntry.LastSeenAt:HH:mm:ss}");

            var strangerQuery = await teacherApi.GetAsync(
                $"{root}{RelayPaths.TeacherClassrooms}?accountId={Uri.EscapeDataString(headId)}");
            var strangerBody = await strangerQuery.Content.ReadAsStringAsync();

            Check("拿别人的账号 Id 来查班级被拒（403，且回的是 ok:false）",
                (int)strangerQuery.StatusCode == 403 && strangerBody.Contains("\"ok\":false"),
                $"HTTP {(int)strangerQuery.StatusCode}：{Trim(strangerBody)}");

            var narrowedResponse = await teacherApi.GetAsync(
                $"{root}{RelayPaths.TeacherClassrooms}?accountId={Uri.EscapeDataString(teacherSettings.UserId!)}&uuid={Uri.EscapeDataString(uuid)}");
            var narrowed = narrowedResponse.IsSuccessStatusCode
                ? await narrowedResponse.Content.ReadFromJsonAsync<List<TeacherClassroomDto>>(JsonOptions) ?? []
                : [];

            Check("再带上 uuid 时只回这一间",
                narrowed.Count == 1 && narrowed[0].Uuid == uuid,
                $"HTTP {(int)narrowedResponse.StatusCode}，回了 {narrowed.Count} 间：{string.Join("、", narrowed.Select(item => item.Name))}");

            var headOwnView = await headApi.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}?accountId={Uri.EscapeDataString(headId)}", JsonOptions) ?? [];
            var headOwnEntry = headOwnView.FirstOrDefault(item => item.Uuid == uuid);

            Check("被指定为班主任之后，这一间在他那里就是 headTeacher 且可管",
                headOwnEntry is { Role: UserRoles.HeadTeacher, CanManage: true },
                headOwnEntry is null
                    ? $"列表里没有这一间（共 {headOwnView.Count} 间）"
                    : $"role={headOwnEntry.Role}，canManage={headOwnEntry.CanManage}");

            // ---------- 8i. 班主任只看得见、也只改得了自己的班 ----------
            //
            // 这是"班主任"这个角色唯一存在的意义，也是这一节里最该被压死的一条：
            // 管权限的接口一旦只按"你是不是班主任"判断、而不按"你是不是**这一间**的班主任"，
            // 任何一位班主任都能往别人的班里塞人。
            var headClassrooms = await headApi.GetFromJsonAsync<List<HeadTeacherClassroomDto>>(
                $"{root}{RelayPaths.HeadTeacherClassrooms}", JsonOptions) ?? [];

            Check("班主任只看到自己当班主任的那几间（这里恰好只有一间）",
                headClassrooms.Count == 1 && headClassrooms[0].Uuid == uuid,
                headClassrooms.Count == 0
                    ? "一间都没有"
                    : string.Join("、", headClassrooms.Select(item => item.Name)));

            var headRoom = headClassrooms.FirstOrDefault(item => item.Uuid == uuid);

            Check("他管的这间班带着教室名与在线状态",
                headRoom is { Online: true } && !string.IsNullOrWhiteSpace(headRoom.Name),
                headRoom is null ? "(列表里没有这一间)" : $"name={headRoom.Name}，online={headRoom.Online}，lastSeen={headRoom.LastSeenAt:HH:mm:ss}");

            var headSelfInRoom = headRoom?.Teachers.FirstOrDefault(teacher => teacher.UserId == headId);
            var courseTeacherInRoom = headRoom?.Teachers.FirstOrDefault(teacher => teacher.UserId == teacherSettings.UserId);

            Check("这间班已授权的老师都列了出来，并标明谁是班主任",
                headSelfInRoom is { AsHeadTeacher: true, DisplayName: "李班主任" }
                && courseTeacherInRoom is { AsHeadTeacher: false },
                headRoom is null
                    ? "(列表里没有这一间)"
                    : string.Join("、", headRoom.Teachers.Select(teacher => $"{teacher.DisplayName}{(teacher.AsHeadTeacher ? "(班主任)" : string.Empty)}")));

            Check("还没上传统一名单时，hasRoster 与 enforced 都是假",
                headRoom is { HasRoster: false, Enforced: false },
                headRoom is null ? "(列表里没有这一间)" : $"hasRoster={headRoom.HasRoster}，enforced={headRoom.Enforced}");

            var teacherHeadView = await teacherApi.GetFromJsonAsync<List<HeadTeacherClassroomDto>>(
                $"{root}{RelayPaths.HeadTeacherClassrooms}", JsonOptions) ?? [];

            Check("不是班主任的老师看到的是空列表（而不是全部班级）",
                teacherHeadView.Count == 0,
                $"看到 {teacherHeadView.Count} 间");

            var adminHeadView = await consoleHttp.GetFromJsonAsync<List<HeadTeacherClassroomDto>>(
                $"{root}{RelayPaths.HeadTeacherClassrooms}", JsonOptions) ?? [];

            Check("管理员不受这条限制，看到的是全部班级",
                adminHeadView.Count >= 2
                && adminHeadView.Any(item => item.Uuid == uuid)
                && adminHeadView.Any(item => item.Uuid == otherSettings.Uuid),
                $"看到 {adminHeadView.Count} 间：{string.Join("、", adminHeadView.Select(item => item.Name))}");

            // 找一位"现成的普通账号"当被授权的任课老师：用前面 CSV 批量导入进来的那一位。
            // 这一节要压的是权限，不必再凭空造一个账号。
            var peerProfile = (await consoleHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(user => user.Username?.StartsWith("csvok", StringComparison.Ordinal) == true);

            var peerId = peerProfile?.Id ?? string.Empty;

            Check("找到一位普通教师账号用来试授权（CSV 导入进来的那一位）",
                peerProfile is { Role: UserRoles.Teacher },
                peerProfile is null
                    ? "没找到 csvok 开头的账号"
                    : $"id={peerProfile.Id}，姓名={peerProfile.DisplayName}，role={peerProfile.Role}");

            // 班主任之外的人给自己的班拉人：必须 403。这里用的是那间班的任课老师 ——
            // 他能朝这间教室喊话，但"改这个班的权限"是另一回事。
            var notHeadGrant = await teacherApi.PostAsJsonAsync($"{root}{RelayPaths.HeadTeacherBindings}",
                new HeadTeacherGrantRequest(uuid, peerId), JsonOptions);
            var notHeadGrantBody = await notHeadGrant.Content.ReadAsStringAsync();

            Check("任课老师不能给自己任课的班改权限（403）",
                (int)notHeadGrant.StatusCode == 403 && notHeadGrantBody.Contains("\"error\""),
                $"HTTP {(int)notHeadGrant.StatusCode}：{Trim(notHeadGrantBody)}");

            // 班主任去动别人的班：同样是 403。这一条比上一条更要紧 ——
            // 它挡的正是"一位班主任顺手管了全校"。
            var crossGrant = await headApi.PostAsJsonAsync($"{root}{RelayPaths.HeadTeacherBindings}",
                new HeadTeacherGrantRequest(otherSettings.Uuid, teacherSettings.UserId!), JsonOptions);
            var crossGrantBody = await crossGrant.Content.ReadAsStringAsync();

            Check("班主任不能给不是自己管的班授权（403）",
                (int)crossGrant.StatusCode == 403 && crossGrantBody.Contains("\"error\""),
                $"HTTP {(int)crossGrant.StatusCode}：{Trim(crossGrantBody)}");

            var crossRevoke = await headApi.DeleteAsync(
                $"{root}{RelayPaths.HeadTeacherBindings}?classroomUuid={Uri.EscapeDataString(otherSettings.Uuid)}&userId={Uri.EscapeDataString(teacherSettings.UserId!)}");
            var crossRevokeBody = await crossRevoke.Content.ReadAsStringAsync();

            Check("班主任也不能收回别人班里的老师（403）",
                (int)crossRevoke.StatusCode == 403 && crossRevokeBody.Contains("\"error\""),
                $"HTTP {(int)crossRevoke.StatusCode}：{Trim(crossRevokeBody)}");

            var peerGrant = await headApi.PostAsJsonAsync($"{root}{RelayPaths.HeadTeacherBindings}",
                new HeadTeacherGrantRequest(uuid, peerId), JsonOptions);
            var peerGrantBody = await peerGrant.Content.ReadFromJsonAsync<HeadTeacherGrantResult>(JsonOptions);

            Check("班主任能给自己管的班授权一位任课老师",
                peerGrant.IsSuccessStatusCode && peerGrantBody is { Ok: true, Already: false },
                $"HTTP {(int)peerGrant.StatusCode}：{Trim(peerGrantBody?.Message ?? peerGrantBody?.Error ?? "(没有说明)")}");

            var peerAgain = await headApi.PostAsJsonAsync($"{root}{RelayPaths.HeadTeacherBindings}",
                new HeadTeacherGrantRequest(uuid, peerId), JsonOptions);
            var peerAgainBody = await peerAgain.Content.ReadFromJsonAsync<HeadTeacherGrantResult>(JsonOptions);

            Check("重复授权不算错，但要如实说「本来就有」",
                peerAgain.IsSuccessStatusCode && peerAgainBody is { Ok: true, Already: true },
                $"HTTP {(int)peerAgain.StatusCode}：{Trim(peerAgainBody?.Message ?? "(没有说明)")}");

            // 授权只加"这间班归他用"，不动账号角色。两件事混在一起的话，
            // "班主任"这个头衔会随着某一次普通的班级授权被随手送出去。
            var peerAfterGrant = (await consoleHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(user => user.Id == peerId);

            Check("被授权任课不会顺带把账号变成班主任",
                peerAfterGrant is { Role: UserRoles.Teacher },
                peerAfterGrant is null ? "(账号不见了)" : $"role={peerAfterGrant.Role}");

            var peerRevoke = await headApi.DeleteAsync(
                $"{root}{RelayPaths.HeadTeacherBindings}?classroomUuid={Uri.EscapeDataString(uuid)}&userId={Uri.EscapeDataString(peerId)}");
            var peerRevokeBody = await peerRevoke.Content.ReadAsStringAsync();

            Check("班主任能收回自己班里任课老师的权限",
                peerRevoke.IsSuccessStatusCode && peerRevokeBody.Contains("\"ok\":true"),
                $"HTTP {(int)peerRevoke.StatusCode}：{Trim(peerRevokeBody)}");

            var roomsAfterPeerRevoke = await headApi.GetFromJsonAsync<List<HeadTeacherClassroomDto>>(
                $"{root}{RelayPaths.HeadTeacherClassrooms}", JsonOptions) ?? [];
            var teachersAfterPeerRevoke = roomsAfterPeerRevoke.FirstOrDefault(item => item.Uuid == uuid)?.Teachers ?? [];

            Check("收回之后那个人就从这间班的授权名单里消失了",
                teachersAfterPeerRevoke.All(teacher => teacher.UserId != peerId),
                teachersAfterPeerRevoke.Count == 0
                    ? "(这间班现在一条授权都没有)"
                    : string.Join("、", teachersAfterPeerRevoke.Select(teacher => teacher.DisplayName)));

            // 管理员把同一个人指定为这个班的班主任：**一次动作**里既要加标记、
            // 也要把账号角色升上去 —— 这正是"班主任绑定"这条接口存在的理由。
            var peerHeadGrant = await consoleHttp.PostAsJsonAsync($"{root}{RelayPaths.ConsoleBindings}",
                new GrantBindingRequest(peerId, uuid, AsHeadTeacher: true), JsonOptions);
            var peerHeadGrantBody = await peerHeadGrant.Content.ReadAsStringAsync();

            Check("管理员一次就能把一位任课老师指定为班主任",
                peerHeadGrant.IsSuccessStatusCode,
                $"HTTP {(int)peerHeadGrant.StatusCode}：{Trim(peerHeadGrantBody)}");

            var peerAfterHeadGrant = (await consoleHttp.GetFromJsonAsync<List<UserProfileDto>>(
                $"{root}/api/console/users", JsonOptions) ?? [])
                .FirstOrDefault(user => user.Id == peerId);

            Check("这一下顺带把他的账号角色也升成了班主任（一次动作两件事）",
                peerAfterHeadGrant is { Role: UserRoles.HeadTeacher },
                peerAfterHeadGrant is null ? "(账号不见了)" : $"role={peerAfterHeadGrant.Role}，姓名={peerAfterHeadGrant.DisplayName}");

            var bindingsAfterPeerHead = await consoleHttp.GetFromJsonAsync<List<ConsoleBinding>>(
                $"{root}{RelayPaths.ConsoleBindings}", JsonOptions) ?? [];
            var peerHeadRow = bindingsAfterPeerHead.FirstOrDefault(row => row.UserId == peerId && row.Uuid == uuid);

            Check("授权列表里这条也变成了班主任授权",
                peerHeadRow is { AsHeadTeacher: true },
                peerHeadRow is null
                    ? "没有这条记录"
                    : $"{peerHeadRow.UserDisplayName} → {peerHeadRow.ClassroomName}，asHeadTeacher={peerHeadRow.AsHeadTeacher}");

            // 班主任的任免只在管理员手里。这条不挡的话，一位班主任可以给自己拉一个
            // "班主任同伴"，然后两人的权限互相兜底 —— 谁都收不掉谁。
            var revokePeerHead = await headApi.DeleteAsync(
                $"{root}{RelayPaths.HeadTeacherBindings}?classroomUuid={Uri.EscapeDataString(uuid)}&userId={Uri.EscapeDataString(peerId)}");
            var revokePeerHeadBody = await revokePeerHead.Content.ReadAsStringAsync();

            Check("班主任收不掉另一位班主任的权限（非 2xx，并说明原因）",
                !revokePeerHead.IsSuccessStatusCode && revokePeerHeadBody.Contains("\"error\""),
                $"HTTP {(int)revokePeerHead.StatusCode}：{Trim(revokePeerHeadBody)}");

            var revokeSelf = await headApi.DeleteAsync(
                $"{root}{RelayPaths.HeadTeacherBindings}?classroomUuid={Uri.EscapeDataString(uuid)}&userId={Uri.EscapeDataString(headId)}");
            var revokeSelfBody = await revokeSelf.Content.ReadAsStringAsync();

            Check("班主任也收不掉自己的班主任权限（非 2xx）",
                !revokeSelf.IsSuccessStatusCode && revokeSelfBody.Contains("\"error\""),
                $"HTTP {(int)revokeSelf.StatusCode}：{Trim(revokeSelfBody)}");

            // ---------- 8j. 名单按班隔离 ----------
            //
            // 一位老师教好几个班，每个班的名单完全不同。同步时不带班级的话，
            // 他在二班传的名单会出现在三班 —— 老师那边看到的是"三班的名单里
            // 全是二班的人"，而这在课堂上当场就会出丑。
            var rosterForA = new StudentRoster
            {
                Name = "二班名单",
                Students =
                [
                    new Student { Id = "a1", Name = "二班甲", StudentNo = "20260101" },
                    new Student { Id = "a2", Name = "二班乙", StudentNo = "20260102" },
                ],
            };

            var rosterForB = new StudentRoster
            {
                Name = "三班名单",
                Students =
                [
                    new Student { Id = "b1", Name = "三班甲", StudentNo = "20260201" },
                ],
            };

            var putRosterA = await teacherApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([rosterForA], rosterForA.Id, ClassroomUuid: uuid), JsonOptions);
            var putRosterABody = await putRosterA.Content.ReadFromJsonAsync<TeacherRosterPutResult>(JsonOptions);

            Check("同步名单时可以指明是给哪一间班的",
                putRosterA.IsSuccessStatusCode && putRosterABody is { Ok: true, Rosters: 1 },
                $"HTTP {(int)putRosterA.StatusCode}：{putRosterABody?.Error ?? $"服务器上这一班有 {putRosterABody?.Rosters} 份名单"}");

            var readRosterA = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("按班级读回来的就是这一班那一份（来源是「我自己导入的」、可以继续上传）",
                readRosterA is { Rosters.Count: 1, Source: ClassroomRosterRules.Own, CanUpload: true }
                && readRosterA.Rosters[0].Name == "二班名单",
                readRosterA is null
                    ? "(没读到)"
                    : $"classroomUuid={readRosterA.ClassroomUuid}，source={readRosterA.Source}，canUpload={readRosterA.CanUpload}，{readRosterA.Rosters.Count} 份：{string.Join("、", readRosterA.Rosters.Select(roster => roster.Name))}");

            var readRosterB = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(otherSettings.Uuid)}", JsonOptions);

            // 这里不能断"另一间一定是空的"：这个账号在前面的用例里同步过一份
            // **没带班级**的老记录（升级前客户端就是这么传的），而服务器对这种情况
            // 有意做了兼容回落（见 RosterStore.Get：精确匹配不到就退回那一份），
            // 为的是"老师升级后第一次同步之前，界面上仍然是原来那份名单"。
            // 所以这一条断的是隔离本身：给这一班同步的那份，绝不能出现在另一班里。
            Check("给这一间同步的那份不会出现在另一间里",
                readRosterA is { Rosters.Count: 1 }
                && readRosterB?.Rosters.Any(roster => roster.Name == readRosterA.Rosters[0].Name) != true,
                readRosterB is null
                    ? "(没读到)"
                    : $"一班={string.Join("、", readRosterA?.Rosters.Select(roster => roster.Name) ?? [])}；"
                    + $"另一班 source={readRosterB.Source}，{readRosterB.Rosters.Count} 份：{string.Join("、", readRosterB.Rosters.Select(roster => roster.Name))}");

            // 上面那条被老记录挡住了，所以"从没同步过的班必须是空的 rosters: []"
            // 得换一个名下一条记录都没有的账号来压 —— 管理员用的就是同一套接口。
            using var cleanRosterApi = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            cleanRosterApi.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, adminToken);

            var cleanRosterA = new StudentRoster
            {
                Name = "管理员同步的一班名单",
                Students = [new Student { Id = "m1", Name = "管理员名单甲", StudentNo = "20260401" }],
            };

            var cleanPutA = await cleanRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([cleanRosterA], cleanRosterA.Id, ClassroomUuid: uuid), JsonOptions);

            var cleanReadA = await cleanRosterApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);
            var cleanReadB = await cleanRosterApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(otherSettings.Uuid)}", JsonOptions);

            Check("从没同步过名单的那个班，读回来就是空的 rosters: []（不是别的班那份）",
                cleanPutA.IsSuccessStatusCode
                && cleanReadA is { Rosters.Count: 1 } && cleanReadA.Rosters[0].Name == "管理员同步的一班名单"
                && cleanReadB is { Rosters.Count: 0, Source: ClassroomRosterRules.Empty, CanUpload: true },
                $"一班={string.Join("、", cleanReadA?.Rosters.Select(roster => roster.Name) ?? [])}；"
                + $"另一班 source={cleanReadB?.Source}，{cleanReadB?.Rosters.Count ?? -1} 份："
                + $"{string.Join("、", cleanReadB?.Rosters.Select(roster => roster.Name) ?? [])}");

            // 再给另一间同步一份：回到第一间去读，得到的必须还是第一间自己那一份。
            var cleanRosterB = new StudentRoster
            {
                Name = "管理员同步的另一班名单",
                Students = [new Student { Id = "m2", Name = "管理员名单乙", StudentNo = "20260402" }],
            };

            await cleanRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([cleanRosterB], cleanRosterB.Id, ClassroomUuid: otherSettings.Uuid), JsonOptions);

            var cleanReadAAgain = await cleanRosterApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("反过来给另一间同步，也不会把这一间那份顶掉",
                cleanReadAAgain is { Rosters.Count: 1 } && cleanReadAAgain.Rosters[0].Name == "管理员同步的一班名单",
                cleanReadAAgain is null
                    ? "(没读到)"
                    : $"{cleanReadAAgain.Rosters.Count} 份：{string.Join("、", cleanReadAAgain.Rosters.Select(roster => roster.Name))}");

            var putRosterB = await teacherApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([rosterForB], rosterForB.Id, ClassroomUuid: otherSettings.Uuid), JsonOptions);
            var putRosterBBody = await putRosterB.Content.ReadFromJsonAsync<TeacherRosterPutResult>(JsonOptions);

            Check("给另一间班也传一份名单（两边的名单各自独立）",
                putRosterB.IsSuccessStatusCode && putRosterBBody is { Ok: true, Rosters: 1 },
                $"HTTP {(int)putRosterB.StatusCode}：{putRosterBBody?.Error ?? $"服务器上这一班有 {putRosterBBody?.Rosters} 份名单"}");

            var readRosterAAfterB = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);
            var readRosterBAfterB = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(otherSettings.Uuid)}", JsonOptions);

            Check("在另一间班传名单之后，这一班那份原封不动",
                readRosterAAfterB is { Rosters.Count: 1 } && readRosterAAfterB.Rosters[0].Name == "二班名单"
                && readRosterBAfterB is { Rosters.Count: 1 } && readRosterBAfterB.Rosters[0].Name == "三班名单",
                $"二班={string.Join("、", readRosterAAfterB?.Rosters.Select(roster => roster.Name) ?? [])}；"
                + $"三班={string.Join("、", readRosterBAfterB?.Rosters.Select(roster => roster.Name) ?? [])}");

            // 老客户端同步时不带 classroomUuid，那些数据存在"没有指定班级"的那一格里，
            // 不能被新的按班规则顺手清掉或改写。
            var legacyRoster = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}", JsonOptions);

            Check("不带班级的那一份仍然只属于它自己（老客户端的数据没被打乱）",
                legacyRoster is { Rosters.Count: 1 } && legacyRoster.Rosters[0].Name == "三年二班",
                legacyRoster is null
                    ? "(没读到)"
                    : $"{legacyRoster.Rosters.Count} 份：{string.Join("、", legacyRoster.Rosters.Select(roster => roster.Name))}");

            // ---------- 8k. 班主任的班级统一名单与「强制」开关 ----------
            //
            // 一个班到底按哪份名单叫人，必须只有一个答案：班主任传了一份并设为强制之后，
            // 任课老师自己那份就压不过它。这条规则的价值全在"强制"上，
            // 所以下面每一条都对着"强制"之后的行为压。
            using var headRosterApi = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            headRosterApi.DefaultRequestHeaders.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, headToken);

            var deniedReadRoomRoster = await teacherApi.GetAsync(
                $"{root}{RelayPaths.TeacherClassroomRoster}?classroomUuid={Uri.EscapeDataString(uuid)}");
            var deniedReadRoomRosterBody = await deniedReadRoomRoster.Content.ReadAsStringAsync();

            Check("任课老师看不到本班的统一名单（403）",
                (int)deniedReadRoomRoster.StatusCode == 403 && deniedReadRoomRosterBody.Contains("\"error\""),
                $"HTTP {(int)deniedReadRoomRoster.StatusCode}：{Trim(deniedReadRoomRosterBody)}");

            var deniedWriteRoomRoster = await teacherApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherClassroomRoster}",
                new ClassroomRosterUpload(ClassroomUuid: uuid, Rosters: [rosterForA], Enforced: true), JsonOptions);
            var deniedWriteRoomRosterBody = await deniedWriteRoomRoster.Content.ReadAsStringAsync();

            Check("任课老师也不能上传本班的统一名单、更不能自己设强制（403）",
                (int)deniedWriteRoomRoster.StatusCode == 403 && deniedWriteRoomRosterBody.Contains("\"error\""),
                $"HTTP {(int)deniedWriteRoomRoster.StatusCode}：{Trim(deniedWriteRoomRosterBody)}");

            // 空名单 + 强制 = 这个班谁都叫不了。宁可当场拒绝，也别留下这种状态 ——
            // 它在界面上看起来一切正常，直到有老师说"我一个学生都叫不出来"。
            var emptyHeadRoster = await headRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherClassroomRoster}",
                new ClassroomRosterUpload(ClassroomUuid: uuid, Rosters: [], Enforced: true), JsonOptions);
            var emptyHeadRosterBody = await emptyHeadRoster.Content.ReadFromJsonAsync<ClassroomRosterPutResult>(JsonOptions);

            Check("空名单配「强制」被拒（400）",
                (int)emptyHeadRoster.StatusCode == 400 && emptyHeadRosterBody?.Ok == false,
                $"HTTP {(int)emptyHeadRoster.StatusCode}：{Trim(emptyHeadRosterBody?.Error ?? "(没有说明)")}");

            // 先按 CSV 传一遍：控制台上传班级名单走的就是这条路，
            // 与老师导入名单用的是同一个解析器（表头、空行、从 Excel 粘过来的都认）。
            var headRosterCsv = string.Join('\n',
                "姓名,学号",
                "一班甲,20260301",
                "一班乙,20260302",
                "一班丙,20260303");

            var headRosterCsvPut = await headRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherClassroomRoster}",
                new ClassroomRosterUpload(ClassroomUuid: uuid, CsvText: headRosterCsv, RosterName: "一班统一名单"), JsonOptions);
            var headRosterCsvBody = await headRosterCsvPut.Content.ReadFromJsonAsync<ClassroomRosterPutResult>(JsonOptions);

            Check("班主任能直接贴一份 CSV 作为本班统一名单（这时还没强制）",
                headRosterCsvPut.IsSuccessStatusCode
                && headRosterCsvBody is { Ok: true, Students: 3, Rosters: 1, Enforced: false },
                $"HTTP {(int)headRosterCsvPut.StatusCode}：ok={headRosterCsvBody?.Ok}，{headRosterCsvBody?.Students} 名学生，"
                + $"{headRosterCsvBody?.Rosters} 份，强制={headRosterCsvBody?.Enforced}");

            var headRoster = new StudentRoster
            {
                Name = "一班统一名单",
                Students =
                [
                    new Student { Id = "h1", Name = "一班甲", StudentNo = "20260301" },
                    new Student { Id = "h2", Name = "一班乙", StudentNo = "20260302" },
                    new Student { Id = "h3", Name = "一班丙", StudentNo = "20260303" },
                ],
            };

            // 对象形式再传一遍：与客户端「同步名单」用的是同一个请求体，
            // 两种形式都要能用，否则控制台与 App 只有一个能改班级名单。
            var headRosterPut = await headRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherClassroomRoster}",
                new ClassroomRosterUpload(ClassroomUuid: uuid, Rosters: [headRoster], ActiveRosterId: headRoster.Id), JsonOptions);
            var headRosterPutBody = await headRosterPut.Content.ReadFromJsonAsync<ClassroomRosterPutResult>(JsonOptions);

            Check("名单也能用对象形式传（与客户端同一个请求体）",
                headRosterPut.IsSuccessStatusCode
                && headRosterPutBody is { Ok: true, Students: 3, Rosters: 1, Enforced: false },
                $"HTTP {(int)headRosterPut.StatusCode}：ok={headRosterPutBody?.Ok}，{headRosterPutBody?.Students} 名学生，"
                + $"{headRosterPutBody?.Rosters} 份，强制={headRosterPutBody?.Enforced}");

            var headRosterView = await headRosterApi.GetFromJsonAsync<ClassroomRosterSnapshot>(
                $"{root}{RelayPaths.TeacherClassroomRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("班主任读回来的就是自己刚传的那份，并记下是谁改的",
                headRosterView is { Rosters.Count: 1, Enforced: false } && headRosterView.UpdatedByName == "李班主任"
                && headRosterView.Rosters[0].Name == "一班统一名单"
                && headRosterView.Rosters[0].Students.Count == 3,
                headRosterView is null
                    ? "(没读到)"
                    : $"{headRosterView.Rosters.Count} 份、{headRosterView.Rosters.Sum(roster => roster.Students.Count)} 名学生，"
                    + $"修改人={headRosterView.UpdatedByName ?? "(空)"}");

            // 还没设强制：任课老师自己那份仍然优先。这就把"强制"这个开关的作用范围钉死了 ——
            // 它管的不是"有没有这份名单"，而是"这份名单算不算数"。
            var beforeEnforce = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("还没强制时，任课老师用的仍是自己那份",
                beforeEnforce is { Source: ClassroomRosterRules.Own, CanUpload: true }
                && beforeEnforce.Rosters.Count == 1 && beforeEnforce.Rosters[0].Name == "二班名单",
                beforeEnforce is null
                    ? "(没读到)"
                    : $"source={beforeEnforce.Source}，canUpload={beforeEnforce.CanUpload}，{beforeEnforce.Rosters.Count} 份：{string.Join("、", beforeEnforce.Rosters.Select(roster => roster.Name))}");

            var enforceRoster = await headRosterApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherClassroomRoster}",
                new ClassroomRosterUpload(ClassroomUuid: uuid, Enforced: true), JsonOptions);
            var enforceRosterBody = await enforceRoster.Content.ReadFromJsonAsync<ClassroomRosterPutResult>(JsonOptions);

            Check("只改「强制」这一个开关时，名单本身不动",
                enforceRoster.IsSuccessStatusCode
                && enforceRosterBody is { Ok: true, Students: 3, Rosters: 1, Enforced: true },
                $"HTTP {(int)enforceRoster.StatusCode}：ok={enforceRosterBody?.Ok}，{enforceRosterBody?.Students} 名学生，强制={enforceRosterBody?.Enforced}");

            // ---------- 8l. 强制之后，任课老师看到与能用到的都必须是班主任那份 ----------
            //
            // 这里压的是一个**真实存在过的 bug**：开关生效了、canUpload 也变成 false 了，
            // 但回给老师的名单却是空列表 —— 界面上就成了"这个班一个学生都没有"，
            // 而老师没有任何线索能解释学生去哪儿了。所以这里断的不是"不能上传"，
            // 而是"看到的必须是班主任那份、而且里面真的有人"。
            var forcedRead = await teacherApi.GetFromJsonAsync<TeacherRosterSnapshot>(
                $"{root}{RelayPaths.TeacherRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("强制之后任课老师读到的是班主任那份名单（不是空列表）",
                forcedRead is { Source: ClassroomRosterRules.HeadTeacher, CanUpload: false }
                && forcedRead.Rosters.Count == 1
                && forcedRead.Rosters[0].Students.Count == 3
                && forcedRead.Rosters[0].Students.Any(student => student.Name == "一班甲"),
                forcedRead is null
                    ? "(没读到)"
                    : $"source={forcedRead.Source}，canUpload={forcedRead.CanUpload}，{forcedRead.Rosters.Count} 份、"
                    + $"{forcedRead.Rosters.Sum(roster => roster.Students.Count)} 名学生：{string.Join("、", forcedRead.Rosters.SelectMany(roster => roster.Students).Select(student => student.Name))}");

            var blockedRoster = new StudentRoster
            {
                Name = "想顶掉班主任的那份",
                Students = [new Student { Id = "x1", Name = "不该出现的甲" }],
            };

            var blockedPut = await teacherApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([blockedRoster], blockedRoster.Id, ClassroomUuid: uuid), JsonOptions);
            var blockedPutBody = await blockedPut.Content.ReadFromJsonAsync<TeacherRosterPutResult>(JsonOptions);

            // 这里刻意断 HTTP 200 + ok:false：这是这个端点既有的约定（界面按 ok 判断，
            // 不按状态码），换成 403 反倒会让老客户端把它当成一次网络故障。
            Check("强制之后任课老师上传自己被拒（HTTP 200 + ok:false，并说明是班主任管的）",
                (int)blockedPut.StatusCode == 200 && blockedPutBody is { Ok: false }
                && blockedPutBody.Error?.Contains("班主任") == true,
                $"HTTP {(int)blockedPut.StatusCode}：ok={blockedPutBody?.Ok}，error={Trim(blockedPutBody?.Error ?? "(没有说明)")}");

            var afterBlockedPut = await headRosterApi.GetFromJsonAsync<ClassroomRosterSnapshot>(
                $"{root}{RelayPaths.TeacherClassroomRoster}?classroomUuid={Uri.EscapeDataString(uuid)}", JsonOptions);

            Check("被拒的那次上传没有改动任何名单",
                afterBlockedPut is { Rosters.Count: 1, Enforced: true }
                && afterBlockedPut.Rosters[0].Name == "一班统一名单"
                && afterBlockedPut.Rosters[0].Students.Count == 3,
                afterBlockedPut is null
                    ? "(没读到)"
                    : $"{afterBlockedPut.Rosters.Count} 份、{afterBlockedPut.Rosters.Sum(roster => roster.Students.Count)} 名学生，"
                    + $"强制={afterBlockedPut.Enforced}，名单名={string.Join("、", afterBlockedPut.Rosters.Select(roster => roster.Name))}");

            var forcedClassroomList = await teacherApi.GetFromJsonAsync<List<TeacherClassroomDto>>(
                $"{root}{RelayPaths.TeacherClassrooms}?accountId={Uri.EscapeDataString(teacherSettings.UserId!)}", JsonOptions) ?? [];
            var forcedClassroom = forcedClassroomList.FirstOrDefault(item => item.Uuid == uuid);

            Check("班级列表里也标出了「这个班用班主任那份、而且是强制的」",
                forcedClassroom is { RosterSource: ClassroomRosterRules.HeadTeacher, RosterEnforced: true },
                forcedClassroom is null
                    ? "(列表里没有这一间)"
                    : $"rosterSource={forcedClassroom.RosterSource}，rosterEnforced={forcedClassroom.RosterEnforced}");

            // 强制只作用于这一间：另一间班没有班主任名单，他照样能传自己的。
            var rosterBSecond = new StudentRoster
            {
                Name = "三班名单（第二版）",
                Students = [new Student { Id = "b2", Name = "三班乙", StudentNo = "20260202" }],
            };

            var putBAfterEnforce = await teacherApi.PutAsJsonAsync($"{root}{RelayPaths.TeacherRoster}",
                new TeacherRosterUpload([rosterBSecond], rosterBSecond.Id, ClassroomUuid: otherSettings.Uuid), JsonOptions);
            var putBAfterEnforceBody = await putBAfterEnforce.Content.ReadFromJsonAsync<TeacherRosterPutResult>(JsonOptions);

            Check("强制只作用于那一间班：别的班他照样能传自己的名单",
                putBAfterEnforce.IsSuccessStatusCode && putBAfterEnforceBody is { Ok: true },
                $"HTTP {(int)putBAfterEnforce.StatusCode}：{putBAfterEnforceBody?.Error ?? $"这一班现在有 {putBAfterEnforceBody?.Rosters} 份名单"}");

            var roomsAfterEnforce = await headApi.GetFromJsonAsync<List<HeadTeacherClassroomDto>>(
                $"{root}{RelayPaths.HeadTeacherClassrooms}", JsonOptions) ?? [];
            var roomAfterEnforce = roomsAfterEnforce.FirstOrDefault(item => item.Uuid == uuid);

            Check("班主任的班级列表里也标出了「有统一名单 / 已强制 / 什么时候改的」",
                roomAfterEnforce is { HasRoster: true, Enforced: true, RosterUpdatedAt: not null },
                roomAfterEnforce is null
                    ? $"列表里没有这一间（共 {roomsAfterEnforce.Count} 间）"
                    : $"hasRoster={roomAfterEnforce.HasRoster}，enforced={roomAfterEnforce.Enforced}，"
                    + $"updatedAt={roomAfterEnforce.RosterUpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }

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
    /// 「已保存的教室」这个列表本身的规矩：去重、排序、上限。
    ///
    /// 纯本地逻辑，不需要服务器 —— 但它决定了老师换班上课时列表里还剩哪几间。
    /// 写错的表现（同一间出现两条、刚绑过的那间被挤掉）都要等人用上一阵子才会发现，
    /// 所以这里直接压。
    /// </summary>
    private static void AssertSavedClassroomList()
    {
        static BoundClassroom Make(string uuid, string name, int minute)
            => new(uuid, name, new DateTimeOffset(2026, 9, 24, 8, minute, 0, TimeSpan.Zero));

        var list = new List<BoundClassroom>();

        TeacherRelaySettings.Remember(list, Make("A", "三年二班", 0));
        TeacherRelaySettings.Remember(list, Make("B", "三年三班", 1));
        Check("最新绑定的排在最前", list[0].Uuid == "B", $"首条={list[0].Name}");

        // 同一间再次绑定：只留一条，并回到最前
        TeacherRelaySettings.Remember(list, Make("A", "三年二班", 2));
        Check("重复绑定只留一条", list.Count == 2, $"共 {list.Count} 条");
        Check("重复绑定的那间回到最前", list[0].Uuid == "A", $"首条={list[0].Name}");

        // 大小写不同的 UUID 是同一间教室
        TeacherRelaySettings.Remember(list, Make("a", "三年二班", 3));
        Check("UUID 大小写不同仍算同一间", list.Count == 2, $"共 {list.Count} 条");

        // 上限：塞满再塞，最旧的被丢掉，且总条数不涨
        for (var i = 0; i < TeacherRelaySettings.MaxSavedClassrooms + 5; i++)
        {
            TeacherRelaySettings.Remember(list, Make($"X{i}", $"教室 {i}", i));
        }

        Check($"最多保留 {TeacherRelaySettings.MaxSavedClassrooms} 间",
            list.Count == TeacherRelaySettings.MaxSavedClassrooms,
            $"共 {list.Count} 条");

        var lastAdded = $"X{TeacherRelaySettings.MaxSavedClassrooms + 4}";
        Check("超出上限时留下的是最近绑定的那几间",
            list[0].Uuid == lastAdded,
            $"首条={list[0].Name}");

        // 落盘再读回来，确认这几个字段真的能存下来（record 用参数化构造，字段名写错会静默丢）
        var path = Path.Combine(LocalSettings.Directory, "e2e-teacher-roundtrip.json");
        var settings = new TeacherRelaySettings { ServerUrl = "https://relay.example.com" };
        TeacherRelaySettings.Remember(settings.RecentClassrooms,
            new BoundClassroom("UUID-1", "三年二班", DateTimeOffset.UtcNow, "https://relay.example.com", "SECRET-1"));

        LocalSettings.Save("e2e-teacher-roundtrip.json", settings);
        var back = LocalSettings.Load("e2e-teacher-roundtrip.json", static () => new TeacherRelaySettings());
        File.Delete(path);

        Check("口令能落盘再读回来",
            back.RecentClassrooms.Count == 1 && back.RecentClassrooms[0].Secret == "SECRET-1",
            back.RecentClassrooms.Count == 0 ? "读回来是空的" : $"口令={(string.IsNullOrEmpty(back.RecentClassrooms[0].Secret) ? "(丢失)" : "在")}");

        Check("服务器地址能落盘再读回来",
            back.RecentClassrooms.Count == 1 && back.RecentClassrooms[0].ServerUrl == "https://relay.example.com",
            back.RecentClassrooms.Count == 0 ? "读回来是空的" : $"地址={back.RecentClassrooms[0].ServerUrl ?? "(丢失)"}");
    }

    /// <summary>
    /// 定时通知：到点的发、没到的不发、过期太久的不补发、发过的不重复发。
    ///
    /// 这四条里最要紧的是最后两条。补发一条"下课前五分钟提醒交作业"，
    /// 会在下一节课上突然喊一句不着边际的话；而重复发则是同一条提醒响两遍 ——
    /// 两者都不会报错，只会让教室里的人觉得这软件坏了。
    /// </summary>
    private static async Task AssertSchedulerAsync()
    {
        var settings = new TeacherScheduleSettings();
        var now = DateTimeOffset.Now;

        var due = new ScheduledShout { SendAt = now.AddSeconds(-5), Text = "到点了" };
        var future = new ScheduledShout { SendAt = now.AddMinutes(10), Text = "还没到" };
        var missed = new ScheduledShout { SendAt = now.AddHours(-2), Text = "早就过了" };

        settings.Add(future);
        settings.Add(missed);
        settings.Add(due);

        Check("待发列表按时间排好序",
            settings.Items.Select(i => i.Text).SequenceEqual(["早就过了", "到点了", "还没到"]),
            string.Join(" → ", settings.Items.Select(i => i.Text)));

        var sent = new List<string>();

        // 构造时会自己扫一遍"过期"（sendDue: false），所以我们传进去的三个任务里
        // 那条早就过期的会被立刻标成错过 —— 这正是应用启动时该发生的事。
        using var scheduler = new ShoutScheduler(settings)
        {
            SendAsync = item =>
            {
                lock (sent)
                {
                    sent.Add(item.Text);
                }

                return Task.FromResult<(bool, string?)>((true, "已发"));
            },
        };

        Check("启动时就把过期太久的标成错过，且不补发",
            settings.History.Any(i => i.Text == "早就过了") && !sent.Contains("早就过了"),
            $"历史里 {settings.History.Count} 条，已发 {sent.Count} 条");

        scheduler.Sweep(now);
        await Task.Delay(400);

        Check("到点的任务被发出去", sent.Contains("到点了"),
            sent.Count == 0 ? "一条都没发" : string.Join("、", sent));

        Check("还没到的不发", !sent.Contains("还没到"), "未来那条仍在待发列表");

        Check("发出去的任务移进历史", settings.History.Any(i => i.Text == "到点了"),
            $"历史 {settings.History.Count} 条，待发 {settings.Items.Count} 条");

        // 再扫一遍：发过的不该再发一次
        scheduler.Sweep(now.AddSeconds(1));
        await Task.Delay(300);

        Check("已经发过的不重复发",
            sent.Count(text => text == "到点了") == 1,
            $"「到点了」发了 {sent.Count(text => text == "到点了")} 次");

        // 取消掉的任务也不再发
        var cancelled = new ScheduledShout { SendAt = now.AddSeconds(-1), Text = "已取消", Cancelled = true };
        settings.Add(cancelled);
        scheduler.Sweep(now.AddSeconds(1));
        await Task.Delay(300);

        Check("取消掉的任务不会发", !sent.Contains("已取消"), "已取消的那条没发出去");

        // 上限：列表不会无限长
        for (var i = 0; i < TeacherScheduleSettings.MaxItems + 5; i++)
        {
            settings.Add(new ScheduledShout { SendAt = now.AddHours(i + 1), Text = $"第 {i} 条" });
        }

        Check($"待发最多保留 {TeacherScheduleSettings.MaxItems} 条",
            settings.Items.Count <= TeacherScheduleSettings.MaxItems,
            $"{settings.Items.Count} 条");
    }

    /// <summary>
    /// 学生名单的导入与标识格式。
    ///
    /// 名单是老师从教务系统导出、或在 Excel 里手打的一份表，
    /// 所以解析必须宽容（表头、空行、注释、只有姓名）。
    /// 而"名字（学号，简写，小组）"这个格式是给学生看的 ——
    /// 没填的字段不能留下空括号，否则教室里那块屏上会满屏是括号。
    /// </summary>
    private static void AssertRosterParsing()
    {
        const string input = """
            姓名,学号,简写,小组,性别
            张三,20250101,小张,A组,男
            李四,20250102,,B组,女
            王五
            ,20250104,,
            # 这是注释
            赵六,20250105,六六,,男生
            """;

        var result = RosterCsv.Parse(input, "三年二班");

        Check("名单能解析出来", result.Ok, result.Roster is null ? "没解析出名单" : $"{result.Roster.Students.Count} 名学生");

        var students = result.Roster?.Students ?? [];

        Check("表头与注释行不计入学生", students.Count == 4, $"共 {students.Count} 人：{string.Join("、", students.Select(s => s.Name))}");

        Check("没有姓名的行被跳过并说明原因",
            result.SkippedLines.Any(line => line.Contains("姓名", StringComparison.Ordinal)),
            result.SkippedLines.Count == 0 ? "没有任何跳过说明" : result.SkippedLines[0]);

        Check("只有姓名的行也能导入",
            students.Any(student => student.Name == "王五"),
            "王五在名单里");

        Check("标识格式是 姓名（学号，简写，小组）",
            students.FirstOrDefault(s => s.Name == "张三")?.Label == "张三（20250101，小张，A组）",
            students.FirstOrDefault(s => s.Name == "张三")?.Label ?? "(缺)");

        Check("只填了小组时也带上括号",
            students.FirstOrDefault(s => s.Name == "李四")?.Label == "李四（20250102，B组）",
            students.FirstOrDefault(s => s.Name == "李四")?.Label ?? "(缺)");

        Check("没填的可选字段不留空括号",
            students.FirstOrDefault(s => s.Name == "王五")?.Label == "王五",
            students.FirstOrDefault(s => s.Name == "王五")?.Label ?? "(缺)");

        Check("小组去重后按出现顺序列出",
            (result.Roster?.Groups ?? []).SequenceEqual(["A组", "B组"]),
            string.Join("、", result.Roster?.Groups ?? []));

        // 空输入不能崩，也不能产出一份空名单
        Check("空文本不会产出一份空名单",
            !RosterCsv.Parse("", "空").Ok && !RosterCsv.Parse(null, "空").Ok,
            "两次都返回了失败");

        // Excel 在字段里出现逗号时会自动加引号。若按逗号硬切，这一行会从
        // 引号里的那个逗号处裂开，后面几列**全部错位**（学号变成小组这一类），
        // 而且是静默的：只有到教室里看见"李四（A组）"才会发现。
        var quoted = RosterCsv.Parse("\"张三, 小张\",20250101,,A组", "带逗号的引号").Roster?.Students;

        Check("引号里的逗号不会把这一行切开",
            quoted is { Count: 1 } && quoted[0].Name == "张三, 小张",
            quoted is { Count: 1 } ? $"姓名=「{quoted[0].Name}」" : $"解析出 {quoted?.Count ?? 0} 人");

        Check("引号字段之后的几列仍然对得上",
            quoted is { Count: 1 } && quoted[0].StudentNo == "20250101" && quoted[0].Group == "A组",
            quoted is { Count: 1 } ? $"学号={quoted[0].StudentNo ?? "(空)"}，小组={quoted[0].Group ?? "(空)"}" : "取不到该学生");

        // Excel 另存为 CSV 时会把每一格都套上引号
        var excel = RosterCsv.Parse("\"李四\",\"20250102\",\"小李\",\"B组\"", "整行带引号").Roster?.Students;

        Check("整行都套着引号时也能读（Excel 另存为 CSV 的样子）",
            excel is { Count: 1 } && excel[0].Label == "李四（20250102，小李，B组）",
            excel is { Count: 1 } ? excel[0].Label : $"解析出 {excel?.Count ?? 0} 人");

        Check("引号里的一对引号表示一个引号本身",
            RosterCsv.Parse("\"他说\"\"你好\"\"\",20250103", "带引号的名字").Roster?.Students is { Count: 1 } one
                && one[0].Name == "他说\"你好\"",
            RosterCsv.Parse("\"他说\"\"你好\"\"\",20250103", "带引号的名字").Roster?.Students.FirstOrDefault()?.Name ?? "(缺)");

        Check("没配对的引号不会把整行丢掉",
            RosterCsv.Parse("\"王五,20250105", "没配对的引号").Roster?.Students is { Count: 1 } half
                && half[0].Name == "王五,20250105",
            RosterCsv.Parse("\"王五,20250105", "没配对的引号").Roster?.Students.FirstOrDefault()?.Name ?? "(整行被丢掉了)");

        // ---------- 第五列：性别（随机叫人按性别筛的时候要用） ----------

        Check("第五列读成性别",
            students.FirstOrDefault(s => s.Name == "张三")?.Gender == "男"
                && students.FirstOrDefault(s => s.Name == "李四")?.Gender == "女",
            $"张三={students.FirstOrDefault(s => s.Name == "张三")?.Gender ?? "(空)"}，"
                + $"李四={students.FirstOrDefault(s => s.Name == "李四")?.Gender ?? "(空)"}");

        Check("性别的写法被归一化（男生 → 男）",
            students.FirstOrDefault(s => s.Name == "赵六")?.Gender == "男",
            students.FirstOrDefault(s => s.Name == "赵六")?.Gender ?? "(空)");

        Check("没填性别的行不会被猜一个",
            students.FirstOrDefault(s => s.Name == "王五")?.Gender is null,
            students.FirstOrDefault(s => s.Name == "王五")?.Gender ?? "(空)");

        Check("性别不混进显示用的标识里",
            students.FirstOrDefault(s => s.Name == "张三")?.Label == "张三（20250101，小张，A组）",
            students.FirstOrDefault(s => s.Name == "张三")?.Label ?? "(缺)");

        // 各种写法都要认，认不出来的留空（而不是硬塞成"男"）
        Check("性别的常见写法都认",
            RosterCsv.NormalizeGender("M") == "男"
                && RosterCsv.NormalizeGender("male") == "男"
                && RosterCsv.NormalizeGender("1") == "男"
                && RosterCsv.NormalizeGender("F") == "女"
                && RosterCsv.NormalizeGender("female") == "女"
                && RosterCsv.NormalizeGender("0") == "女",
            $"M={RosterCsv.NormalizeGender("M") ?? "(空)"}，female={RosterCsv.NormalizeGender("female") ?? "(空)"}，1={RosterCsv.NormalizeGender("1") ?? "(空)"}");

        Check("认不出的性别留空，不瞎猜",
            RosterCsv.NormalizeGender("保密") is null && RosterCsv.NormalizeGender("") is null,
            $"保密 → {RosterCsv.NormalizeGender("保密") ?? "(空)"}");

        Check("导入时每人都有一个隐形的时间因子，初值为 0",
            students.All(s => s.TimeFactor == 0 && s.FactorSetAt is null),
            students.Count == 0 ? "(没有学生)" : $"最大 {students.Max(s => s.TimeFactor)}");

        // 表头可有可无：老师从教务系统里复制出来的常常没有表头
        var headerless = RosterCsv.Parse("张三,20250101,小张,A组,男", "没有表头").Roster?.Students;

        Check("没有表头也能导入（第一行不会被当成表头吃掉）",
            headerless is { Count: 1 } && headerless[0].Name == "张三",
            headerless is { Count: 1 } ? headerless[0].Label : $"解析出 {headerless?.Count ?? 0} 人");
    }

    /// <summary>
    /// 直接导入 Excel（.xlsx）。
    ///
    /// 老师手里的名单九成是表格，而"先另存为 CSV"这道工序恰恰最容易出错
    /// （选错编码、Excel 把学号前面的 0 吃掉）。所以这条路要能走通，
    /// 而且读出来的结果必须和 CSV **完全一致** —— 两种入口给出两个答案是最坏的。
    ///
    /// 这里自己拼一个最小但结构完整的 xlsx（Excel 写出来的东西的样子：
    /// 文本走 sharedStrings、纯数字存成数字），不依赖任何写表格的库。
    /// </summary>
    private static void AssertRosterExcelImport()
    {
        Check("按扩展名认出表格（而不是当成文本）",
            RosterExcel.LooksLikeExcel("名单.xlsx")
                && RosterExcel.LooksLikeExcel("名单.XLS")
                && !RosterExcel.LooksLikeExcel("名单.csv")
                && !RosterExcel.LooksLikeExcel(null),
            "xlsx/xls 认表格，csv 与空名不认");

        Check("导入支持的扩展名覆盖 csv 与表格",
            RosterFile.Extensions.Contains(".csv") && RosterFile.Extensions.Contains(".xlsx"),
            string.Join("、", RosterFile.Extensions));

        // 学号是**数字**单元格：Excel 里敲 20250101 默认就是这个样子。
        // 直接 ToString 会得到 "20250101.0"，教室里就会听见"二零二五零一零一点零"。
        var rows = new List<object?[]>
        {
            // 这里刻意写全 new object?[]：对象初始化器里的 [ ] 会被解析成"索引器赋值"，
            // 嵌套的集合表达式在这个位置是有歧义的
            new object?[] { "姓名", "学号", "简写", "小组", "性别" },
            new object?[] { "张三", 20250101d, "小张", "A组", "男" },
            new object?[] { "李四", 20250102d, null, "B组", "女" },
            new object?[] { null, null, null, null, null },
            new object?[] { "王五", 20250105d, "小五", "A组", "男" },
        };

        using var xlsx = BuildXlsx("三年二班名单", rows);

        var result = RosterExcel.Read(xlsx, "名单", "名单.xlsx");
        var students = result.Roster?.Students ?? [];

        Check("表格能读出一份名单", result.Ok && students.Count == 3,
            result.Ok ? $"共 {students.Count} 人" : string.Join(" / ", result.SkippedLines));

        Check("名单名用工作表名（比文件名更贴近内容）",
            result.Roster?.Name == "三年二班名单",
            result.Roster?.Name ?? "(缺)");

        Check("数字单元格里的学号不会带小数点",
            students.FirstOrDefault(s => s.Name == "张三")?.StudentNo == "20250101",
            students.FirstOrDefault(s => s.Name == "张三")?.StudentNo ?? "(空)");

        Check("表格里的性别与小组照读",
            students.FirstOrDefault(s => s.Name == "李四") is { Gender: "女", Group: "B组" },
            students.FirstOrDefault(s => s.Name == "李四") is { } lisi ? $"性别={lisi.Gender ?? "(空)"}，小组={lisi.Group ?? "(空)"}" : "(缺)");

        Check("表格里的空行被跳过",
            students.All(s => !string.IsNullOrWhiteSpace(s.Name)),
            string.Join("、", students.Select(s => s.Name)));

        Check("表格导入的学生也带隐形时间因子（初值 0）",
            students.All(s => s.TimeFactor == 0),
            students.Count == 0 ? "(没有学生)" : $"最大 {students.Max(s => s.TimeFactor)}");

        // 同一个文件按扩展名分派：.csv 走文本、.xlsx 走表格 —— 读法只有一处决定
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("张三,20250101,小张,A组,男"));
        var viaCsv = RosterFile.Read("名单.csv", csv, "名单");

        Check("按扩展名分派：csv 走文本读法",
            viaCsv.Ok && viaCsv.Roster?.Students.FirstOrDefault()?.Label == "张三（20250101，小张，A组）",
            viaCsv.Ok ? viaCsv.Roster!.Students[0].Label : string.Join(" / ", viaCsv.SkippedLines));

        using var xlsx2 = BuildXlsx("Sheet1", [["姓名"], ["张三"]]);
        var viaXlsx = RosterFile.Read("名单.xlsx", xlsx2, "名单");

        Check("按扩展名分派：xlsx 走表格读法",
            viaXlsx.Ok && viaXlsx.Roster?.Students.Count == 1,
            viaXlsx.Ok ? $"{viaXlsx.Roster!.Students.Count} 人" : string.Join(" / ", viaXlsx.SkippedLines));

        // 老师点错文件（或者下载到一半的 .xlsx）不该崩，只该得到一句人话
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("这不是表格，只是一段文字"));

        var broken = RosterExcel.Read(garbage, "坏文件", "坏文件.xlsx");

        Check("改名的文本当成表格读时给一句提示，而不是崩掉",
            !broken.Ok && broken.SkippedLines.Count > 0,
            broken.SkippedLines.Count == 0 ? "(没有任何说明)" : broken.SkippedLines[0]);

        using var empty = new MemoryStream([]);
        Check("空文件也给提示而不是崩掉",
            !RosterExcel.Read(empty, "空", "空.xlsx").Ok,
            "返回了失败");
    }

    /// <summary>
    /// 拼一个最小但结构完整的 .xlsx（Excel 自己写出来的样子）。
    ///
    /// 文本走 sharedStrings、数字存成数字单元格 —— 这两点正是真实文件里最容易
    /// 让导入读错的地方（学号变成 "20250101.0" 就是后者）。
    /// </summary>
    private static MemoryStream BuildXlsx(string sheetName, IReadOnlyList<object?[]> rows)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        var strings = new List<string>();
        var sheetRows = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("sheetData", main));

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("row", main));
            cells.SetAttributeValue("r", r + 1);

            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                var cell = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("c", main));
                cell.SetAttributeValue("r", $"{(char)('A' + c)}{r + 1}");

                switch (value)
                {
                    case null:
                    case "":
                        continue; // 空格子：Excel 也是干脆不写

                    case double number:
                        cell.SetAttributeValue("t", "n");
                        cell.Add(new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("v", main), number.ToString("R")));
                        break;

                    default:
                        var text = value.ToString()!;
                        var index = strings.IndexOf(text);

                        if (index < 0)
                        {
                            strings.Add(text);
                            index = strings.Count - 1;
                        }

                        cell.SetAttributeValue("t", "s");
                        cell.Add(new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("v", main), index));
                        break;
                }

                cells.Add(cell);
            }

            sheetRows.Add(cells);
        }

        var sst = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("sst", main),
            new System.Xml.Linq.XAttribute("count", strings.Count),
            new System.Xml.Linq.XAttribute("uniqueCount", strings.Count));

        foreach (var text in strings)
        {
            var item = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("si", main));
            var t = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("t", main), text);
            t.SetAttributeValue(System.Xml.Linq.XName.Get("space", "http://www.w3.org/XML/1998/namespace"), "preserve");
            item.Add(t);
            sst.Add(item);
        }

        var worksheet = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("worksheet", main), sheetRows);

        var workbook = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("workbook", main),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("sheets", main),
                new System.Xml.Linq.XElement(
                    System.Xml.Linq.XName.Get("sheet", main),
                    new System.Xml.Linq.XAttribute("name", sheetName),
                    new System.Xml.Linq.XAttribute("sheetId", 1),
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XName.Get("id", rel), "rId1"))));

        var contentTypes = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Default"),
                new System.Xml.Linq.XAttribute("Extension", "rels"),
                new System.Xml.Linq.XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Default"),
                new System.Xml.Linq.XAttribute("Extension", "xml"),
                new System.Xml.Linq.XAttribute("ContentType", "application/xml")),
            Override("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
            Override("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
            Override("/xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"));

        var rootRels = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("Relationships", pkg),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Relationship"),
                new System.Xml.Linq.XAttribute("Id", "rId1"),
                new System.Xml.Linq.XAttribute("Type", $"{rel}/officeDocument"),
                new System.Xml.Linq.XAttribute("Target", "xl/workbook.xml")));

        var workbookRels = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("Relationships", pkg),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Relationship"),
                new System.Xml.Linq.XAttribute("Id", "rId1"),
                new System.Xml.Linq.XAttribute("Type", $"{rel}/worksheet"),
                new System.Xml.Linq.XAttribute("Target", "worksheets/sheet1.xml")),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Relationship"),
                new System.Xml.Linq.XAttribute("Id", "rId2"),
                new System.Xml.Linq.XAttribute("Type", $"{rel}/sharedStrings"),
                new System.Xml.Linq.XAttribute("Target", "sharedStrings.xml")));

        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", contentTypes);
            Add(zip, "_rels/.rels", rootRels);
            Add(zip, "xl/workbook.xml", workbook);
            Add(zip, "xl/_rels/workbook.xml.rels", workbookRels);
            Add(zip, "xl/worksheets/sheet1.xml", worksheet);
            Add(zip, "xl/sharedStrings.xml", sst);
        }

        buffer.Position = 0;
        return buffer;

        static System.Xml.Linq.XElement Override(string part, string contentType)
            => new(
                System.Xml.Linq.XName.Get("Override"),
                new System.Xml.Linq.XAttribute("PartName", part),
                new System.Xml.Linq.XAttribute("ContentType", contentType));

        static void Add(ZipArchive zip, string path, System.Xml.Linq.XElement content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);

            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            writer.Write(content.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        }
    }

    /// <summary>
    /// 随机叫人与"隐形的时间因子"。
    ///
    /// 这套规则没法靠看界面验证（因子是刻意不显示的，而且抽出来是随机的），
    /// 所以每一条都必须在这里用可复现的随机源钉住：因子怎么衰减、抽中之后重置到哪、
    /// 范围怎么筛、抽位数越界怎么办。错了的表现都是"看着正常、其实不公平"——
    /// 比如刚叫过的学生下一位还是他，或者衰减窗口不生效。
    /// </summary>
    private static void AssertRandomCall()
    {
        var roster = RosterCsv.Parse(
            "张三,20250101,小张,A组,男\n李四,20250102,,A组,女\n王五,,小五,B组,男\n赵六,,,B组,女",
            "三年二班").Roster;

        if (roster is null)
        {
            Check("随机叫人：名单能解析出来", false, "名单没解析出来，后面的断言无法进行");
            return;
        }

        var now = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.FromHours(8));
        var window = RandomCall.DefaultDecayWindow;

        Check("默认衰减窗口是一节课（40 分钟）",
            window == TimeSpan.FromMinutes(40) && RandomCall.DecayChoices.Contains(window),
            $"默认 {window.TotalMinutes} 分钟，可选 {string.Join("、", RandomCall.DecayChoices.Select(d => d.TotalMinutes))} 分钟");

        // 刚导入的人：因子 0 → 权重拉满
        Check("没被叫过的人因子是 0、权重是 1",
            roster.Students.All(s => RandomCall.EffectiveFactor(s, now, window) == 0
                                     && RandomCall.Weight(s, now, window) == 1),
            $"最大因子 {roster.Students.Max(s => RandomCall.EffectiveFactor(s, now, window))}");

        // 线性衰减：走完半个窗口就掉一半
        var zhang = roster.Students[0];
        zhang.TimeFactor = 1.0;
        zhang.FactorSetAt = now - TimeSpan.FromMinutes(20);

        Check("因子按时间线性衰减（40 分钟走一半 → 0.5）",
            Math.Abs(RandomCall.EffectiveFactor(zhang, now, window) - 0.5) < 1e-9,
            RandomCall.EffectiveFactor(zhang, now, window).ToString("0.###"));

        Check("衰减窗口走完就回到 0（不会变成负数）",
            RandomCall.EffectiveFactor(zhang, now + TimeSpan.FromMinutes(120), window) == 0,
            RandomCall.EffectiveFactor(zhang, now + TimeSpan.FromMinutes(120), window).ToString("0.###"));

        Check("衰减窗口可调（20 分钟窗时同样的因子掉得更快）",
            RandomCall.EffectiveFactor(zhang, now, TimeSpan.FromMinutes(20)) == 0,
            RandomCall.EffectiveFactor(zhang, now, TimeSpan.FromMinutes(20)).ToString("0.###"));

        // 时间被往回调过（校时、改系统时间）不能让"刚叫过"的人凭空变回最容易被抽中
        zhang.FactorSetAt = now + TimeSpan.FromMinutes(30);

        Check("系统时间被回拨时因子不会越过上限",
            RandomCall.EffectiveFactor(zhang, now, window) <= RandomCall.MaxFactor,
            RandomCall.EffectiveFactor(zhang, now, window).ToString("0.###"));

        Check("权重有下限：刚叫过的人也不是完全没机会",
            RandomCall.Weight(zhang, now, window) >= RandomCall.MinWeight,
            RandomCall.Weight(zhang, now, window).ToString("0.###"));

        // ---------- 抽签 ----------

        var random = new Random(20260302);
        var picked = RandomCall.Pick(roster.Students, 2, now, window, random);

        Check("抽几位就抽几位",
            picked.Count == 2,
            $"抽了 {picked.Count} 位：{string.Join("、", picked.Select(s => s.Name))}");

        Check("一次抽多位不会抽到同一个学生",
            picked.Select(s => s.Id).Distinct().Count() == picked.Count,
            string.Join("、", picked.Select(s => s.Name)));

        Check("抽中之后因子重置到 0.95～1.00",
            picked.All(s => s.TimeFactor >= RandomCall.ResetFloor && s.TimeFactor <= RandomCall.MaxFactor),
            string.Join("、", picked.Select(s => $"{s.Name}={s.TimeFactor:0.###}")));

        Check("抽中之后记下了重置时刻（衰减才有起点）",
            picked.All(s => s.FactorSetAt == now),
            picked.Count == 0 ? "(没抽到)" : $"{picked[0].Name} @ {picked[0].FactorSetAt:HH:mm}");

        Check("抽中的人当时权重被压到最低",
            picked.All(s => RandomCall.Weight(s, now, window) <= RandomCall.MinWeight + 1e-9),
            string.Join("、", picked.Select(s => RandomCall.Weight(s, now, window).ToString("0.###"))));

        // 恢复成"都没被叫过"再验证越界行为
        foreach (var student in roster.Students)
        {
            student.TimeFactor = 0;
            student.FactorSetAt = null;
        }

        Check("要抽的人数超过名单时有多少抽多少",
            RandomCall.Pick(roster.Students, 99, now, window, random).Count == roster.Students.Count,
            RandomCall.Pick(roster.Students, 99, now, window, random).Count.ToString());

        Check("抽 0 位或名单为空时不抽",
            RandomCall.Pick(roster.Students, 0, now, window, random).Count == 0
                && RandomCall.Pick([], 1, now, window, random).Count == 0,
            "两次都返回空");

        // ---------- "刚叫过的人先缓一缓"到底成不成立 ----------

        foreach (var student in roster.Students)
        {
            student.TimeFactor = 0;
            student.FactorSetAt = null;
        }

        var hot = roster.Students[0];
        var repeat = 0;
        var draws = 400;
        var stats = new Random(20250101);

        for (var i = 0; i < draws; i++)
        {
            // 每次都把场面恢复成"只有 hot 刚被叫过"
            foreach (var student in roster.Students)
            {
                student.TimeFactor = 0;
                student.FactorSetAt = null;
            }

            hot.TimeFactor = RandomCall.MaxFactor;
            hot.FactorSetAt = now;

            if (ReferenceEquals(RandomCall.Pick(roster.Students, 1, now, window, stats)[0], hot))
            {
                repeat++;
            }
        }

        // 纯随机的话是 1/4（100 次左右）；按权重算期望只有 ~1.6%（6 次左右）
        Check("刚叫过的学生接下来明显更难被抽到",
            repeat < 40,
            $"{draws} 次里被抽中 {repeat} 次（纯随机约 {draws / roster.Students.Count} 次）");

        // 上一段循环里每次抽签都会改对象上的因子，所以这里先把场面摆正再比权重
        foreach (var student in roster.Students)
        {
            student.TimeFactor = 0;
            student.FactorSetAt = null;
        }

        hot.TimeFactor = RandomCall.MaxFactor;
        hot.FactorSetAt = now;

        Check("越久没叫过的人越容易被抽到（衰减真的在影响抽取）",
            RandomCall.Weight(hot, now, window) < RandomCall.Weight(roster.Students[1], now, window),
            $"刚叫过 {RandomCall.Weight(hot, now, window):0.###} < 没叫过 {RandomCall.Weight(roster.Students[1], now, window):0.###}");

        // ---------- 范围筛选 ----------

        Check("不限范围时全体都在候选里",
            RandomCall.Candidates(roster.Students, null, null, new Random(1)).Count == roster.Students.Count,
            RandomCall.Candidates(roster.Students, null, null, new Random(1)).Count.ToString());

        var groupA = RandomCall.Candidates(roster.Students, "A组", null, new Random(1));
        Check("按小组筛只留下这一组",
            groupA.Count == 2 && groupA.All(s => s.Group == "A组"),
            string.Join("、", groupA.Select(s => $"{s.Name}({s.Group})")));

        var boys = RandomCall.Candidates(roster.Students, null, "男", new Random(1));
        Check("按性别筛只留下男生（女生的写法也归一化了）",
            boys.Count == 2 && boys.All(s => s.Gender == "男"),
            string.Join("、", boys.Select(s => $"{s.Name}({s.Gender})")));

        var both = RandomCall.Candidates(roster.Students, "A组", "男", new Random(1));
        Check("小组与性别同时筛时取交集",
            both.Count == 1 && both[0].Name == "张三",
            string.Join("、", both.Select(s => s.Name)));

        Check("筛选词前后的空格不影响匹配",
            RandomCall.Candidates(roster.Students, " A组 ", " 男 ", new Random(1)).Count == 1,
            RandomCall.Candidates(roster.Students, " A组 ", " 男 ", new Random(1)).Count.ToString());

        Check("筛不出人时给空候选（界面才不会抽到一个不相干的人）",
            RandomCall.Candidates(roster.Students, "C组", null, new Random(1)).Count == 0,
            RandomCall.Candidates(roster.Students, "C组", null, new Random(1)).Count.ToString());

        // 名单里同组的人常常连在一起；不打乱的话，"权重相等"时会偏向排在前面的
        var many = RosterCsv.Parse(
            string.Join("\n", Enumerable.Range(1, 12).Select(i => $"学生{i},2025010{i},,A组,男")),
            "十二人班").Roster!.Students;

        var shuffled = RandomCall.Candidates(many, null, null, new Random(7));
        Check("候选会先打乱（避免总偏向名单开头的人）",
            !shuffled.Select(s => s.Name).SequenceEqual(many.Select(s => s.Name)),
            string.Join("、", shuffled.Take(4).Select(s => s.Name)) + " …");

        // ---------- 拼成一句话 ----------

        var template = TeacherCallSettings.RandomTemplate();
        var twoPicked = RandomCall.Pick(roster.Students, 2, now, window, new Random(5));

        var sentence = CallComposer.ComposeRandom(template, twoPicked, roster, "数学张老师");

        Check("随机叫人拼成一句（顿号分隔，不是一人一条）",
            sentence is not null && sentence.StartsWith("请 ", StringComparison.Ordinal)
                && sentence.EndsWith(" 来回答这个问题", StringComparison.Ordinal)
                && sentence.Contains('、'),
            sentence ?? "(没有输出)");

        var named = new CallTemplate
        {
            Components =
            [
                MessageComponent.Of(MessageComponentKinds.Text, "请 "),
                MessageComponent.Of(MessageComponentKinds.StudentName),
                MessageComponent.Of(MessageComponentKinds.Text, " 回答"),
                MessageComponent.Of(MessageComponentKinds.Text, "（"),
                MessageComponent.Of(MessageComponentKinds.Group),
                MessageComponent.Of(MessageComponentKinds.Text, "）"),
            ],
        };

        var byName = CallComposer.ComposeRandom(named, twoPicked, roster, "数学张老师");
        var groups = twoPicked.Select(s => s.Group).Distinct().ToList();

        Check("随机模板里的姓名组件取抽中的第一位",
            byName is not null && byName.StartsWith($"请 {twoPicked[0].Name} 回答", StringComparison.Ordinal),
            byName ?? "(没有输出)");

        Check("随机模板里的小组组件按抽中的几位去重",
            byName is not null && byName.EndsWith($"（{string.Join("、", groups)}）", StringComparison.Ordinal),
            byName ?? "(没有输出)");

        Check("没抽到人时什么都不发",
            CallComposer.ComposeRandom(template, [], roster, "数学张老师") is null,
            "返回了 null");

        Check("随机模板默认带「随机叫人」组件",
            template.HasRandomComponent && TeacherCallSettings.DefaultTemplate().HasRandomComponent == false,
            $"随机模板 {template.Components.Count} 个组件，普通模板 {TeacherCallSettings.DefaultTemplate().Components.Count} 个");

        // ---------- 设置项被手改过也要收回来 ----------

        var wild = new TeacherCallSettings { RandomCount = 99, DecayMinutes = 7, RandomGender = "male", RandomGroup = "  A组  " };
        wild.Normalized();

        Check("离谱的抽位数被夹到上限",
            wild.RandomCount == RandomCall.MaxCount,
            wild.RandomCount.ToString());

        Check("不认识的衰减窗口退回默认 40 分钟",
            wild.DecayMinutes == 40,
            wild.DecayMinutes.ToString());

        Check("设置里的性别写法也归一化",
            wild.RandomGender == "男",
            wild.RandomGender ?? "(空)");

        Check("小组名前后的空格被去掉",
            wild.RandomGroup == "A组",
            wild.RandomGroup ?? "(空)");

        var zero = new TeacherCallSettings { RandomCount = 0, RandomGroup = "   " };
        zero.Normalized();

        Check("抽位数写成 0 时回到 1",
            zero.RandomCount == 1,
            zero.RandomCount.ToString());

        Check("空白的小组名当作不限",
            zero.RandomGroup is null,
            zero.RandomGroup ?? "(空)");

        var kept = new TeacherCallSettings { DecayMinutes = 90, RandomCount = 3 };
        kept.Normalized();

        Check("合法的设置不会被改动",
            kept.DecayMinutes == 90 && kept.RandomCount == 3,
            $"{kept.DecayMinutes} 分钟 / {kept.RandomCount} 位");

        Check("衰减窗口换算成 TimeSpan",
            new TeacherCallSettings { DecayMinutes = 60 }.DecayWindow == TimeSpan.FromMinutes(60)
                && new TeacherCallSettings { DecayMinutes = 0 }.DecayWindow == RandomCall.DefaultDecayWindow,
            $"60 分钟 → {new TeacherCallSettings { DecayMinutes = 60 }.DecayWindow.TotalMinutes} 分钟");
    }

    /// <summary>
    /// 常用语（文字页上点一下即填的那几句）。
    ///
    /// 这份列表由老师自己维护，所以真正要守住的是"保存时怎么收拾他写的东西"：
    /// 空白串、手滑写重的、超长的、写太多的，都得在落盘前收敛掉，
    /// 否则界面上出现一条点不动的空按钮，或者下次打开发现少了几条又不知道为什么。
    ///
    /// 而"删光了不自动补回默认"是刻意的：那是老师明确表达过的意思。
    /// </summary>
    private static void AssertPhraseSettings()
    {
        var normalized = new TeacherPhraseSettings
        {
            Phrases =
            [
                "  同学们请安静  ",
                "",
                "   ",
                "请注意看黑板",
                "请注意看黑板",
                new string('长', TeacherPhraseSettings.MaxLength + 10),
                .. Enumerable.Range(1, TeacherPhraseSettings.MaxCount + 5).Select(i => $"第 {i} 条"),
            ],
        }.Normalized();

        Check("常用语：首尾空白被去掉",
            normalized.Phrases[0] == "同学们请安静",
            $"首条=「{normalized.Phrases[0]}」");

        Check("常用语：空条目被丢掉（不会留下点不动的空按钮）",
            normalized.Phrases.All(p => p.Trim().Length > 0),
            $"共 {normalized.Phrases.Count} 条，无空条目");

        Check("常用语：写重了的只留一条",
            normalized.Phrases.Count(p => p == "请注意看黑板") == 1,
            $"「请注意看黑板」出现 {normalized.Phrases.Count(p => p == "请注意看黑板")} 次");

        Check("常用语：超出上限的截断到上限",
            normalized.Phrases.Count == TeacherPhraseSettings.MaxCount,
            $"{normalized.Phrases.Count} 条（上限 {TeacherPhraseSettings.MaxCount}）");

        Check("常用语：留下的是靠前的那几条",
            normalized.Phrases[1] == "请注意看黑板" && normalized.Phrases[3] == "第 1 条",
            $"第 2、4 条是「{normalized.Phrases[1]}」「{normalized.Phrases[3]}」");

        var tooLong = new TeacherPhraseSettings { Phrases = [new string('长', 200)] }.Normalized();
        Check("常用语：超长的截断而不是整条丢掉",
            tooLong.Phrases.Count == 1 && tooLong.Phrases[0].Length == TeacherPhraseSettings.MaxLength,
            $"{tooLong.Phrases.Count} 条，长度 {tooLong.Phrases[0].Length}（上限 {TeacherPhraseSettings.MaxLength}）");

        // 这条是刻意的设计：删光了就是删光了，下次启动不该又冒出来
        var emptied = new TeacherPhraseSettings { Phrases = [] }.Normalized();
        Check("常用语：删光了就保持为空（不偷偷补回默认）",
            emptied.Phrases.Count == 0,
            $"剩 {emptied.Phrases.Count} 条");

        var fallback = TeacherPhraseSettings.WithDefaults().Normalized();
        Check("常用语：出厂那几句本身是干净的（不多不少）",
            fallback.Phrases.Count == TeacherPhraseSettings.Defaults().Count && fallback.Phrases.Count > 0,
            $"共 {fallback.Phrases.Count} 条：{string.Join("、", fallback.Phrases)}");

        Check("常用语：默认里没有重复",
            fallback.Phrases.Distinct(StringComparer.Ordinal).Count() == fallback.Phrases.Count,
            "没有重复项");
    }

    /// <summary>
    /// 按班级的任教科目。
    ///
    /// 这条规则错起来特别安静：名字照样显示，只是科目那两个字不对 ——
    /// 而"是谁在说话"正是这个字段存在的全部理由。两端（服务器贴来源、
    /// 教师端局域网直连自己贴来源）必须算出同一个名字，所以规则放在 Core 里共用。
    /// </summary>
    private static void AssertTeachingSubjects()
    {
        var byClassroom = new Dictionary<string, string?>
        {
            ["uuid-junior-2"] = "化学",
            ["uuid-junior-3"] = " 物理 ",   // 首尾空白要裁掉
            ["uuid-empty"] = "   ",          // 只有空白＝没指定
            [""] = "地理",                    // 空 UUID 丢掉
        };

        var normalized = TeachingSubjects.Normalize(byClassroom);

        Check("按班级的科目表：裁掉空白、丢掉空条目",
            normalized.Count == 2 && normalized["uuid-junior-3"] == "物理",
            $"剩 {normalized.Count} 条：{string.Join("、", normalized.Select(kv => $"{kv.Key}={kv.Value}"))}");

        Check("有单独指定的班用那一份",
            TeachingSubjects.For("数学", normalized, "uuid-junior-2") == "化学",
            TeachingSubjects.For("数学", normalized, "uuid-junior-2") ?? "(空)");

        Check("没单独指定的班回落到默认科目",
            TeachingSubjects.For("数学", normalized, "uuid-unknown") == "数学",
            TeachingSubjects.For("数学", normalized, "uuid-unknown") ?? "(空)");

        Check("UUID 大小写不同也算同一间",
            TeachingSubjects.For("数学", normalized, "UUID-JUNIOR-2") == "化学",
            TeachingSubjects.For("数学", normalized, "UUID-JUNIOR-2") ?? "(空)");

        Check("没有默认科目又没单独指定时，不编一个出来",
            TeachingSubjects.For(null, normalized, "uuid-unknown") is null,
            "返回了空");

        Check("连科目都没有时来源只报姓名",
            TeachingSubjects.ShoutName("张老师", null, null, null) == "张老师",
            TeachingSubjects.ShoutName("张老师", null, null, null));

        Check("有科目时来源是「科目＋姓名」",
            TeachingSubjects.ShoutName("张老师", "数学", normalized, "uuid-junior-2") == "化学张老师",
            TeachingSubjects.ShoutName("张老师", "数学", normalized, "uuid-junior-2"));

        // 存下来的那份与"可以往里写 null"的那份要能互转：读写共用同一个形状
        var asNullable = TeachingSubjects.AsNullable(normalized);
        Check("存下来的表能转成可写 null 的那一份（读写同一个形状）",
            asNullable is { Count: 2 } && asNullable["uuid-junior-2"] == "化学",
            asNullable is null ? "返回了 null" : $"{asNullable.Count} 条");

        Check("空表转出来是 null 而不是空字典（少一种状态要判断）",
            TeachingSubjects.AsNullable(new Dictionary<string, string>()) is null,
            "返回了 null");
    }

    /// <summary>
    /// WAV 编解码。
    ///
    /// 定时语音要落盘再读回来，而**格式丢了就没法播**：教室端把 16 kHz 的字节
    /// 当成 48 kHz 放出来，是一段快进的声音 —— 不会报错，只是很难听出是人话。
    /// </summary>
    private static void AssertWavCodec()
    {
        var format = new AudioFormat(16000, 1, 16);
        var pcm = new byte[format.BytesForDuration(700)];

        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i % 253);
        }

        var wav = WavCodec.Encode(format, pcm);

        Check("包出来的 WAV 带 RIFF/WAVE 头",
            wav.Length == WavCodec.HeaderBytes + pcm.Length
            && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF"
            && System.Text.Encoding.ASCII.GetString(wav, 8, 4) == "WAVE",
            $"{wav.Length} 字节（PCM {pcm.Length} + 头 {WavCodec.HeaderBytes}）");

        Check("读得回来的格式与写进去的一致",
            WavCodec.TryDecode(wav, out var decodedFormat, out var decodedPcm)
            && decodedFormat.SampleRate == format.SampleRate
            && decodedFormat.Channels == format.Channels
            && decodedFormat.BitsPerSample == format.BitsPerSample,
            "16000 Hz / 1 声道 / 16 bit");

        Check("读回来的 PCM 逐字节一致",
            decodedPcm.Length == pcm.Length && decodedPcm.AsSpan().SequenceEqual(pcm),
            $"发出 {pcm.Length} 字节，读回 {decodedPcm.Length} 字节");

        Check("时长算得对（用来卡 60 秒上限）",
            Math.Abs(format.DurationMsOf(pcm.Length) / 1000.0 - 0.7) < 0.01,
            $"{format.DurationMsOf(pcm.Length) / 1000.0:0.###} 秒");

        Check("不是 WAV 的东西不会被当成 WAV",
            !WavCodec.TryDecode("这不是音频"u8.ToArray(), out _, out _),
            "被拒了");

        // 非 PCM（比如 8 位的 A-law）宁可拒收，也不要放出一段噪声
        var alaw = WavCodec.Encode(format, pcm);
        alaw[20] = 6;
        alaw[21] = 0;

        Check("非 PCM 编码的 WAV 被拒收（而不是放出噪声）",
            !WavCodec.TryDecode(alaw, out _, out _),
            "被拒了");

        // 别的软件写出来的 WAV 常在 fmt 与 data 之间塞 LIST/fact 块，要能跳过去
        var withExtra = new List<byte>(WavCodec.Encode(format, pcm));
        withExtra.InsertRange(WavCodec.HeaderBytes, [.. "LIST"u8.ToArray(), 4, 0, 0, 0, 1, 2, 3, 4]);

        Check("中间的额外块（LIST 之类）能跳过",
            WavCodec.TryDecode(withExtra.ToArray(), out _, out var extraPcm) && extraPcm.Length == pcm.Length,
            $"读回 {extraPcm.Length} 字节");
    }

    /// <summary>
    /// 定时喊话的两种形态（文字 / 语音）在列表里要说得清。
    ///
    /// "（空）"和"语音 12 秒"混起来的话，老师看到一条空条目会以为是坏了。
    /// </summary>
    private static void AssertScheduledShoutModel()
    {
        var voice = new ScheduledShout
        {
            Kind = ScheduledShoutKinds.Voice,
            AudioSeconds = 12.4,
            AudioFile = "abc.wav",
        };

        Check("语音定时的摘要是时长，不是空内容",
            voice.SummaryText == "语音 12.4 秒",
            voice.SummaryText);

        Check("认得出这是语音定时",
            ScheduledShoutKinds.IsVoice(voice.Kind) && !ScheduledShoutKinds.IsVoice(ScheduledShoutKinds.Text),
            $"{voice.Kind} / {ScheduledShoutKinds.Text}");

        var text = new ScheduledShout { Text = "下课前五分钟提醒交作业，别忘了把实验报告带上并交给课代表" };

        Check("文字定时的摘要会被截断（列表里一行放得下）",
            text.SummaryText.Length <= 25 && text.SummaryText.EndsWith('…'),
            text.SummaryText);

        var at = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        var pending = new ScheduledShout { SendAt = at };

        Check("到点了才算 due（差一秒都不算）",
            pending.IsDue(at) && !pending.IsDue(at.AddSeconds(-1)),
            "到点那一刻算，前一秒不算");

        Check("错过超过宽限窗口就不再补发",
            pending.IsMissedBeyond(at.AddMinutes(3).AddSeconds(1), TimeSpan.FromMinutes(3))
            && !pending.IsMissedBeyond(at.AddMinutes(2), TimeSpan.FromMinutes(3)),
            "三分钟以内还发，超过就不发");

        var sent = new ScheduledShout { SendAt = at, SentAt = at };

        Check("已经发过的不再算 due（不会重复发）",
            !sent.IsDue(at.AddMinutes(1)),
            "发过就不再发");

        var cancelled = new ScheduledShout { SendAt = at, Cancelled = true };

        Check("取消掉的不算 due",
            !cancelled.IsDue(at),
            "取消优先于时间");
    }

    /// <summary>
    /// 检查更新与自定义镜像源。
    ///
    /// 这一段刻意不碰真网络：自检要在没有外网、或者校园网把 GitHub 拦掉的机器上
    /// 也能跑。HTTP 那一层用一个假的处理器，于是"请求打到哪个地址、拿到什么之后算出什么"
    /// 全都能断言 —— 而这两件事恰恰是这个功能里最容易错的（镜像拼错、版本比反）。
    /// </summary>
    private static async Task AssertUpdateCheckerAsync()
    {
        // —— 版本比对 ——
        Check("版本比对：1.8.1 比 1.8.0 新",
            UpdateChecker.IsNewer("1.8.1", "1.8.0"),
            "1.8.1 > 1.8.0");

        // 按字符串比会得出相反的结论，而这种错要到第 10 个次版本才显形
        Check("版本比对：1.10.0 比 1.9.0 新（按段比数字，不是按字符串）",
            UpdateChecker.IsNewer("1.10.0", "1.9.0"),
            "1.10.0 > 1.9.0");

        Check("版本比对：同版本不算新",
            !UpdateChecker.IsNewer("1.8.0", "1.8.0") && !UpdateChecker.IsNewer("v1.8.0", "1.8.0"),
            "带不带 v 都一样");

        Check("版本比对：旧版本不算新",
            !UpdateChecker.IsNewer("1.7.0", "1.8.0"),
            "1.7.0 < 1.8.0");

        Check("版本比对：预发布比同号正式版旧",
            !UpdateChecker.IsNewer("1.9.0-rc1", "1.9.0"),
            "rc 不算正式版");

        // —— 镜像模板 ——
        var rawUrl = "https://github.com/WRD1145/ClassShout/releases/download/v1.9.0/ClassShout.Classroom.exe";

        Check("镜像模板：留空就用原始地址（直连 GitHub）",
            UpdateSettings.ApplyTemplate(string.Empty, rawUrl) == rawUrl,
            "原样返回");

        Check("镜像模板：{url} 是整段原始地址（前缀式镜像）",
            UpdateSettings.ApplyTemplate("https://ghproxy.net/{url}", rawUrl)
                == "https://ghproxy.net/" + rawUrl,
            UpdateSettings.ApplyTemplate("https://ghproxy.net/{url}", rawUrl));

        Check("镜像模板：{path} 是去掉 github.com/ 之后的部分（换域名式镜像）",
            UpdateSettings.ApplyTemplate("https://kkgithub.com/{path}", rawUrl)
                == "https://kkgithub.com/WRD1145/ClassShout/releases/download/v1.9.0/ClassShout.Classroom.exe",
            UpdateSettings.ApplyTemplate("https://kkgithub.com/{path}", rawUrl));

        Check("仓库名容错：整条 GitHub 地址也能认",
            UpdateSettings.NormalizeRepository("https://github.com/WRD1145/ClassShout.git/") == "WRD1145/ClassShout",
            UpdateSettings.NormalizeRepository("https://github.com/WRD1145/ClassShout.git/"));

        Check("仓库名容错：空值回落到默认仓库",
            UpdateSettings.NormalizeRepository("  ") == UpdateSettings.DefaultRepository,
            UpdateSettings.DefaultRepository);

        Check("预置了几条内置镜像，且第一条是直连",
            UpdateMirror.BuiltIns().Count >= 5 && UpdateMirror.BuiltIns()[0].DownloadTemplate.Length == 0,
            string.Join("、", UpdateMirror.BuiltIns().Select(m => m.Label)));

        // —— 内置的 Gitee 镜像 ——
        var gitee = UpdateMirror.BuiltIns().FirstOrDefault(m => m.Provider == UpdateMirrorProvider.Gitee);

        Check("内置了 Gitee 镜像（码云上那份镜像仓库）",
            gitee is { Repository: "li-hansen136/ClassShout" },
            gitee is null ? "没有 Gitee 那一条" : $"{gitee.Label} → {gitee.EffectiveRepository(UpdateSettings.DefaultRepository)}");

        Check("Gitee 镜像走的是码云的接口地址",
            gitee?.LatestReleaseUrl(UpdateSettings.DefaultRepository)
                == "https://gitee.com/api/v5/repos/li-hansen136/ClassShout/releases/latest",
            gitee?.LatestReleaseUrl(UpdateSettings.DefaultRepository) ?? "(没找到)");

        Check("GitHub 镜像走的是 GitHub 的接口地址",
            UpdateMirror.BuiltIns()[0].LatestReleaseUrl(UpdateSettings.DefaultRepository)
                == "https://api.github.com/repos/WRD1145/ClassShout/releases/latest",
            UpdateMirror.BuiltIns()[0].LatestReleaseUrl(UpdateSettings.DefaultRepository));

        Check("仓库名容错：整条 Gitee 地址也能认",
            UpdateSettings.NormalizeRepository("https://gitee.com/li-hansen136/ClassShout.git") == "li-hansen136/ClassShout",
            UpdateSettings.NormalizeRepository("https://gitee.com/li-hansen136/ClassShout.git"));

        Check("下载模板：{path} 对 Gitee 的地址也剪得对",
            UpdateSettings.ApplyTemplate(
                "https://myproxy/{path}",
                "https://gitee.com/li-hansen136/ClassShout/releases/download/v1.10.0/x.zip")
            == "https://myproxy/li-hansen136/ClassShout/releases/download/v1.10.0/x.zip",
            "剪掉了 gitee.com/ 前缀");

        // —— 镜像列表：能加、能删、内置的删不掉 ——
        var withCustom = new UpdateSettings
        {
            Mirrors = [.. UpdateMirror.BuiltIns()],
            SelectedMirrorId = "gitee",
        }.Normalized();

        withCustom.Mirrors.Add(new UpdateMirror { Label = "校园代理", ApiBase = "https://mirror.school.edu" });
        var afterAdd = withCustom.Normalized();

        Check("可以往列表里加自定义镜像",
            afterAdd.Mirrors.Count == UpdateMirror.BuiltIns().Count + 1
            && afterAdd.Mirrors.Any(m => m.Label == "校园代理"),
            $"共 {afterAdd.Mirrors.Count} 条");

        Check("选中的镜像会记住",
            afterAdd.SelectedMirror.Id == "gitee",
            afterAdd.SelectedMirror.Label);

        var emptied = new UpdateSettings { Mirrors = [], SelectedMirrorId = "nope" }.Normalized();

        Check("把镜像删光之后内置的会补回来（总有一条能试）",
            emptied.Mirrors.Count == UpdateMirror.BuiltIns().Count && emptied.SelectedMirror.Id == UpdateMirror.BuiltIns()[0].Id,
            $"补回 {emptied.Mirrors.Count} 条");

        // —— 代理解析 ——
        var noProxy = UpdateProxy.Resolve(UpdateProxyMode.None, null);
        Check("代理：选「不使用」就是直连", !noProxy.UseProxy && noProxy.Description.Contains("直连"), noProxy.Description);

        var customProxy = UpdateProxy.Resolve(UpdateProxyMode.Custom, "http://127.0.0.1:7890");
        Check("代理：自己填的地址会被用上",
            customProxy.UseProxy && customProxy.Address.Contains("127.0.0.1:7890"),
            customProxy.Description);

        var badProxy = UpdateProxy.Resolve(UpdateProxyMode.Custom, "这不是地址");
        Check("代理：地址填错时说清楚，而不是悄悄直连",
            !badProxy.UseProxy && badProxy.Description.Contains("看不懂"),
            badProxy.Description);

        var systemProxy = UpdateProxy.Resolve(UpdateProxyMode.System, null);
        Check("代理：跟随系统时会把实际解析结果说出来（没配就是直连）",
            systemProxy.Description.Contains("系统"),
            systemProxy.Description);

        // 这一条正是踩过的坑：.NET 自带的解析在"系统代理开着、但只设了 HTTP_PROXY"
        // 的机器上会把 https 请求判成直连，于是应用连不上而浏览器能开。
        // 现在跟随系统会去读 Windows 的系统代理设置（注册表），这里注入一份假的来断言。
        var fakeSystem = UpdateProxy.Resolve(
            UpdateProxyMode.System,
            null,
            "https://api.github.com",
            () => (true, "127.0.0.1:7890", "localhost;127.*;<local>"));

        Check("代理：系统代理开着时，https 请求也会走它（这是踩过的坑）",
            fakeSystem.UseProxy && fakeSystem.Address.Contains("127.0.0.1:7890"),
            fakeSystem.Description);

        var fakeOff = UpdateProxy.Resolve(
            UpdateProxyMode.System,
            null,
            "https://api.github.com",
            () => (false, "127.0.0.1:7890", null));

        Check("代理：系统代理关着时不会硬套上那个地址",
            !fakeOff.UseProxy,
            fakeOff.Description);

        Check("代理：按协议分写的设置能取对那一段",
            UpdateProxy.ParseWindowsProxyServer("http=1.2.3.4:8080;https=5.6.7.8:9090", "https://api.github.com")
                == "http://5.6.7.8:9090",
            UpdateProxy.ParseWindowsProxyServer("http=1.2.3.4:8080;https=5.6.7.8:9090", "https://api.github.com") ?? "(空)");

        Check("代理：https 没单独配时用 http 那一段（http 代理也能转发 https）",
            UpdateProxy.ParseWindowsProxyServer("http=1.2.3.4:8080", "https://api.github.com")
                == "http://1.2.3.4:8080",
            UpdateProxy.ParseWindowsProxyServer("http=1.2.3.4:8080", "https://api.github.com") ?? "(空)");

        Check("代理：socks 那一段也认",
            UpdateProxy.ParseWindowsProxyServer("socks=127.0.0.1:1080", "https://api.github.com")
                == "socks5://127.0.0.1:1080",
            UpdateProxy.ParseWindowsProxyServer("socks=127.0.0.1:1080", "https://api.github.com") ?? "(空)");

        Check("代理：没写 scheme 的 host:port 会补上 http://",
            UpdateProxy.ParseWindowsProxyServer("127.0.0.1:7890", "https://api.github.com")
                == "http://127.0.0.1:7890",
            UpdateProxy.ParseWindowsProxyServer("127.0.0.1:7890", "https://api.github.com") ?? "(空)");

        Check("代理：设置里是空的就别编一个出来",
            UpdateProxy.ParseWindowsProxyServer("   ", "https://api.github.com") is null
            && UpdateProxy.ParseWindowsProxyServer(null, "https://api.github.com") is null,
            "返回了 null");

        Check("代理：选了自定义却没填地址，会退回跟随系统（不会假装走了代理）",
            new UpdateSettings { ProxyMode = UpdateProxyMode.Custom, ProxyUrl = "  " }.Normalized().ProxyMode == UpdateProxyMode.System,
            "退回跟随系统");

        // —— 端到端：请求打到哪、拿到什么、算出什么 ——
        const string releaseJson = """
            {
              "tag_name": "v1.10.0",
              "body": "这一版修了几个问题。",
              "html_url": "https://github.com/WRD1145/ClassShout/releases/tag/v1.10.0",
              "assets": [
                { "name": "ClassShout.Classroom-win-x64.zip", "size": 1234, "browser_download_url": "https://github.com/WRD1145/ClassShout/releases/download/v1.10.0/ClassShout.Classroom-win-x64.zip" },
                { "name": "classshout-teacher-1.10.0-universal.apk", "size": 5678, "browser_download_url": "https://github.com/WRD1145/ClassShout/releases/download/v1.10.0/classshout-teacher-1.10.0-universal.apk" }
              ]
            }
            """;

        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json"),
        });

        // 处理器按代理解析结果来造：于是"这次有没有走代理"也能断言
        ProxyResolution? seenProxy = null;
        var checker = new UpdateChecker(
            new UpdateSettings
            {
                Mirrors =
                [
                    new UpdateMirror
                    {
                        Id = "ghproxy",
                        Label = "ghproxy.net",
                        DownloadTemplate = "https://ghproxy.net/{url}",
                    },
                ],
                SelectedMirrorId = "ghproxy",
                ProxyMode = UpdateProxyMode.Custom,
                ProxyUrl = "http://127.0.0.1:7890",
            },
            proxy =>
            {
                seenProxy = proxy;
                return handler;
            });

        var found = await checker.CheckAsync("1.9.0");

        Check("检查更新：查得到新版本",
            found is { Ok: true, HasUpdate: true } && found.LatestVersion == "1.10.0",
            found.Error ?? $"最新 {found.LatestVersion}");

        Check("检查更新：请求打到了正确的 API 地址",
            handler.LastUri == "https://api.github.com/repos/WRD1145/ClassShout/releases/latest",
            handler.LastUri ?? "(没请求)");

        Check("检查更新：带上 User-Agent（缺了 GitHub 直接 403）",
            handler.LastUserAgent == "ClassShout-Updater",
            handler.LastUserAgent ?? "(没有)");

        Check("检查更新：自己填的代理真的传给了 HTTP 层",
            seenProxy is { UseProxy: true } && seenProxy.Value.Address.Contains("7890"),
            seenProxy?.Description ?? "(没解析)");

        Check("检查更新：结果里带回走的哪个镜像与代理（出问题时说得清）",
            found.MirrorLabel == "ghproxy.net" && found.ProxyDescription?.Contains("7890") == true,
            $"{found.MirrorLabel} / {found.ProxyDescription}");

        Check("检查更新：附件地址已经过镜像换算",
            found.FindAsset("ClassShout.Classroom-win-x64.zip")?.Url
                == "https://ghproxy.net/https://github.com/WRD1145/ClassShout/releases/download/v1.10.0/ClassShout.Classroom-win-x64.zip",
            found.FindAsset("ClassShout.Classroom-win-x64.zip")?.Url ?? "(没找到)");

        Check("检查更新：按后缀也能挑到附件（附件名里带着版本号）",
            found.FindAssetEndingWith(".apk")?.Name == "classshout-teacher-1.10.0-universal.apk",
            found.FindAssetEndingWith(".apk")?.Name ?? "(没找到)");

        Check("检查更新：带上发行说明与页面地址",
            found.Notes?.Contains("修了几个问题") == true && found.PageUrl?.Contains("releases/tag") == true,
            "说明与页面都在");

        // —— 已经是最新 ——
        var upToDate = await checker.CheckAsync("1.10.0");

        Check("已经是最新时不报有新版本",
            upToDate is { Ok: true, HasUpdate: false },
            $"最新 {upToDate.LatestVersion}");

        // —— 一键检测：必须真的并发 ——
        //
        // 串行检测是这里最现实的一种退化（写起来更自然），而它的代价是
        // "六条源里有一条不通就要等它超时"，用户按下按钮后要干等十几秒。
        // 所以用"每个请求睡 200 毫秒"来断言：六条并发应当在 500 毫秒内回来，
        // 串行则至少 1.2 秒。
        var slowHandler = new StubHandler(
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json"),
            },
            TimeSpan.FromMilliseconds(200));

        var parallelChecker = new UpdateChecker(
            new UpdateSettings { Mirrors = [.. UpdateMirror.BuiltIns()] },
            _ => slowHandler,
            (_, _) => new ProxyResolution(false, null, "直连", string.Empty));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = await parallelChecker.TestAllAsync(UpdateMirror.BuiltIns());
        stopwatch.Stop();

        Check("一键检测：所有镜像都测到了",
            results.Count == UpdateMirror.BuiltIns().Count && results.All(r => r.Ok),
            $"测了 {results.Count} 条，通 {results.Count(r => r.Ok)} 条");

        Check("一键检测：并发发出，而不是一条一条等（6 × 200ms 远小于串行的 1.2s）",
            stopwatch.ElapsedMilliseconds < 900 && slowHandler.PeakConcurrency > 1,
            $"{stopwatch.ElapsedMilliseconds} ms，同时在跑的请求数峰值 {slowHandler.PeakConcurrency}");

        Check("一键检测：每条结果都带回自己的名字与耗时",
            results.All(r => r.Label.Length > 0 && r.LatencyMs >= 0),
            string.Join("、", results.Select(r => $"{r.Label}={r.LatencyMs}ms")));

        // —— 失败路径：要给人话，不要异常 ——
        var notFound = await new UpdateChecker(
            new UpdateSettings(),
            _ => new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))).CheckAsync("1.9.0");

        Check("仓库不存在时给一句人话（并指出是这个镜像上没有发行版）",
            !notFound.Ok && notFound.Error?.Contains("发行版") == true,
            notFound.Error ?? "(没有原因)");

        var limited = await new UpdateChecker(
            new UpdateSettings(),
            _ => new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden))).CheckAsync("1.9.0");

        Check("被限流时说清是限流（换个镜像还能救）",
            !limited.Ok && limited.Error?.Contains("频率限制") == true,
            limited.Error ?? "(没有原因)");

        var garbled = await new UpdateChecker(
            new UpdateSettings(),
            _ => new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("这不是 JSON", System.Text.Encoding.UTF8, "application/json"),
            })).CheckAsync("1.9.0");

        Check("返回内容被中间层改过时给一句人话（而不是把 JSON 解析异常甩出来）",
            !garbled.Ok && garbled.Error?.Contains("看不懂") == true,
            garbled.Error ?? "(没有原因)");

        // 没走代理时，"连不上"这句里要顺带提醒可以设代理 —— 这正是用户这次踩到的坑
        var offline = await new UpdateChecker(
            new UpdateSettings { ProxyMode = UpdateProxyMode.None },
            _ => new StubHandler(_ => throw new HttpRequestException("无法连接"))).CheckAsync("1.9.0");

        Check("直连失败时提示「可以设个代理」（校园网里这是最常见的原因）",
            !offline.Ok && offline.Error?.Contains("代理") == true,
            offline.Error ?? "(没有原因)");

        // —— 卡片本身：查到新版本之后，"下载新版"必须真的能点 ——
        //
        // 这是一个真实发生过的问题：属性（CanDownload）变了，但按钮的可用状态来自命令的
        // CanExecute，而命令没人通知"变了" —— 于是界面上"明明查到了新版本，
        // 「下载新版」却一直是灰的、点不动"。光断言 CanDownload 是发现不了的，
        // 必须问命令本人 CanExecute 要答案。
        var openedUrls = new List<string>();
        var originalDataDir = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        var tempDataDir = Path.Combine(Path.GetTempPath(), "cs-updatecard-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // 检查完会落盘（update.json），自检不能改使用者自己的那份
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", tempDataDir);

            var card = new ClassShout.Design.UpdateCardViewModel(
                null,
                new UpdateSettings { Mirrors = [.. UpdateMirror.BuiltIns()] },
                url =>
                {
                    openedUrls.Add(url);
                    return Task.CompletedTask;
                },
                settings => new UpdateChecker(settings, _ => handler))
            {
                CurrentVersion = "1.9.0",
                PreferredAssetName = "ClassShout.Classroom-win-x64.zip",
            };

            var clickableBefore = card.OpenDownloadCommand.CanExecute(null);
            var notified = false;
            card.OpenDownloadCommand.CanExecuteChanged += (_, _) => notified = true;

            await card.CheckAsync();

            Check("卡片：没查到之前「下载新版」是灰的",
                !clickableBefore,
                $"CanExecute={clickableBefore}");

            Check("卡片：查到新版本后「下载新版」变成可点（并通知过界面重新问）",
                card.OpenDownloadCommand.CanExecute(null) && notified,
                $"CanExecute={card.OpenDownloadCommand.CanExecute(null)}，通知过={notified}");

            await card.OpenDownloadAsync();

            Check("卡片：点「下载新版」打开的就是本平台的附件地址",
                openedUrls.Count == 1 && openedUrls[0].EndsWith("ClassShout.Classroom-win-x64.zip", StringComparison.Ordinal),
                openedUrls.Count == 1 ? openedUrls[0] : $"打开了 {openedUrls.Count} 个地址");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", originalDataDir);
        }
    }

    /// <summary>
    /// 文字页「这条发给谁」那张卡。
    ///
    /// 这张卡的可见性以前是"已保存的服务器教室数量 > 1"——于是只连局域网时
    /// （那份列表是服务器绑定用的，平时是空的）整张卡都不出现，
    /// 而"这条会发到哪"恰恰是老师最想确认的一句话。
    /// 现在有一间就显示，并且写清是经服务器发的还是局域网直连。
    /// </summary>
    private static void AssertShoutTargetCard()
    {
        var originalDataDir = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        var tempDataDir = Path.Combine(Path.GetTempPath(), "cs-targetcard-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // 视图模型会读本机的展示参数与常用语，指到临时目录免得动使用者自己的
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", tempDataDir);

            var text = new ClassShout.Teacher.ViewModels.TextShoutViewModel(
                new ClassShout.Teacher.Services.ShoutTransportRouter());

            Check("可发送班级：一间都没连、也没保存教室时，这张卡不显示",
                !text.HasTargets,
                $"HasTargets={text.HasTargets}");

            // 只连局域网（用桌面端当教师端就是这么用的）
            text.SyncTargets(
                [new ShoutTargetInfo("lan-1", "三年二班", ShoutTargetSources.Lan, IsOnline: true, IsCurrent: true, Record: null)]);

            Check("可发送班级：只连局域网时卡片也显示",
                text.HasTargets,
                $"HasTargets={text.HasTargets}");

            Check("可发送班级：局域网那项写着「局域网直连」",
                text.TargetHintText.Contains("三年二班") && text.Targets[0].SourceLabel == "局域网直连",
                $"{text.TargetHintText} / {text.Targets[0].SourceLabel}");

            Check("可发送班级：局域网直连时右上角那行也写着是哪一间",
                text.TargetSummaryText.Contains("三年二班"),
                text.TargetSummaryText);

            // 只有一间服务器教室：以前整张卡都不显示
            text.SyncTargets(
                [
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: true,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            Check("可发送班级：只有一间服务器教室时也显示，并写明发到哪一间",
                text.HasTargets && text.TargetHintText.Contains("三年三班"),
                text.TargetHintText);

            Check("可发送班级：只有一间时不再出现多班那套说明",
                !text.TargetHintText.Contains("同时发到每一间"),
                text.TargetHintText);

            Check("可发送班级：只有一间时它默认是勾上的（不然发送会变成「什么都不发」）",
                text.Targets.Count == 1 && text.Targets[0].IsSelected,
                $"共 {text.Targets.Count} 项，勾选 {text.Targets.Count(t => t.IsSelected)} 项");

            // 两间：回到多班说明
            text.SyncTargets(
                [
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: false,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                    new ShoutTargetInfo(
                        "uuid-2", "三年四班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: true,
                        Record: new BoundClassroom("uuid-2", "三年四班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            Check("可发送班级：再同步会保留老师的手工勾选（去设备页转一圈回来不该被清空）",
                text.Targets.Count == 2 && text.Targets[0].IsSelected && !text.Targets[1].IsSelected,
                string.Join("、", text.Targets.Select(t => $"{t.Name}{(t.IsSelected ? "(已勾)" : string.Empty)}")));

            // 两间都勾上：这时那句话要写清会同时发到每一间
            text.Targets[0].IsSelected = true;
            text.Targets[1].IsSelected = true;

            Check("可发送班级：两间时说明会同时发到每一间（并列出是哪几间）",
                text.TargetHintText.Contains("同时发到每一间") && text.TargetHintText.Contains("三年三班"),
                text.TargetHintText);

            // 离线的那些要单独说一句：不然老师会以为"发出去了 = 教室里响了"
            text.SyncTargets(
                [
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: false,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                    new ShoutTargetInfo(
                        "uuid-2", "三年四班", ShoutTargetSources.Server, IsOnline: false, IsCurrent: false,
                        Record: new BoundClassroom("uuid-2", "三年四班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            text.Targets[0].IsSelected = true;
            text.Targets[1].IsSelected = true;

            Check("可发送班级：勾中的班里有离线时会把这件事说出来",
                text.TargetHintText.Contains("离线"),
                text.TargetHintText);

            Check("可发送班级：在线状态写在每个条目自己身上",
                text.Targets[0].OnlineText == "在线" && text.Targets[1].OnlineText == "当前离线",
                $"{text.Targets[0].OnlineText} / {text.Targets[1].OnlineText}");

            // 局域网那间 + 服务器两间：局域网直连的那项排在最前，且它必然是"已连接"
            text.SyncTargets(
                [
                    new ShoutTargetInfo("lan-1", "三年二班", ShoutTargetSources.Lan, IsOnline: true, IsCurrent: true, Record: null),
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: false, IsCurrent: false,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            Check("可发送班级：局域网与服务器两条路合在同一张表里",
                text.Targets.Count == 2 && text.Targets[0].IsLan && !text.Targets[1].IsLan,
                string.Join("、", text.Targets.Select(t => $"{t.Name}({t.SourceLabel})")));

            Check("可发送班级：局域网那项显示「已连接」（不通就发不出去）",
                text.Targets[0].OnlineText == "已连接",
                text.Targets[0].OnlineText);

            Check("可发送班级：局域网那项没有绑定记录（它本来就连着，不需要凭据）",
                text.Targets[0].Record is null && text.Targets[1].Record is not null,
                $"局域网 Record={(text.Targets[0].Record is null ? "null" : "有")}，服务器 Record={(text.Targets[1].Record is null ? "null" : "有")}");

            // 首次填充（本机还没记住任何教室）时默认勾当前绑定的那间
            var fresh = new ClassShout.Teacher.ViewModels.TextShoutViewModel(
                new ClassShout.Teacher.Services.ShoutTransportRouter());

            fresh.SyncTargets(
                [
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: false,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                    new ShoutTargetInfo(
                        "uuid-2", "三年四班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: true,
                        Record: new BoundClassroom("uuid-2", "三年四班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            Check("可发送班级：首次填充时默认勾当前绑定的那一间",
                fresh.Targets.Count == 2
                && fresh.Targets.Count(t => t.IsSelected) == 1
                && fresh.Targets[1].IsSelected,
                string.Join("、", fresh.Targets.Select(t => $"{t.Name}{(t.IsSelected ? "(已勾)" : string.Empty)}")));

            // 局域网那间在最前时，首次填充默认勾的就是它（最直接的那条路）
            var freshLan = new ClassShout.Teacher.ViewModels.TextShoutViewModel(
                new ClassShout.Teacher.Services.ShoutTransportRouter());

            freshLan.SyncTargets(
                [
                    new ShoutTargetInfo("lan-1", "三年二班", ShoutTargetSources.Lan, IsOnline: true, IsCurrent: true, Record: null),
                    new ShoutTargetInfo(
                        "uuid-1", "三年三班", ShoutTargetSources.Server, IsOnline: true, IsCurrent: false,
                        Record: new BoundClassroom("uuid-1", "三年三班", DateTimeOffset.Now, "https://relay.example.com")),
                ]);

            Check("可发送班级：首次填充时优先勾局域网直连那间",
                freshLan.Targets.Count(t => t.IsSelected) == 1 && freshLan.Targets[0].IsSelected,
                string.Join("、", freshLan.Targets.Select(t => $"{t.Name}{(t.IsSelected ? "(已勾)" : string.Empty)}")));

            // —— 呼叫页的学生行：构造顺序曾经把安卓端直接崩掉 ——
            //
            // StudentPickRow 的 _isSelected 原先在 Display 赋值**之前**求值，
            // 而 IsSelectable 看的就是 Display。只要"上次勾过学生"（勾选存在本机，
            // LoadStudents 会把它们带上），&& 就不会短路 → 读到一个 null 的 Display
            // → 启动即 FATAL EXCEPTION。第一次用的人不会触发（没有勾选 → 短路 → 不崩），
            // 所以这个坑在"用过一次呼叫"的机器上才现形。
            var pickStudent = new Student
            {
                Id = "s1",
                Name = "张三",
                StudentNo = "20250101",
                ShortName = "小张",
                Group = "A组",
            };

            var pickedRow = new ClassShout.Teacher.ViewModels.StudentPickRow(
                pickStudent, StudentLabelStyles.Name, isSelected: true, _ => { });

            Check("呼叫页的学生行：上次勾过的那位也构造得出来（这条曾经让安卓端启动即崩）",
                pickedRow is { IsSelected: true, Display: "张三" } && pickedRow.Detail.Contains("20250101"),
                $"IsSelected={pickedRow.IsSelected}，Display={pickedRow.Display}");

            // 按学号显示时，没填学号的学生应当"不可选"，且请求的勾选要被否掉
            var noStudentNo = new Student { Id = "s2", Name = "李四" };

            var unavailableRow = new ClassShout.Teacher.ViewModels.StudentPickRow(
                noStudentNo, StudentLabelStyles.StudentNo, isSelected: true, _ => { });

            Check("呼叫页的学生行：该样式下没内容的条目不可选（哪怕请求里勾着）",
                !unavailableRow.IsSelectable && !unavailableRow.IsSelected && unavailableRow.UnavailableHint.Contains("学号"),
                $"Display=「{unavailableRow.Display}」，提示=「{unavailableRow.UnavailableHint}」");

            // —— 顶部那条错误提示：能点掉，而且到点自己收起 ——
            Check("错误提示条：停留时间比浮动提示长得多（3 秒读不完一整句原因）",
                ClassShout.Teacher.ViewModels.TeacherShellViewModel.ErrorBannerSeconds >= 10,
                $"{ClassShout.Teacher.ViewModels.TeacherShellViewModel.ErrorBannerSeconds} 秒");

            var shell = new ClassShout.Teacher.ViewModels.TeacherShellViewModel();
            shell.ErrorMessage = "连接超时：请检查地址是否正确、教室端是否已启动、防火墙是否放行。";

            Check("错误提示条：出现时是可见的，且有关闭命令",
                shell.HasError && shell.DismissErrorCommand.CanExecute(null),
                $"HasError={shell.HasError}");

            shell.DismissErrorCommand.Execute(null);

            Check("错误提示条：点关闭之后立刻收起",
                !shell.HasError,
                $"HasError={shell.HasError}");

            // 「地址已保存」「连接正常」这类一次性提示同样要能点掉、也会自己收起
            shell.ServerAddressNotice = "地址已保存。因为换了服务器，之前的登录与教室绑定都已解除。";

            Check("一次性提示：出现时可见，且有关闭命令",
                shell.HasServerAddressNotice && shell.DismissServerAddressNoticeCommand.CanExecute(null),
                $"HasServerAddressNotice={shell.HasServerAddressNotice}");

            shell.DismissServerAddressNoticeCommand.Execute(null);

            Check("一次性提示：点关闭之后立刻收起",
                !shell.HasServerAddressNotice,
                $"HasServerAddressNotice={shell.HasServerAddressNotice}");

            Check("一次性提示：停留时间比错误提示短一些（读一眼就够）",
                ClassShout.Teacher.ViewModels.TeacherShellViewModel.NoticeBannerSeconds > 0
                && ClassShout.Teacher.ViewModels.TeacherShellViewModel.NoticeBannerSeconds
                   < ClassShout.Teacher.ViewModels.TeacherShellViewModel.ErrorBannerSeconds,
                $"{ClassShout.Teacher.ViewModels.TeacherShellViewModel.NoticeBannerSeconds} 秒 < "
                + $"{ClassShout.Teacher.ViewModels.TeacherShellViewModel.ErrorBannerSeconds} 秒");

            shell.ServerAddressError = "地址格式不对，应形如 192.168.1.5:45900";
            shell.DismissServerAddressErrorCommand.Execute(null);

            Check("服务器地址那行的错误也能点掉",
                !shell.HasServerAddressError,
                $"HasServerAddressError={shell.HasServerAddressError}");

            shell.ShareError = "这串分享令牌看不懂。";
            shell.DismissShareErrorCommand.Execute(null);

            Check("分享链接的错误也能点掉",
                !shell.HasShareError,
                $"HasShareError={shell.HasShareError}");

            // —— 呼叫页的随机模式（视图模型这一层） ——
            //
            // 这一块平时是藏着的（要先从组件面板里把「随机叫人」拖进拼装区），
            // 而渲染预览时它露过两个马脚：
            //   · 小组下拉框是**空白**的 —— 设置里明明存着「A组」，
            //     原因是这个列表每次读属性都新建一份，下拉框判定"这一项不在列表里"，
            //     于是把选中项清成了空。所以这里锁"两次读到的必须是同一个实例"；
            //   · 随机模式下"还没有选学生""按小组叫""按哪种标识显示"这些
            //     只对手动勾选有意义的控件仍然出现，和抽签范围各说各的。
            AssertRandomCallPage();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", originalDataDir);
        }
    }

    /// <summary>呼叫页随机模式的界面状态（跑在临时数据目录里，不碰使用者的设置）。</summary>
    private static void AssertRandomCallPage()
    {
        var rosters = new TeacherRosterSettings();
        // 刻意让 B组 只有男生：这样"B组 + 只要女生"是一个**空范围**，
        // 才测得到"范围里一个人都没有"那句话（每个组合都有人时永远走不到那个分支）
        var parsed = RosterCsv.Parse(
            "张三,20250101,小张,A组,男\n李四,20250102,,A组,女\n赵六,20250105,六六,A组,女\n王五,,小五,B组,男",
            "三年二班");

        if (parsed.Roster is not { } roster)
        {
            Check("随机叫人页：名单能解析出来", false, "名单没解析出来，后面的断言无法进行");
            return;
        }

        rosters.Rosters.Add(roster);
        rosters.ActiveRosterId = roster.Id;
        LocalSettings.SaveRosters(rosters);

        var template = TeacherCallSettings.RandomTemplate();
        LocalSettings.SaveCalls(new TeacherCallSettings
        {
            Templates = [template],
            ActiveTemplateId = template.Id,
            RandomGroup = "A组",
            RandomGender = "男",
            RandomCount = 2,
            DecayMinutes = 40,
        });

        var vm = new ClassShout.Teacher.ViewModels.TeacherShellViewModel();

        Check("随机叫人页：模板里有「随机叫人」时整页切到随机模式",
            vm.Call.IsRandomMode && !vm.Call.IsPickMode,
            $"IsRandomMode={vm.Call.IsRandomMode}，IsPickMode={vm.Call.IsPickMode}");

        Check("随机叫人页：存着的小组会出现在下拉项里（不然下拉框会是空白）",
            vm.Call.RandomGroupChoices.Any(choice => choice.Value == "A组"),
            string.Join("、", vm.Call.RandomGroupChoices.Select(choice => choice.Label)));

        Check("随机叫人页：小组下拉项是**同一个实例**（每次新建一份会让选中项被清空）",
            ReferenceEquals(vm.Call.RandomGroupChoices, vm.Call.RandomGroupChoices),
            "两次读属性拿到同一个列表");

        Check("随机叫人页：存着的小组仍然是选中的那一项",
            vm.Call.SelectedRandomGroup.Value == "A组",
            vm.Call.SelectedRandomGroup.Label);

        Check("随机叫人页：人数与冷却也照存着的值还原",
            vm.Call.SelectedRandomCount == 2 && vm.Call.SelectedDecayMinutes == 40,
            $"{vm.Call.SelectedRandomCount} 位 / {vm.Call.SelectedDecayMinutes} 分钟");

        Check("随机叫人页：右上角写的是「发送时抽几位」，不是「还没有选学生」",
            vm.Call.PickedSummary.Contains("抽") && !vm.Call.PickedSummary.Contains("没有选"),
            vm.Call.PickedSummary);

        // 范围里只有 1 位男生（张三），而要抽 2 位：预览必须把这件事说出来
        Check("随机叫人页：预览说清范围与人数",
            vm.Call.PreviewText.Contains("「A组」") && vm.Call.PreviewText.Contains("1 位"),
            vm.Call.PreviewText);

        Check("随机叫人页：要抽的人数超过范围内的人数时写明这一点",
            vm.Call.PreviewText.Contains("只有 1 人") && vm.Call.PreviewText.Contains("2 位"),
            vm.Call.PreviewText);

        Check("随机叫人页：随机模式下预览不会把谁抽掉（因子全不变）",
            roster.Students.All(student => student.TimeFactor == 0 && student.FactorSetAt is null),
            $"最大因子 {roster.Students.Max(student => student.TimeFactor)}");

        // 换一个筛不出人的范围：界面必须说"这个范围里一个人都没有"
        vm.Call.SelectedRandomGender = vm.Call.RandomGenderChoices.First(choice => choice.Value == "女");
        vm.Call.SelectedRandomGroup = vm.Call.RandomGroupChoices.First(choice => choice.Value == "B组");

        Check("随机叫人页：范围里没人时预览直说（而不是发一条空喊话）",
            vm.Call.PreviewText.Contains("一个人都没有"),
            vm.Call.PreviewText);

        Check("随机叫人页：范围里没人的那句话里也写着是哪一段",
            vm.Call.PreviewText.Contains("「B组」") && vm.Call.PreviewText.Contains("女生"),
            vm.Call.PreviewText);

        // 换回"不限"，范围那句话要跟着变
        vm.Call.SelectedRandomGroup = vm.Call.RandomGroupChoices[0];
        vm.Call.SelectedRandomGender = vm.Call.RandomGenderChoices[0];

        Check("随机叫人页：改回「不限」之后范围也回到全班",
            vm.Call.RandomScopeText == "全班" && vm.Call.PreviewText.Contains("全班"),
            vm.Call.RandomScopeText);
    }

    /// <summary>语音页的假采集器：不碰真麦克风，只为把"开始/结束"这条路走通。</summary>
    private sealed class FakeAudioRecorder : ClassShout.Core.Audio.IAudioRecorder
    {
        public bool IsRecording { get; private set; }

        public ClassShout.Core.Audio.AudioFormat Format => ClassShout.Core.Audio.AudioFormat.Default;

#pragma warning disable CS0067 // 自检里用不到这两个事件，但接口要求有
        public event EventHandler<float>? LevelChanged;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067

        public Task StartAsync(Action<ReadOnlyMemory<byte>> onData, CancellationToken cancellationToken = default)
        {
            _ = onData;
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRecording = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 语音页：录完发出去之后，计时必须归零。
    ///
    /// 这条以前是坏的：计到 3 秒、发出去之后屏幕上还挂着「00:03」，
    /// 看起来像是"还在录"或者"刚才那段没发出去"。
    /// </summary>
    private static async Task AssertVoiceTimerResetsAsync()
    {
        ClassShout.Teacher.Services.TeacherPlatform.RegisterAudioRecorder(() => new FakeAudioRecorder());

        var voice = new ClassShout.Teacher.ViewModels.VoiceShoutViewModel(
            new ClassShout.Teacher.Services.ShoutTransportRouter())
        {
            IsConnected = true,
        };

        await voice.ToggleCommand.ExecuteAsync(null);

        Check("语音页：开始录音后计时从 0 起",
            voice.IsRecording && voice.Elapsed == 0,
            $"IsRecording={voice.IsRecording}，Elapsed={voice.ElapsedText}");

        // 假装计时器已经走了 3.2 秒（自检里没有 UI 线程的 DispatcherTimer 在跑）
        voice.Elapsed = 3.2;

        await voice.ToggleCommand.ExecuteAsync(null);

        Check("语音页：发完之后计时归零（屏幕上不该还挂着 00:03）",
            !voice.IsRecording && voice.Elapsed == 0 && voice.ElapsedText == "00:00",
            $"IsRecording={voice.IsRecording}，Elapsed={voice.ElapsedText}");
    }

    /// <summary>
    /// 「连点版本号 10 次开启开发者模式」这个手势。
    ///
    /// 为什么值得单独测：规则本身很短（数到 10，隔太久就重新数），
    /// 但写错的表现是"怎么点都不解锁" —— 而那种问题只有真人反复点才能发现，
    /// 而且解锁入口现在**有两处**（「关于」里那行版本号、「版本与更新」卡片里那行），
    /// 两处必须用同一套规则，否则会出现"一处能点开、另一处点了没反应"。
    ///
    /// 真的会把开发者模式打开（不然测不到底），所以先把数据目录指到临时目录 ——
    /// 自检绝不能改动使用者自己的 developer.json。
    /// </summary>
    private static void AssertDeveloperTapGesture()
    {
        var window = ClassShout.Design.DeveloperTapGesture.TapWindow;
        var start = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

        // —— 纯计数规则 ——
        Check("连点计数：连着点会一直累加",
            ClassShout.Design.DeveloperTapGesture.NextCount(3, start, start.AddMilliseconds(200)) == 4,
            "3 → 4");

        Check("连点计数：隔太久就重新从 1 数起",
            ClassShout.Design.DeveloperTapGesture.NextCount(9, start, start + window + TimeSpan.FromMilliseconds(1)) == 1,
            "隔了一整天再点一下不该把之前那 9 下续上");

        Check("连点计数：刚好在窗口边界上不算断开",
            ClassShout.Design.DeveloperTapGesture.NextCount(9, start, start + window) == 10,
            "1.5 秒整还算连点");

        Check("解锁阈值是 10 次",
            ClassShout.Design.DeveloperTapGesture.UnlockTapCount == 10,
            "10 次");

        // 累计到 10：模拟"连点"的计数过程，确认它确实会到达阈值
        var count = 0;
        var at = start;

        for (var i = 0; i < ClassShout.Design.DeveloperTapGesture.UnlockTapCount; i++)
        {
            at = at.AddMilliseconds(100);
            count = ClassShout.Design.DeveloperTapGesture.NextCount(count, at.AddMilliseconds(-100), at);
        }

        Check("连点 10 次会到达解锁阈值",
            count >= ClassShout.Design.DeveloperTapGesture.UnlockTapCount,
            $"数到 {count}");

        // —— 真的点一遍（数据目录先指到临时目录）——
        var originalDataDir = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        var tempDataDir = Path.Combine(Path.GetTempPath(), "cs-devtap-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", tempDataDir);

            // 已经开着的话这一条就测不到"从未开启到开启"，先手工确认初始状态
            var alreadyEnabled = ClassShout.Design.DeveloperMode.IsEnabled;

            if (alreadyEnabled)
            {
                Check("开发者模式的手势（本次运行前已开启，跳过真实解锁那一步）", true,
                    "已开启：手势会给一句「已开启」的提示");
                return;
            }

            var hints = new List<string>();
            var unlocked = 0;

            var gesture = new ClassShout.Design.DeveloperTapGesture(hints.Add, () => unlocked++);

            for (var i = 0; i < 5; i++)
            {
                gesture.Tap(start.AddSeconds(i));
            }

            Check("点到第 5 次时开始提示还差几次",
                hints.Count > 0 && hints[^1].Contains("再点"),
                hints.Count == 0 ? "一句提示都没有" : hints[^1]);

            for (var i = 5; i < ClassShout.Design.DeveloperTapGesture.UnlockTapCount; i++)
            {
                gesture.Tap(start.AddSeconds(i));
            }

            Check("连点 10 次之后开发者模式真的开了",
                ClassShout.Design.DeveloperMode.IsEnabled && unlocked == 1,
                $"IsEnabled={ClassShout.Design.DeveloperMode.IsEnabled}，解锁回调 {unlocked} 次");

            // 已开启之后再点：不该重复触发解锁，而是给一句提示
            gesture.Tap(start.AddSeconds(20));

            Check("已开启之后再点会说一句「已开启」，而不是毫无反应",
                unlocked == 1 && hints[^1].Contains("已开启"),
                hints.Count == 0 ? "没有提示" : hints[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", originalDataDir);
        }
    }

    /// <summary>
    /// 日志档位：Trace / Debug / Info / Warning / Error。
    ///
    /// 这一段守两件事：
    ///   1. 设置里那个字符串认不出来时**退回默认档**，绝不因为设置写坏就一条日志都不记；
    ///   2. 低于当前档位的日志确实既不显示也不落盘 —— "调到调试能看到广播细节"这句话，
    ///      只有在过滤真的生效时才算数（真踩过：档位改了但写文件那一步没看档位）。
    /// </summary>
    private static void AssertLogLevels()
    {
        Check("日志档位：认得出大小写不同的设置",
            AppLogLevels.Parse("debug") == AppLogLevel.Debug
            && AppLogLevels.Parse("TRACE") == AppLogLevel.Trace
            && AppLogLevels.Parse("Warning") == AppLogLevel.Warning,
            "debug / TRACE / Warning 都认出来了");

        Check("日志档位：认不出来就退回默认（信息）",
            AppLogLevels.Parse("胡说八道") == AppLogLevels.Default
            && AppLogLevels.Parse("") == AppLogLevels.Default
            && AppLogLevels.Parse(null) == AppLogLevels.Default,
            $"默认 {AppLogLevels.Default}");

        Check("日志档位：五档齐全且按严重程度排",
            AppLogLevels.All.Length == 5
            && AppLogLevels.All.SequenceEqual(
            [
                AppLogLevel.Trace,
                AppLogLevel.Debug,
                AppLogLevel.Info,
                AppLogLevel.Warning,
                AppLogLevel.Error,
            ]),
            string.Join(" < ", AppLogLevels.All.Select(AppLogLevels.Label)));

        // —— 真的写一遍文件，看过滤有没有生效 ——
        var directory = Path.Combine(Path.GetTempPath(), "cs-loglevel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        var originalDirectory = Environment.GetEnvironmentVariable(ClassShout.Core.Remote.AppLog.DirectoryVariable);
        var originalMinimum = ClassShout.Core.Remote.AppLog.Minimum;

        try
        {
            Environment.SetEnvironmentVariable(ClassShout.Core.Remote.AppLog.DirectoryVariable, directory);

            var file = Path.Combine(directory, ClassShout.Core.Remote.AppLog.FileNameFor(DateTimeOffset.Now));

            // 档位 = 调试：调试、信息、警告都该进文件；跟踪不该
            ClassShout.Core.Remote.AppLog.Minimum = AppLogLevel.Debug;
            ClassShout.Core.Remote.AppLog.Write(AppLogLevel.Trace, "网络", "跟踪级：这条不该出现");
            ClassShout.Core.Remote.AppLog.Write(AppLogLevel.Debug, "网络", "调试级：广播发往 192.168.1.255");
            ClassShout.Core.Remote.AppLog.Write("网络", "信息级：已连上教室");
            ClassShout.Core.Remote.AppLog.Write(AppLogLevel.Warning, "网络", "警告级：教室没回包");

            var text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;

            Check("日志档位：调到调试后，调试级日志真的落盘了",
                text.Contains("调试级：广播发往"),
                text.Length == 0 ? "(文件都没建出来)" : "文件里有这一条");

            Check("日志档位：仍低于档位的跟踪级不会落盘",
                !text.Contains("跟踪级：这条不该出现"),
                "跟踪那条没进去");

            Check("日志行里带着级别（与服务器那份日志同一种读法）",
                text.Contains("[信息 网络]") && text.Contains("[调试 网络]") && text.Contains("[警告 网络]"),
                text.Split('\n').FirstOrDefault(line => line.Contains("信息级："))?.Trim() ?? "(没有信息级那行)");

            // 档位 = 警告：信息级也该被挡住
            ClassShout.Core.Remote.AppLog.Minimum = AppLogLevel.Warning;
            ClassShout.Core.Remote.AppLog.Write("网络", "信息级：档位调到警告之后这条不该出现");
            ClassShout.Core.Remote.AppLog.Write(AppLogLevel.Error, "网络", "错误级：这条要留下");

            var afterRaise = File.ReadAllText(file);

            Check("日志档位：往上调之后，低于档位的直接丢掉",
                !afterRaise.Contains("档位调到警告之后这条不该出现") && afterRaise.Contains("错误级：这条要留下"),
                "信息级被挡住、错误级留着");

            Check("日志档位：IsEnabled 与档位一致",
                !ClassShout.Core.Remote.AppLog.IsEnabled(AppLogLevel.Info)
                && ClassShout.Core.Remote.AppLog.IsEnabled(AppLogLevel.Error),
                "警告档下：信息=关，错误=开");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClassShout.Core.Remote.AppLog.DirectoryVariable, originalDirectory);
            ClassShout.Core.Remote.AppLog.Minimum = originalMinimum;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 日志按天分文件、只留 7 天。
    ///
    /// 教室电脑长期没人管，日志不清理会被慢慢吃掉；而排障要看的几乎都是这几天内的事。
    /// 判断依据刻意用**文件名里的日期**而不是文件时间戳：后者会被复制、备份、
    /// 解压改掉，而"这个文件是哪一天的"是它名字里写着的。
    /// </summary>
    private static void AssertLogRetention()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-logtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        var today = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        try
        {
            // 造 10 天的日志文件
            for (var i = 0; i < 10; i++)
            {
                var day = today.AddDays(-i);
                File.WriteAllText(
                    Path.Combine(directory, ClassShout.Core.Remote.AppLog.FileNameFor(day)),
                    $"第 {i} 天的日志");
            }

            // 一个名字不像我们写的文件：不该被当成日志删掉
            var foreign = Path.Combine(directory, "notes.txt");
            File.WriteAllText(foreign, "手工放的文件");

            var removed = ClassShout.Core.Remote.AppLog.PruneFiles(directory, today, ClassShout.Core.Remote.AppLog.RetainDays);

            var left = Directory.GetFiles(directory, "classshout-*.log").Length;

            Check("日志只保留最近 7 天", left == ClassShout.Core.Remote.AppLog.RetainDays, $"剩 {left} 个");
            Check("日志清理会删掉 7 天前的文件", removed == 3, $"删了 {removed} 个");
            Check("今天那份不会被删",
                File.Exists(Path.Combine(directory, ClassShout.Core.Remote.AppLog.FileNameFor(today))),
                "今天还在");
            Check("恰好第 7 天那份还在（边界不算过期）",
                File.Exists(Path.Combine(directory, ClassShout.Core.Remote.AppLog.FileNameFor(today.AddDays(-6)))),
                "第 7 天还在");
            Check("第 8 天那份已经被删",
                !File.Exists(Path.Combine(directory, ClassShout.Core.Remote.AppLog.FileNameFor(today.AddDays(-7)))),
                "第 8 天已删");
            Check("不认识的日志文件不会被误删", File.Exists(foreign), "notes.txt 还在");

            // 文件名按天分：同一天写多次是追加到同一个文件
            var name = ClassShout.Core.Remote.AppLog.FileNameFor(today);
            Check("日志文件名里带着日期（按天分文件）",
                name == "classshout-2026-09-26.log",
                name);

            // —— 凭据不进文件日志 ——
            //
            // 两处真实存在：服务器首次启动的横幅里有「口令：xxx」（管理员口令），
            // 教室端注册成功后界面上显示「口令：xxx —— 请抄给老师」。
            // 界面显示口令是设计如此，但**落到磁盘**就多了一份暴露面：
            // 日志会被打包发给别人看、会被备份拷走。
            Check("日志里的口令会被抹掉（服务器启动横幅那种）",
                !ClassShout.Core.Remote.AppLog.Redact("      口令：hw5F+4k3VzQ8^V%J!2c^").Contains("hw5F"),
                ClassShout.Core.Remote.AppLog.Redact("      口令：hw5F+4k3VzQ8^V%J!2c^").Trim());

            Check("日志里的口令会被抹掉（教室端那条「请抄给老师」）",
                !ClassShout.Core.Remote.AppLog.Redact("口令：ABC12345 —— 请抄给老师，教师端绑定时要填这两项。").Contains("ABC12345"),
                ClassShout.Core.Remote.AppLog.Redact("口令：ABC12345 —— 请抄给老师，教师端绑定时要填这两项。"));

            Check("密钥与令牌同样抹掉",
                !ClassShout.Core.Remote.AppLog.Redact("API Key: sk-abcdef123456").Contains("sk-abcdef123456")
                && !ClassShout.Core.Remote.AppLog.Redact("token：abcdef123456").Contains("abcdef123456"),
                ClassShout.Core.Remote.AppLog.Redact("API Key: sk-abcdef123456"));

            Check("不带值的口令消息照旧留着（否则日志没法读）",
                ClassShout.Core.Remote.AppLog.Redact("管理员口令已更新。") == "管理员口令已更新。",
                ClassShout.Core.Remote.AppLog.Redact("管理员口令已更新。"));

            Check("普通日志一行都不动",
                ClassShout.Core.Remote.AppLog.Redact("教室端已启动，监听 192.168.2.124:45900")
                    == "教室端已启动，监听 192.168.2.124:45900",
                "原样保留");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 删不掉就算了，临时目录会自己清
            }
        }
    }

    /// <summary>
    /// 一个只回固定响应的 HTTP 处理器，顺便记下每次请求的地址与 User-Agent。
    ///
    /// 自检机器上没有外网也要能跑，所以"检查更新"这一段完全不打真网络。
    /// 支持按请求延迟，用来断言"多条镜像是不是真的并发在测"。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly TimeSpan _delay;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan delay = default)
        {
            _respond = respond;
            _delay = delay;
        }

        public string? LastUri { get; private set; }

        public string? LastUserAgent { get; private set; }

        /// <summary>收到的每一个请求地址（用来断言"有没有真的并发发出去"）。</summary>
        public List<string> Urls { get; } = [];

        /// <summary>同一时刻正在处理的请求数峰值。</summary>
        public int PeakConcurrency { get; private set; }

        private int _active;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.TryGetValues("User-Agent", out var values)
                ? values.FirstOrDefault()
                : null;

            lock (Urls)
            {
                Urls.Add(LastUri ?? string.Empty);

                var active = Interlocked.Increment(ref _active);
                PeakConcurrency = Math.Max(PeakConcurrency, active);
            }

            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                }

                return _respond(request);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>
    /// 组件式呼叫的拼装。
    ///
    /// 两条规则各自都会"看起来对、其实不对"：
    ///   · 组件之间**不加**自动空格 —— 空格由"文字"组件自己带，
    ///     否则"张三" + "："会变成"张三 ："，标点前多一个空格；
    ///   · 含"小组成员"时按组归并成一句，否则每个学生一条 ——
    ///     老师一次叫三个人，教室里要的是"张三、李四 来办公室"这样一句，
    ///     而不是连着闪三条几乎一样的喊话。
    /// </summary>
    private static void AssertCallComposer()
    {
        var roster = RosterCsv.Parse(
            "张三,20250101,小张,A组\n李四,20250102,,A组\n王五,,小五,B组", "三年二班").Roster;

        if (roster is null)
        {
            Check("呼叫拼装：名单能解析出来", false, "名单没解析出来，后面的断言无法进行");
            return;
        }

        var zhangsan = roster.Students.First(s => s.Name == "张三");
        var lisi = roster.Students.First(s => s.Name == "李四");
        var wangwu = roster.Students.First(s => s.Name == "王五");

        var perStudent = new CallTemplate
        {
            Components =
            [
                MessageComponent.Of(MessageComponentKinds.StudentName),
                MessageComponent.Of(MessageComponentKinds.Text, " 来 "),
                MessageComponent.Of(MessageComponentKinds.Teacher),
                MessageComponent.Of(MessageComponentKinds.Text, " 办公室"),
            ],
        };

        var single = CallComposer.Compose(perStudent, [zhangsan], roster, "数学张老师");

        Check("逐学生拼装：姓名 + 文字 + 教师名",
            single.Count == 1 && single[0] == "张三 来 数学张老师 办公室",
            single.Count == 0 ? "没有输出" : single[0]);

        var two = CallComposer.Compose(perStudent, [zhangsan, lisi], roster, "数学张老师");

        Check("选了两位学生就发两条", two.Count == 2, $"共 {two.Count} 条");

        Check("第二条对应第二位学生",
            two.Count == 2 && two[1].StartsWith("李四", StringComparison.Ordinal),
            two.Count == 2 ? two[1] : "(缺)");

        // 标点紧贴：组件之间不加自动空格
        var punctuated = new CallTemplate
        {
            Components =
            [
                MessageComponent.Of(MessageComponentKinds.StudentName),
                MessageComponent.Of(MessageComponentKinds.Text, "：请到办公室"),
            ],
        };

        var tight = CallComposer.Compose(punctuated, [zhangsan], roster, "数学张老师");

        Check("组件之间不会自动加空格（标点要紧贴姓名）",
            tight.Count == 1 && tight[0] == "张三：请到办公室",
            tight.Count == 0 ? "没有输出" : tight[0]);

        // 小组成员：按组归并，且列出**整组**的人
        var groupTemplate = new CallTemplate
        {
            Components =
            [
                MessageComponent.Of(MessageComponentKinds.Text, "请 "),
                MessageComponent.Of(MessageComponentKinds.Group),
                MessageComponent.Of(MessageComponentKinds.Text, " 来 "),
                MessageComponent.Of(MessageComponentKinds.Teacher),
                MessageComponent.Of(MessageComponentKinds.Text, " 办公室"),
            ],
        };

        var grouped = CallComposer.Compose(groupTemplate, [zhangsan], roster, "数学张老师");

        Check("小组成员拼出整组的人（不只被勾上的那位）",
            grouped.Count == 1 && grouped[0] == "请 张三、李四 来 数学张老师 办公室",
            grouped.Count == 0 ? "没有输出" : grouped[0]);

        // 同组的两位一起被勾上时只发一条
        var sameGroup = CallComposer.Compose(groupTemplate, [zhangsan, lisi], roster, "数学张老师");
        Check("同组两人只发一条（按小组归并）", sameGroup.Count == 1, $"共 {sameGroup.Count} 条");

        // 不同组各一条
        var twoGroups = CallComposer.Compose(groupTemplate, [zhangsan, wangwu], roster, "数学张老师");
        Check("不同组各发一条", twoGroups.Count == 2, $"共 {twoGroups.Count} 条：{string.Join(" / ", twoGroups)}");

        // 学号与简写组件
        var byNo = new CallTemplate
        {
            Components =
            [
                MessageComponent.Of(MessageComponentKinds.StudentNo),
                MessageComponent.Of(MessageComponentKinds.Text, " "),
                MessageComponent.Of(MessageComponentKinds.StudentShort),
            ],
        };

        var zhangNo = CallComposer.Compose(byNo, [zhangsan], roster, "数学张老师");
        Check("学号与简写组件取到对应字段",
            zhangNo.Count == 1 && zhangNo[0] == "20250101 小张",
            zhangNo.Count == 0 ? "没有输出" : $"「{zhangNo[0]}」");

        // 没填的字段输出空串，而不是"（空）"之类
        var wangNo = CallComposer.Compose(byNo, [wangwu], roster, "数学张老师");
        Check("学生没填的字段输出为空（不留占位）",
            wangNo.Count == 1 && wangNo[0].Trim() == "小五",
            wangNo.Count == 0 ? "没有输出" : $"「{wangNo[0]}」");

        Check("没选学生时一句都不发",
            CallComposer.Compose(perStudent, [], roster, "数学张老师").Count == 0,
            "返回了空表");

        Check("空模板一句都不发",
            CallComposer.Compose(new CallTemplate(), [zhangsan], roster, "数学张老师").Count == 0,
            "返回了空表");
    }

    /// <summary>
    /// 分享链接的解析与启动参数提取。
    ///
    /// 这几条纯函数决定了"老师点开链接之后有没有反应"。出错的两种表现都不会报错：
    /// 一种是把普通文字误当成令牌（于是弹一句莫名其妙的失败提示），
    /// 另一种是链接明明对、却因为系统多给了一个带引号的参数而认不出来。
    /// </summary>
    private static void AssertShareLinkParsing()
    {
        const string token = "0123456789abcdef0123456789abcdef";

        // 形态一：管理台复制出来的网页链接
        var web = ShareLink.TryExtractToken($"https://relay.example.com/share/{token}", out var webServer);
        Check("认得出网页链接里的令牌",
            web == token, web ?? "没认出来");
        Check("网页链接里的服务器地址也取了出来",
            webServer == "https://relay.example.com", webServer ?? "(空)");

        // 形态二：点"用教师端打开"时的应用链接（地址在查询串里，且被转义过）
        var app = ShareLink.TryExtractToken(
            $"classshout://claim?token={token}&server=https%3A%2F%2Frelay.example.com%3A8443",
            out var appServer);

        Check("认得出应用链接里的令牌", app == token, app ?? "没认出来");
        Check("应用链接里的服务器地址（含端口）也取了出来",
            appServer == "https://relay.example.com:8443", appServer ?? "(空)");

        // 形态三：老师从聊天记录里挑出来复制的那个令牌
        var bare = ShareLink.TryExtractToken(token, out _);
        Check("光秃秃一个令牌也认", bare == token, bare ?? "没认出来");

        // 反面：普通文字不能被当成令牌 —— 否则老师随手粘贴一句话会得到一句莫名其妙的报错
        Check("普通文字不会被当成令牌",
            ShareLink.TryExtractToken("同学们请安静", out _) is null
            && ShareLink.TryExtractToken("https://relay.example.com/", out _) is null
            && ShareLink.TryExtractToken("", out _) is null,
            "三句都不是链接的都返回了 null");

        // 启动参数：Windows 把协议链接作为普通参数交过来，且可能带引号
        Check("从启动参数里认出分享链接",
            TeacherPlatform.FindShareLink(["ClassShout.Teacher.Desktop.exe", $"\"classshout://claim?token={token}\""])
                == $"classshout://claim?token={token}",
            "连引号一起剥掉了");

        Check("参数里没有链接时返回 null",
            TeacherPlatform.FindShareLink(["app.exe", "--foo", "bar"]) is null,
            "不会把普通参数当链接");
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

    /// <summary>
    /// 投给 ClassIsland 联动插件的那条通知：三类喊话各自的正文与类别。
    ///
    /// 同一个 TcpListener 桩同时顶替插件，所以这里断言的是**离开教室端的那个报文**，
    /// 而不是"我们以为会发出去的东西"。中文在 JSON 里会被转义成 \uXXXX，
    /// 所以按解析后的字符串比对，不去搜原始字节。
    /// </summary>
    private static async Task AssertClassIslandNoticeAsync()
    {
        const string textShout = "现在讲第三题";
        const string transcript = "同学们把书翻到第三十七页";

        // 三类喊话各自应该长什么样。
        //
        // 期望文案**硬编码**在这里，不拿生产代码去生成 —— 用被测代码给自己定标准，
        // 改坏了文案这个断言也照样通过，那它就什么都没测。
        var expected = new[]
        {
            (Kind: "text", Content: textShout),
            (Kind: "voice", Content: "语音消息"),
            (Kind: "voiceTranscript",
                Content: $"语音消息{Environment.NewLine}识别结果：{transcript}"),
        };

        // 投递时走的是教室端真正走的那条路：正文先过一遍 ClassIslandNotice。
        var kindValues = new[] { ShoutNoticeKind.Text, ShoutNoticeKind.Voice, ShoutNoticeKind.VoiceTranscript };
        var rawTexts = new[] { textShout, string.Empty, transcript };

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var bodies = new string[expected.Length];
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < expected.Length; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                bodies[i] = await ReadHttpBodyAsync(stream);

                var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
        });

        var notifier = new ClassIslandNotifier($"http://127.0.0.1:{port}/shout");

        var sent = new List<bool>(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            sent.Add(await notifier.TryNotifyAsync(
                "张老师",
                ClassIslandNotice.ContentFor(kindValues[i], rawTexts[i]),
                kindValues[i]));
        }

        notifier.Dispose();

        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        Check("三类喊话都投递成功", sent.All(x => x), string.Join("、", sent));

        // 把三条报文的正文解析出来，后面的断言都基于它们 ——
        // 中文在 JSON 里是 \uXXXX 转义，按原始字节搜中文必然是搜不到的。
        var parsed = new (string? From, string? Text, string? Kind)[expected.Length];

        for (var i = 0; i < expected.Length; i++)
        {
            try
            {
                using var document = JsonDocument.Parse(bodies[i]);
                var root = document.RootElement;
                parsed[i] = (
                    root.TryGetProperty("from", out var f) ? f.GetString() : null,
                    root.TryGetProperty("text", out var t) ? t.GetString() : null,
                    root.TryGetProperty("kind", out var k) ? k.GetString() : null);
            }
            catch (JsonException)
            {
                // 留成 null，下面那几条断言会如实报失败
            }
        }

        for (var i = 0; i < expected.Length; i++)
        {
            var label = expected[i].Kind switch
            {
                "text" => "文字喊话",
                "voice" => "没配转写的语音喊话",
                _ => "语音转写结果",
            };

            var (from, text, kind) = parsed[i];

            Check($"{label}的类别正确", kind == expected[i].Kind, $"kind={kind ?? "(缺失)"}");

            Check($"{label}的内容正确", text == expected[i].Content,
                text is null ? "(没有正文)" : text.Replace(Environment.NewLine, " / "));

            Check($"{label}带上喊话人", from == "张老师", $"from={from ?? "(缺失)"}");
        }

        // 这一条就是这次改动的目的本身：识别结果必须真的躺在投出去的那条通知里。
        // 少了它，"转写结果传给了插件"只是代码里的说法，没有任何东西拦得住它退化。
        Check("转写结果那条通知里带着识别出来的字",
            parsed[2].Text?.Contains(transcript, StringComparison.Ordinal) == true,
            parsed[2].Text is null ? "(没有正文)" : "正文含识别结果");

        // 没配语音转文字时，内容就只有「语音消息」—— 不能是空的，也不能还留着别的字样。
        var voiceText = parsed[1].Text;
        Check("没配转写时内容只有「语音消息」",
            voiceText == "语音消息" && !voiceText.Contains("识别", StringComparison.Ordinal),
            voiceText ?? "(没有正文)");
    }

    /// <summary>
    /// 读一个最小可用的 HTTP 请求体：先读到头部结束拿 Content-Length，再按字节数读完。
    /// </summary>
    private static async Task<string> ReadHttpBodyAsync(NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        using var received = new MemoryStream();
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return string.Empty;
            }

            received.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(received.ToArray());
        }

        var data = received.ToArray();
        var head = Encoding.ASCII.GetString(data, 0, headerEnd);
        var contentLength = 0;

        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        while (received.Length < headerEnd + 4 + contentLength)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
        }

        var available = (int)Math.Min(contentLength, received.Length - headerEnd - 4);
        return available > 0
            ? Encoding.UTF8.GetString(received.ToArray(), headerEnd + 4, available)
            : string.Empty;
    }

    /// <summary>
    /// 一次喊话最终生效的展示参数：发送方指定了什么就用什么，没指定（或给了非法值）用教室端默认。
    ///
    /// 这一段写错了不会有任何报错，只会在教室里显示得不对 ——
    /// 而其中最容易错、后果也最难看的一条是"常驻（0）被当成没指定"：
    /// 那会让设了常驻的喊话二十秒就消失，或者反过来让没设停留的喊话永远留在屏幕上。
    /// </summary>
    private static void AssertShoutDisplayPlan()
    {
        var defaults = ShoutDisplayDefaults.Standard;

        var unspecified = ShoutDisplayPlan.Resolve(
            null, null, ShoutHoldDurations.Unspecified, speak: true, defaults);

        Check("什么都没指定时用教室端默认（窗口 / 中 / 20 秒）",
            unspecified is
            {
                Display: ShoutDisplayModes.Window,
                FontSize: ShoutFontSizes.Medium,
                HoldMs: ShoutHoldDurations.TwentySeconds,
            },
            $"{unspecified.Display} / {unspecified.FontSize} / {unspecified.HoldMs}ms");

        var popupBig = ShoutDisplayPlan.Resolve(
            ShoutDisplayModes.Popup, ShoutFontSizes.ExtraLarge, ShoutHoldDurations.OneMinute, speak: false, defaults);

        Check("指定了就按指定的来",
            popupBig is
            {
                Display: ShoutDisplayModes.Popup,
                FontSize: ShoutFontSizes.ExtraLarge,
                HoldMs: ShoutHoldDurations.OneMinute,
                Speak: false,
            },
            $"{popupBig.Display} / {popupBig.FontSize} / {popupBig.HoldMs}ms / 朗读={popupBig.Speak}");

        Check("弹窗模式能识别出来（大字区不该被点亮）",
            popupBig.IsPopup && !popupBig.IsWindow,
            $"IsPopup={popupBig.IsPopup}");

        var forever = ShoutDisplayPlan.Resolve(
            ShoutDisplayModes.Window, ShoutFontSizes.Medium, ShoutHoldDurations.Forever, speak: true, defaults);

        Check("常驻（0）不会被当成「没指定」",
            forever.HoldMs == ShoutHoldDurations.Forever && forever.IsForever && forever.Hold is null,
            $"HoldMs={forever.HoldMs}，"
            + (forever.Hold is null ? "不自动消失" : $"会 {forever.Hold} 后消失"));

        var bogus = ShoutDisplayPlan.Resolve("huge", "特大字", -5, speak: true, defaults);

        Check("非法取值退回到安全默认",
            bogus is
            {
                Display: ShoutDisplayModes.Window,
                FontSize: ShoutFontSizes.Medium,
                HoldMs: ShoutHoldDurations.TwentySeconds,
            },
            $"{bogus.Display} / {bogus.FontSize} / {bogus.HoldMs}ms");

        // 同一档在两处界面用的像素字号必须差得开：弹窗只占屏幕一角，
        // 用大字区的尺寸会直接溢出屏幕。
        var popupPixels = ShoutFontSizes.ToPixels(ShoutFontSizes.ExtraLarge, popup: true);
        var stagePixels = ShoutFontSizes.ToPixels(ShoutFontSizes.ExtraLarge, popup: false);

        Check("特大字在弹窗里比在大字区小得多",
            popupPixels < stagePixels / 2,
            $"弹窗 {popupPixels}px，大字区 {stagePixels}px");

        AssertShoutNotificationChannels();
    }

    /// <summary>
    /// 喊话提示走哪几条通道。
    ///
    /// 这一段守的是一个真实发生过的不一致：设置里选了「只看 ClassIsland 提醒」，
    /// 收到喊话时教室端不弹自己的窗（对），可点「预览弹窗」还是会弹出来（错）——
    /// 因为那条规则在两个地方各写了一遍。现在规则只有一份（ShoutNotificationChannels），
    /// 收到喊话、预览弹窗都问它，所以这里断言的就是那份规则的取值表。
    /// </summary>
    private static void AssertShoutNotificationChannels()
    {
        Check("通道：只用 ClassShout 时弹自己的窗、不投 ClassIsland",
            ShoutNotificationChannels.ShowsOwnPopup(ShoutNotificationChannel.ClassShout)
            && !ShoutNotificationChannels.NotifiesClassIsland(ShoutNotificationChannel.ClassShout),
            "ClassShout");

        Check("通道：只看 ClassIsland 时不弹自己的窗（预览也守这条）",
            !ShoutNotificationChannels.ShowsOwnPopup(ShoutNotificationChannel.ClassIsland)
            && ShoutNotificationChannels.NotifiesClassIsland(ShoutNotificationChannel.ClassIsland),
            "ClassIsland");

        Check("通道：两处都显示时两边都走",
            ShoutNotificationChannels.ShowsOwnPopup(ShoutNotificationChannel.Both)
            && ShoutNotificationChannels.NotifiesClassIsland(ShoutNotificationChannel.Both),
            "Both");

        // 三条通道至少有一条会显示，否则喊话会静悄悄地什么都不出现
        var silent = new[]
        {
            ShoutNotificationChannel.ClassShout,
            ShoutNotificationChannel.ClassIsland,
            ShoutNotificationChannel.Both,
        }.Where(channel =>
            !ShoutNotificationChannels.ShowsOwnPopup(channel)
            && !ShoutNotificationChannels.NotifiesClassIsland(channel)).ToArray();

        Check("通道：不存在「什么都不显示」的取值", silent.Length == 0, string.Join("、", silent));
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

            // ---------- 分项保护 ----------
            //
            // 这一段的重点是"改 PIN 不该把已选的项目清空"：用户只是想换个口令，
            // 而保护范围被悄悄重置成默认值的话，他不会收到任何提示。
            var settings = SettingsLock.Load();
            settings.ProtectedAreas = [ProtectedAreas.Stt, ProtectedAreas.Relay];
            settings.ProtectExit = true;
            LocalSettings.Save("settings-lock.json", settings);

            Check("受保护的项被识别出来", SettingsLock.IsAreaProtected(ProtectedAreas.Stt),
                ProtectedAreas.Label(ProtectedAreas.Stt));
            Check("没勾的项不受保护", !SettingsLock.IsAreaProtected(ProtectedAreas.Speech),
                ProtectedAreas.Label(ProtectedAreas.Speech));
            Check("退出保护能读出来", SettingsLock.IsExitProtected, "退出需要 PIN");

            var (rePinOk, rePinError) = SettingsLock.SetPin("1357");
            var afterPin = SettingsLock.Load();

            Check("换 PIN 成功", rePinOk, rePinError ?? "新 PIN 已生效");
            Check("换 PIN 之后受保护项没被清空",
                afterPin.ProtectedAreas.Count == 2 && afterPin.ProtectedAreas.Contains(ProtectedAreas.Stt),
                $"仍勾着 {afterPin.ProtectedAreas.Count} 项");

            Check("换 PIN 之后退出保护还在",
                SettingsLock.IsExitProtected,
                $"ProtectExit={afterPin.ProtectExit}");

            SettingsLock.Disable();
            Check("关闭之后不再拦人", SettingsLock.Verify("0000"), "已恢复为未启用");

            // 关掉之后即使旧配置里还留着"受保护项"，也应该一律放行 ——
            // 这是可选功能，关着的时候不该拦人。
            Check("关闭之后分项保护也一律放行",
                !SettingsLock.IsAreaProtected(ProtectedAreas.Stt) && !SettingsLock.IsExitProtected,
                "关着锁时任何项目都不再受保护");
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
    /// 本机喊话记录：最多留一百条，最新在前，最旧的被挤掉。
    ///
    /// 只压 ShoutHistory.Append 这个纯函数，不碰 LocalSettings ——
    /// 端到端工具跑在开发机上，走磁盘就会把使用者真实的喊话记录覆盖掉。
    /// 条数裁剪这条规则与"存哪儿"无关，纯函数测清楚就够了。
    /// </summary>
    private static void AssertShoutHistoryCap()
    {
        var list = new List<ShoutRecord>();
        var total = ShoutHistory.MaxCount + 5;

        for (var i = 1; i <= total; i++)
        {
            list = ShoutHistory.Append(list, new ShoutRecord($"第 {i} 条", DateTimeOffset.Now, IsVoice: false));
        }

        Check($"喊话记录最多保留 {ShoutHistory.MaxCount} 条", list.Count == ShoutHistory.MaxCount,
            $"写入 {total} 条后剩 {list.Count} 条（上限 {ShoutHistory.MaxCount}）");

        Check("最新的一条排在最前", list[0].Text == $"第 {total} 条", $"首条={list[0].Text}");

        Check("超出上限后从最旧一端丢弃", list[^1].Text == $"第 {total - ShoutHistory.MaxCount + 1} 条",
            $"末条={list[^1].Text}（更早的都该已丢弃）");
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

// ======================== 只回一句结果的几个接口 ========================
//
// 这几个接口返回的是匿名对象（"成没成 + 一句话"），Core 里没有对应的契约类型 ——
// 它们不会被两端共享，本来也不该为了测试在 Core 里加一个只有测试用的类型。
// 在这里按字段读回来，好处是断言里拿到的就是**具体值**：失败时能直接打出来，
// 而不是只能贴一段 JSON 让人自己去猜是哪个字段不对。

/// <summary>控制台改账号角色的返回：成功是 <c>{ok, role, label}</c>，被拒是 <c>{error}</c>。</summary>
internal sealed record ConsoleRoleResult(bool Ok, string? Role = null, string? Label = null, string? Error = null);

/// <summary>
/// 老师同步自己那份名单的返回：成功是 <c>{ok, rosters, templates}</c>；
/// 被班主任设为强制时是 <c>{ok:false, error, source, enforced}</c>（HTTP 仍是 200）。
/// </summary>
internal sealed record TeacherRosterPutResult(
    bool Ok,
    int Rosters = 0,
    int Templates = 0,
    string? Error = null,
    string? Source = null,
    bool Enforced = false);

/// <summary>班主任上传班级统一名单的返回：<c>{ok, students, rosters, enforced}</c>，被拒时是 <c>{ok:false, error}</c>。</summary>
internal sealed record ClassroomRosterPutResult(
    bool Ok,
    int Students = 0,
    int Rosters = 0,
    bool Enforced = false,
    string? Error = null);

/// <summary>班主任给自己管的班授权某位老师的返回：<c>{ok, already, message}</c>。</summary>
internal sealed record HeadTeacherGrantResult(bool Ok, bool Already, string? Message, string? Error = null);
