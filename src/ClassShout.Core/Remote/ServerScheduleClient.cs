using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClassShout.Core.Remote;

/// <summary>
/// 把定时喊话交给服务器。
///
/// 这是"教师端不必在后台运行"的那一半：任务排到服务器上之后，到点由服务器自己发 ——
/// 老师关掉手机、锁屏、甚至关机过周末，教室里照样响。
/// 另一半是本机定时（<c>ShoutScheduler</c>），它只在应用运行时有效，
/// 但不需要登录、也不依赖服务器可达。
/// </summary>
public sealed class ServerScheduleClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TeacherRelaySettings _settings;

    public ServerScheduleClient(HttpClient http, TeacherRelaySettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>能不能用（已登录且配了服务器地址）。</summary>
    public bool IsAvailable => _settings.IsSignedIn && !string.IsNullOrWhiteSpace(_settings.ServerUrl);

    /// <summary>排一条文字定时。</summary>
    public Task<(bool Ok, ScheduledShoutDto? Item, string? Error)> CreateTextAsync(
        ScheduleShoutRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(RelayPaths.AuthSchedule, content: JsonContent.Create(request, options: JsonOptions), cancellationToken);

    /// <summary>
    /// 排一条语音定时。
    ///
    /// 音频单独作为文件传（multipart）：一段几十秒的语音是上兆的 PCM，
    /// 塞进 JSON 里 base64 还要再大三成。
    /// </summary>
    public async Task<(bool Ok, ScheduledShoutDto? Item, string? Error)> CreateVoiceAsync(
        DateTimeOffset sendAt,
        IReadOnlyList<string> targetUuids,
        VoiceRecording recording,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return (false, null, "还没有登录，不能把定时交给服务器。");
        }

        using var content = new MultipartFormDataContent
        {
            { new StringContent(sendAt.ToString("O")), "sendAt" },
            { new StringContent(string.Join(',', targetUuids)), "targetUuids" },
        };

        var audio = new ByteArrayContent(recording.Wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "audio", "clip.wav");

        return await SendAsync(RelayPaths.AuthSchedule + "/voice", content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出自己的服务器定时（待发在前，历史在后）。</summary>
    public async Task<IReadOnlyList<ScheduledShoutDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(RelayPaths.AuthSchedule));
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content
                .ReadFromJsonAsync<List<ScheduledShoutDto>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 服务器不可达时界面照旧显示本机那几条，不弹错误 —— 老师在上课
            return [];
        }
    }

    /// <summary>取消一条服务器定时。返回错误文案；成功时为 null。</summary>
    public async Task<string?> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return "还没有登录，不能取消服务器上的定时。";
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                Url(string.Format(RelayPaths.AuthScheduleItem, Uri.EscapeDataString(id))));

            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadError(body) ?? "取消失败。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"没能连上服务器：{ex.Message}";
        }
    }

    private async Task<(bool Ok, ScheduledShoutDto? Item, string? Error)> SendAsync(
        string path,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return (false, null, "还没有登录，不能把定时交给服务器。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = content };
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, ReadError(body) ?? $"服务器拒绝了这条定时（HTTP {(int)response.StatusCode}）。");
            }

            var parsed = JsonSerializer.Deserialize<ScheduleShoutResponse>(body, JsonOptions);

            return parsed is { Ok: true, Item: not null }
                ? (true, parsed.Item, null)
                : (false, null, parsed?.Error ?? "服务器没有接受这条定时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, $"没能连上服务器：{ex.Message}");
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Url(string path) => $"{(_settings.ServerUrl ?? string.Empty).TrimEnd('/')}{path}";
}

/// <summary>一段要上传的语音：已经包成 WAV 的字节。</summary>
/// <param name="Wav">WAV 字节。</param>
/// <param name="Seconds">时长（秒），只用于界面提示。</param>
public readonly record struct VoiceRecording(byte[] Wav, double Seconds);
