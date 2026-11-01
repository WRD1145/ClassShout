using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassShout.Core.Audio;
using ClassShout.Core.Remote;

namespace ClassShout.EndToEnd;

internal static partial class Program
{
    private static async Task<int> AssertMaintenanceAsync()
    {
        var previous = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        // 每次使用独立目录，不接触实际账户；保留失败现场便于排查。
        var root = Path.Combine(Path.GetTempPath(), "ClassShout-maintenance-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", root);
        const string secret = "MAINTENANCE_SECRET_CANARY_DO_NOT_EXPORT";
        try
        {
            LocalSettings.Save("teacher.json", new { Token = secret });
            LocalSettings.Save("classroom.json", new { Secret = secret });
            LocalSettings.Save("extensions.json", new { Token = secret });
            LocalSettings.Save("bells.json", new BellSettings { Bells = [new() { Label = "迁移上课铃", Time = "09:12", AudioWav = Convert.ToBase64String([1, 2, 3]) }] });
            LocalSettings.SavePhrases(new() { Phrases = ["请翻到第二页"] });
            var speech = new ClassroomSpeechSettings
            {
                Engine = SpeechEngine.Http, Volume = 63, Rate = -2, SystemVoice = "原设备音色",
                Http = new() { Endpoint = "https://example.invalid/private/" + secret + "?key=" + secret, ApiKey = secret, Voice = "中文音色", FallbackToSystem = false },
            };
            LocalSettings.SaveSpeech(speech);
            var classroomBytes = ConfigurationTransfer.Export(true);
            var teacherBytes = ConfigurationTransfer.Export(false);
            var classroom = ConfigurationTransfer.Parse(classroomBytes);
            var teacher = ConfigurationTransfer.Parse(teacherBytes);
            var exported = Encoding.UTF8.GetString(classroomBytes) + Encoding.UTF8.GetString(teacherBytes);
            Check("迁移：两个角色备份均不含密钥和服务地址", !exported.Contains(secret) && !exported.Contains("example.invalid") && !exported.Contains("ApiKey"), "字段白名单，不靠关键词替换");
            Check("迁移：铃声及内嵌音频往返保留", classroom.Bells?.Bells.Single().AudioWav == "AQID" && classroom.Bells.Bells[0].Time == "09:12", "JSON 往返");
            Check("迁移：快捷回复默认配置往返可读", classroom.Replies?.QuickReplies.Count > 0, "包含组件和只读摘要");
            Check("迁移：音色、音量和语速保留", classroom.Speech is { Volume: 63, Rate: -2, HttpVoice: "中文音色", FallbackToSystem: false }, "仅迁移非凭据设置");
            Check("迁移：教师常用语往返保留", teacher.Phrases?.Phrases.Single() == "请翻到第二页" && teacher.Speech is null, "教师端不导出教室配置");
            speech.Http.ApiKey = "目标设备原密钥";
            speech.Http.Endpoint = "https://target.invalid/tts";
            LocalSettings.SaveSpeech(speech);
            LocalSettings.Save("bells.json", new BellSettings());
            ConfigurationTransfer.Import(classroom, true);
            var merged = LocalSettings.LoadSpeech();
            Check("迁移：导入保留目标设备地址和密钥", merged.Http.ApiKey == "目标设备原密钥" && merged.Http.Endpoint == "https://target.invalid/tts", "不覆盖本机凭据");
            Check("迁移：铃声和朗读参数写入落盘", LocalSettings.LoadBells().Bells.Single().Label == "迁移上课铃" && merged.Volume == 63 && merged.Rate == -2, "重新读取配置文件");
            Check("迁移：账户和扩展授权文件未变", File.ReadAllText(Path.Combine(root, "teacher.json")).Contains(secret) && File.ReadAllText(Path.Combine(root, "extensions.json")).Contains(secret), "导入不启用插件、不迁移登录");
            LocalSettings.SavePhrases(new() { Phrases = ["临时文字"] });
            ConfigurationTransfer.Import(teacher, false);
            Check("迁移：教师常用语导入落盘", LocalSettings.LoadPhrases().Phrases.Single() == "请翻到第二页", "重新加载");
            bool Reject(string json)
            {
                try { ConfigurationTransfer.Parse(Encoding.UTF8.GetBytes(json)); return false; }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { return true; }
            }
            Check("迁移安全：拒绝未知凭据字段", Reject("{\"FormatVersion\":1,\"Token\":\"伪造\"}"), "严格格式");
            Check("迁移安全：拒绝写入路径字段", Reject("{\"Path\":\"../../teacher.json\"}"), "目标文件名不可指定");
            Check("迁移安全：拒绝未来版本和无效引擎", Reject("{\"FormatVersion\":999}") && Reject("{\"Speech\":{\"Engine\":999}}"), "版本与枚举检查");
            Check("迁移安全：拒绝空回复组件", Reject("{\"Replies\":{\"QuickReplies\":[{\"Components\":null}]}}") && Reject("{\"Replies\":{\"QuickReplies\":[null]}}"), "导入预检，不让启动时崩溃");
            var oversized = false;
            try { ConfigurationTransfer.Parse(new byte[ConfigurationTransfer.MaxBytes + 1]); } catch (InvalidDataException) { oversized = true; }
            Check("迁移安全：拒绝超限备份", oversized, "16 MiB 上限");
            var wrongRole = false;
            try { ConfigurationTransfer.Import(teacher, true); } catch (InvalidDataException) { wrongRole = true; }
            Check("迁移：错误角色导入不写文件", wrongRole, "教师备份没有教室设置");
            var bellPath = Path.Combine(root, "bells.json");
            var originalBells = File.ReadAllBytes(bellPath);
            var replyPath = Path.Combine(root, "classroom-reply.json");
            File.Delete(replyPath);
            Directory.CreateDirectory(replyPath);
            classroom.Bells!.Bells[0].Label = "不应留下的半份导入";
            var failed = false;
            try { ConfigurationTransfer.Import(classroom, true); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
            Check("迁移：第二个文件写入失败时回滚第一个", failed && File.ReadAllBytes(bellPath).SequenceEqual(originalBells), "真实文件系统失败和恢复");
            Check("迁移：失败未残留暂存文件", Directory.GetFiles(root, "*.tmp").Length == 0, "原子替换清理");
            Directory.Delete(replyPath);

            var reportItems = new List<DiagnosticItem>();
            var relay = await MaintenanceHttpAsync("{\"ok\":true}", address => ConnectionDiagnostics.ProbeRelayAsync(address + "/proxy?key=" + secret, default));
            reportItems.Add(relay.Item);
            Check("诊断：真实 HTTP 健康探测通过", relay.Item.Status == "通过" && relay.Request.StartsWith("GET /proxy/api/health "), "保留反向代理路径、移除查询参数");
            Check("诊断安全：健康探测不转发地址查询密钥", !relay.Request.Contains(secret), "未发送配置中的查询凭据");
            var notReady = await MaintenanceHttpAsync("{\"ok\":false}", address => ConnectionDiagnostics.ProbeRelayAsync(address, default));
            reportItems.Add(notReady.Item);
            Check("诊断：HTTP 200 未就绪不能算通过", notReady.Item.Status == "失败", "检查健康字段");
            var tooLarge = await MaintenanceHttpAsync(new string('x', 65537), address => ConnectionDiagnostics.ProbeRelayAsync(address, default));
            reportItems.Add(tooLarge.Item);
            Check("诊断安全：限制响应大小", tooLarge.Item.Status == "失败", "64 KiB 上限");
            var embedded = await ConnectionDiagnostics.ProbeRelayAsync("https://user:" + Uri.EscapeDataString(secret) + "@example.invalid", default);
            reportItems.Add(embedded);
            Check("诊断安全：拒绝内嵌用户名密码地址", embedded.Status == "失败", "请求前拒绝");
            var tts = await MaintenanceHttpAsync(secret, address =>
            {
                LocalSettings.SaveSpeech(new() { Engine = SpeechEngine.Http, Http = new() { Endpoint = address } });
                return ConnectionDiagnostics.ProbeSpeechAsync(true, default);
            });
            reportItems.Add(tts.Item);
            Check("诊断：无效 TTS 音频明确失败", tts.Item.Status == "失败", "实际合成请求，不将错误正文当音频");
            var wav = WavCodec.Encode(AudioFormat.Default, new byte[6400]);
            var validTts = await MaintenanceHttpAsync(wav, address =>
            {
                LocalSettings.SaveSpeech(new() { Engine = SpeechEngine.Http, Http = new() { Endpoint = address } });
                return ConnectionDiagnostics.ProbeSpeechAsync(true, default);
            }, "audio/wav");
            reportItems.Add(validTts.Item);
            Check("诊断：有效 TTS WAV 实际合成通过", validTts.Item.Status == "通过", "不启动扬声器播放");
            var report = JsonSerializer.Serialize(new DiagnosticReport(1, DateTimeOffset.UtcNow, "classroom", reportItems));
            Check("诊断安全：报告不带失败正文、凭据和地址", !report.Contains(secret) && !report.Contains("example.invalid") && !report.Contains("target.invalid"), "成功及失败报告均检查");
            return _failed == 0 ? 0 : 1;
        }
        finally { Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", previous); }
    }

    private static Task<(DiagnosticItem Item, string Request)> MaintenanceHttpAsync(string body, Func<string, Task<DiagnosticItem>> probe)
        => MaintenanceHttpAsync(Encoding.UTF8.GetBytes(body), probe, "application/json");

    private static async Task<(DiagnosticItem Item, string Request)> MaintenanceHttpAsync(byte[] body, Func<string, Task<DiagnosticItem>> probe, string type)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var respond = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token);
                    header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var request = Encoding.ASCII.GetString(header.ToArray());
                var lengthLine = request.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthLine is not null)
                    await stream.ReadExactlyAsync(new byte[int.Parse(lengthLine.Split(':')[1])], timeout.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
                return request;
            });
            var item = await probe($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            return (item, await respond);
        }
        finally { listener.Stop(); }
    }
}
