using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClassShout.Core.Audio;

/// <summary>
/// 语音转文字的配置。密钥由老师自己填，存在本机。
///
/// 为什么不让服务器统一持有密钥：那需要服务端新增接口、要防滥用、还要有人付账，
/// 而且老师的课堂教学内容会经过学校运维的账号 —— 各单位自己填自己的最省事，
/// 也最不容易在权限上扯皮。代价是每位老师要自己配一次。
/// </summary>
public sealed class SttSettings
{
    /// <summary>是否启用。默认关闭 —— 它需要密钥，不能默认替用户打开。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 接口地址。默认 OpenAI，但填任何兼容 /v1/audio/transcriptions 的服务都可以
    /// （本地 whisper.cpp 服务、Azure OpenAI、各类中转都兼容这个形状）。
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>密钥。只存在本机，不上传、不写日志。</summary>
    public string? ApiKey { get; set; }

    /// <summary>模型名。</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>提示语言，留空由服务自行判断。中文课堂填 zh 通常更准。</summary>
    public string? Language { get; set; } = "zh";

    /// <summary>配置是否完整到可以调用。</summary>
    public bool IsUsable => Enabled
                            && !string.IsNullOrWhiteSpace(ApiKey)
                            && !string.IsNullOrWhiteSpace(BaseUrl)
                            && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>转写结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Text">识别出的文字（成功时）。</param>
/// <param name="Error">失败原因（失败时），已是可以直接显示给用户的文案。</param>
public readonly record struct SttResult(bool Ok, string? Text, string? Error);

/// <summary>
/// 把原始 PCM 包成 WAV。
///
/// 语音识别接口收的是"文件"，而我们在内存里只有一段裸 PCM，
/// 所以必须补上 44 字节的 WAV 头 —— 服务端要靠它知道采样率和位深，
/// 否则会把 16 kHz 的音频按别的采样率解读，识别结果会变成乱码。
/// </summary>
public static class WavWriter
{
    /// <summary>给一段 PCM 加上 WAV 头。</summary>
    public static byte[] ToWav(byte[] pcm, AudioFormat format)
    {
        var sampleRate = format.SampleRate;
        var channels = format.Channels;
        var bitsPerSample = format.BitsPerSample;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                       // fmt 块长度
        writer.Write((short)1);                 // 1 = PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>
/// 语音转文字客户端。
///
/// 用途：老师按住说完一句，把它转成文字显示在教室的大字区。
/// 教室里噪声大、或者有听障学生时，光靠声音是不够的 —— 看见字才真的听得清。
///
/// 走的是 OpenAI 的 /v1/audio/transcriptions 形状（multipart 上传音频文件），
/// 这是事实标准：whisper.cpp 的 server、Azure OpenAI、以及各类兼容服务都用这个形状，
/// 所以地址可配之后，"支持 OpenAI STT" 顺带就等于支持了一大批自建方案。
///
/// 密钥只在本机使用，这里也刻意不把请求内容写进任何日志 ——
/// 转写的是老师的课堂讲话，属于教学内容。
/// </summary>
public sealed class SttClient
{
    /// <summary>单次转写的超时。一句话的识别通常几秒，超过这个时间说明网络或服务有问题。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public SttClient(HttpClient http) => _http = http;

    /// <summary>转写一段 PCM。失败时返回可直接显示的文案，不抛异常。</summary>
    public async Task<SttResult> TranscribeAsync(
        byte[] pcm,
        AudioFormat format,
        SttSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsUsable)
        {
            return new SttResult(false, null, "语音转文字尚未配置好（需要接口地址与密钥）。");
        }

        if (pcm.Length == 0)
        {
            return new SttResult(false, null, "没有录到声音。");
        }

        try
        {
            var wav = WavWriter.ToWav(pcm, format);

            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "shout.wav");
            content.Add(new StringContent(settings.Model), "model");

            if (!string.IsNullOrWhiteSpace(settings.Language))
            {
                content.Add(new StringContent(settings.Language), "language");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/audio/transcriptions")
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new SttResult(false, null, DescribeFailure(response.StatusCode, body));
            }

            var text = ExtractText(body);
            return string.IsNullOrWhiteSpace(text)
                ? new SttResult(false, null, "识别服务没有返回文字。")
                : new SttResult(true, text, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SttResult(false, null, "语音转文字超时，请检查网络或接口地址。");
        }
        catch (HttpRequestException ex)
        {
            return new SttResult(false, null, $"连不上语音转文字服务：{ex.Message}");
        }
        catch (JsonException)
        {
            return new SttResult(false, null, "语音转文字服务返回了无法解析的内容。");
        }
    }

    /// <summary>
    /// 把失败翻译成人能看懂的话。
    ///
    /// 401/403 是老师最可能踩到的：密钥填错、或者账号没开通该模型。
    /// 直接把原始 JSON 甩到界面上没用，得告诉他去改哪里。
    /// </summary>
    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
        => status switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "密钥无效（401）。请到设置里检查 API Key。",
            System.Net.HttpStatusCode.Forbidden =>
                "密钥被拒绝（403）。可能没有开通该模型，或密钥权限不足。",
            System.Net.HttpStatusCode.NotFound =>
                "接口地址不对（404）。请检查地址，通常应以 /v1 结尾。",
            System.Net.HttpStatusCode.TooManyRequests =>
                "请求过于频繁或余额不足（429）。",
            _ => $"识别失败（HTTP {(int)status}）：{Trim(body)}",
        };

    private static string Trim(string body)
        => body.Length <= 160 ? body : body[..160] + "…";

    /// <summary>兼容两种返回：{"text":"..."} 与纯文本。</summary>
    private static string? ExtractText(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
    }
}