using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ClassShout.Core.Audio;

/// <summary>第三方语音协议：POST JSON，返回原始 PCM WAV；禁止重定向携带密钥。</summary>
public sealed class HttpTtsClient : IDisposable
{
    private const int MaxAudioBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _loopbackHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<byte[]> SynthesizeAsync(HttpTtsSettings settings, string text, int rate,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("第三方语音地址必须是有效的 HTTP 或 HTTPS 地址，且不能包含凭据或片段。");
        if (endpoint.Scheme == "http" && !string.IsNullOrEmpty(settings.ApiKey) && !settings.AllowInsecureHttp)
            throw new InvalidOperationException("携带密钥的 HTTP 连接需要显式允许明文传输。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 10000)
            throw new InvalidOperationException("第三方朗读文字长度必须在 1–10000 字之间。");
        if (settings.ApiKey.Length > 4096 || settings.ApiKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '~' or '+' or '/' or '-' or '=')))
            throw new InvalidOperationException("第三方语音密钥包含无效字符或过长。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { text, voice = settings.Voice, rate = Math.Clamp(rate, -10, 10), format = "wav" }),
        };
        if (!string.IsNullOrEmpty(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        var client = endpoint.IsLoopback ? _loopbackHttp : _http;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // 不把服务端正文放入异常，避免第三方错误响应回显密钥。
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"第三方语音服务拒绝请求（HTTP {(int)response.StatusCode}）。");
        if (response.Content.Headers.ContentLength > MaxAudioBytes)
            throw new InvalidDataException("第三方语音响应超过 8 MiB。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (result.Length + read > MaxAudioBytes) throw new InvalidDataException("第三方语音响应超过 8 MiB。");
            result.Write(buffer, 0, read);
        }
        var audio = result.ToArray();
        if (!WavCodec.TryDecode(audio, out var format, out var pcm) || !format.IsSupported
            || format.BitsPerSample != 16 || pcm.Length == 0)
            throw new InvalidDataException("第三方语音必须返回受支持的 16 位 PCM WAV。");
        return audio;
    }

    public void Dispose() { _http.Dispose(); _loopbackHttp.Dispose(); }
}

public sealed class HttpTtsSettings
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Voice { get; set; } = "";
    public bool AllowInsecureHttp { get; set; }
    public bool FallbackToSystem { get; set; } = true;
}
