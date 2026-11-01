using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ClassShout.Core.Audio;

namespace ClassShout.Core.Remote;

public sealed record DiagnosticItem(string Component, string Status, string Detail, long ElapsedMs = 0);
public sealed record DiagnosticReport(int FormatVersion, DateTimeOffset CreatedAt, string Role, IReadOnlyList<DiagnosticItem> Items);

/// <summary>报告仅由固定说明组成，不序列化配置、地址、响应正文或异常消息。</summary>
public static class ConnectionDiagnostics
{
    public static async Task<DiagnosticItem> ProbeRelayAsync(string? address, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(address)) return new("中继", "未配置", "尚未配置服务器地址；局域网直连不需要中继。");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return new("中继", "失败", "服务器地址格式无效或包含嵌入式凭据。");
        var builder = new UriBuilder(uri) { Query = "", Fragment = "", Path = uri.AbsolutePath.TrimEnd('/') + "/api/health" };
        var result = await ProbeJsonAsync(builder.Uri, token);
        return result.Available
            ? new("中继", "通过", "健康接口响应正常；不代表账号授权或教室已在线。", result.Elapsed)
            : new("中继", "失败", result.Error, result.Elapsed);
    }

    public static async Task<DiagnosticItem> ProbeClassIslandAsync(CancellationToken token)
    {
        var result = await ProbeJsonAsync(new Uri("http://127.0.0.1:45902/schedule/today"), token, "available");
        return result.Available ? new("ClassIsland", "通过", "本机插件和当天课表可读取。", result.Elapsed)
            : new("ClassIsland", "未通过", result.Error, result.Elapsed);
    }

    public static async Task<DiagnosticItem> ProbeSpeechAsync(bool systemAvailable, CancellationToken token)
    {
        var settings = LocalSettings.LoadSpeech();
        if (settings.Engine == SpeechEngine.System)
            return new("TTS", systemAvailable ? "待试听" : "失败", systemAvailable
                ? "检测到系统语音引擎；请使用试听检查扬声器。" : "未检测到系统语音引擎。");
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (settings.Engine == SpeechEngine.Http)
            {
                using var client = new HttpTtsClient();
                await client.SynthesizeAsync(settings.Http ?? new(), "ClassShout 连接诊断", 0, deadline.Token);
                return new("TTS", "通过", "第三方服务已返回有效 WAV；未播放，扬声器仍需试听。", clock.ElapsedMilliseconds);
            }
            using var http = new HttpClient();
            using var edge = new EdgeTtsClient(http);
            var voices = await edge.GetVoicesAsync(deadline.Token);
            return new("TTS", voices.Count > 0 ? "待试听" : "失败", voices.Count > 0
                ? "在线音色接口可达；尚未验证实际合成和播放。" : "在线服务未返回音色。", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        { return new("TTS", "失败", "服务地址、鉴权、网络或音频格式检查失败；请在本机核对配置后试听。", clock.ElapsedMilliseconds); }
    }

    private static async Task<(bool Available, string Error, long Elapsed)> ProbeJsonAsync(Uri uri, CancellationToken token, string property = "ok")
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !uri.IsLoopback });
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return (false, $"接口拒绝请求（HTTP {(int)response.StatusCode}）。", clock.ElapsedMilliseconds);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[65537];
            var count = 0;
            while (count < bytes.Length)
            {
                var n = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token);
                if (n == 0) break;
                count += n;
            }
            if (count > 65536) return (false, "诊断响应过大。", clock.ElapsedMilliseconds);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var valid = json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
            return (valid, valid ? "" : "接口可达，但未确认服务就绪或当天课表可用。", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        { return (false, ex.HttpRequestError == HttpRequestError.SecureConnectionError ? "TLS 证书验证失败。" : "无法连接服务，请检查进程、地址和网络。", clock.ElapsedMilliseconds); }
        catch (Exception) { return (false, "服务超时或响应格式无效。", clock.ElapsedMilliseconds); }
    }
}

public interface IMaintenanceContext
{
    bool IsClassroomMaintenance { get; }
    bool CanImportConfiguration { get; }
    Task<IReadOnlyList<DiagnosticItem>> DiagnoseAsync(CancellationToken token);
}
