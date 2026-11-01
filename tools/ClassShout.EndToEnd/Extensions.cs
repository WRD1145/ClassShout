using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassShout.Core.Audio;
using ClassShout.Core.Net;
using ClassShout.Core.Remote;

namespace ClassShout.EndToEnd;

internal static partial class Program
{
    private static async Task<int> AssertExtensionsAsync()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        await using var server = new LocalIpcServer(secret, (method, path, body) => Task.FromResult(new IpcResponse(200, new { method, path, body })), 0);
        server.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        using var unauthorized = await http.GetAsync("/v1/status");
        Check("IPC：缺少令牌被拒绝", unauthorized.StatusCode == HttpStatusCode.Unauthorized, "真实 TCP HTTP 401");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var valid = await http.GetAsync("/v1/status");
        Check("IPC：正确令牌可访问", valid.IsSuccessStatusCode, "真实 TCP HTTP 200");
        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/v1/status");
        crossSite.Headers.Add("Origin", "https://example.invalid");
        using var denied = await http.SendAsync(crossSite);
        Check("IPC：跨站请求即使有令牌也拒绝", denied.StatusCode == HttpStatusCode.BadRequest, "HTTP 400");
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/v1/status");
        rebound.Headers.Host = "example.invalid";
        using var blocked = await http.SendAsync(rebound);
        Check("IPC：非回环 Host 被拒绝", blocked.StatusCode == HttpStatusCode.BadRequest, "HTTP 400");
        var oversizedRejected = false;
        try
        {
            using var tooLarge = await http.PostAsync("/v1/shouts", new StringContent(new string('x', 32769)));
            oversizedRejected = tooLarge.StatusCode == HttpStatusCode.BadRequest;
        }
        catch (HttpRequestException) { oversizedRejected = true; }
        Check("IPC：超限正文被拒绝", oversizedRejected, "32 KiB 上限，拒绝或关闭连接");

        using var voice = new HttpTtsClient();
        var rejected = false;
        try { await voice.SynthesizeAsync(new() { Endpoint = "file:///tmp/audio.wav" }, "测试", 0); }
        catch (InvalidOperationException) { rejected = true; }
        Check("HTTP TTS：禁止文件协议", rejected, "只接受 HTTP(S)");
        rejected = false;
        try { await voice.SynthesizeAsync(new() { Endpoint = "http://127.0.0.1:1", ApiKey = "secret" }, "测试", 0); }
        catch (InvalidOperationException) { rejected = true; }
        Check("HTTP TTS：明文密钥需要显式授权", rejected, "请求前拒绝");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var mock = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                var collected = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    await stream.ReadExactlyAsync(one);
                    collected.Add(one[0]);
                    if (collected.Count >= 4 && collected.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var header = Encoding.ASCII.GetString(collected.ToArray());
                var length = int.Parse(header.Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var body = new byte[length];
                await stream.ReadExactlyAsync(body);
                using var document = JsonDocument.Parse(body);
                Check("HTTP TTS：UTF-8 正文和音色传输正确", document.RootElement.GetProperty("text").GetString() == "中文试听" && document.RootElement.GetProperty("voice").GetString() == "test", "实际 HTTP 请求");
                var wav = WavCodec.Encode(AudioFormat.Default, new byte[6400]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wav.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(wav);
            });
            var audio = await voice.SynthesizeAsync(new() { Endpoint = $"http://127.0.0.1:{port}", Voice = "test" }, "中文试听", 0);
            await mock.WaitAsync(TimeSpan.FromSeconds(10));
            Check("HTTP TTS：有效 WAV 可解码", WavCodec.TryDecode(audio, out _, out var pcm) && pcm.Length == 6400, "音频经过真实 HTTP 返回");
        }
        finally { listener.Stop(); }
        rejected = false;
        try { _ = new ProcessPluginHost(new() { Enabled = false, Executable = Environment.ProcessPath! }, (_, _, _) => Task.FromResult(new IpcResponse(200, new { })), _ => { }); }
        catch (InvalidOperationException) { rejected = true; }
        Check("插件：未授权插件不能启动", rejected, "默认关闭");
        var pluginCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var plugin = new ProcessPluginHost(new()
        {
            Enabled = true, Executable = Environment.ProcessPath!, Arguments = ["--plugin-fixture"],
        }, (method, path, _) =>
        {
            if (method == "GET" && path == "/v1/status") pluginCalled.TrySetResult();
            return Task.FromResult(new IpcResponse(200, new { ready = true }));
        }, _ => { }))
        {
            plugin.Start();
            await pluginCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check("插件：非法空字段后仍可继续正常命令", pluginCalled.Task.IsCompletedSuccessfully, "真实独立进程 JSON 行交互");
        }
        Console.WriteLine($"扩展测试：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }
}
