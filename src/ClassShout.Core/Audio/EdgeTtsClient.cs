using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassShout.Core.Audio;

/// <summary>Edge 在线语音可选的一个音色。</summary>
/// <param name="ShortName">传给合成接口的名字，如 zh-CN-XiaoxiaoNeural。</param>
/// <param name="FriendlyName">界面上显示的名字。</param>
/// <param name="Locale">语言区域，如 zh-CN。</param>
/// <param name="Gender">性别，仅用于展示。</param>
public sealed record EdgeVoiceInfo(string ShortName, string FriendlyName, string Locale, string Gender)
{
    /// <summary>界面上的显示串。</summary>
    public string Display => $"{FriendlyName}（{Gender}）";
}

/// <summary>
/// 微软 Edge 的「朗读」在线语音合成（readaloud 接口）。
///
/// 为什么值得接：教室端的音质瓶颈一直是系统 SAPI —— 那是十几年前的拼接式合成，
/// 念长句时机械感很明显。Edge 用的是神经网络音色，同一句话的自然度高一个档次，
/// 而且不需要 API key、不需要账号。
///
/// 但它是**在线**的，所以只能作为可选引擎：教室网被隔离时它一次都连不上，
/// 那种情况下必须自动回落到系统语音，而不是让教室端"哑掉"。
/// 这一条由调用方（教室端的语音引擎选择）保证，这里只负责协议。
///
/// 协议要点（对着 rany2/edge-tts 的实现核过）：
///   · 握手要带 Sec-MS-GEC：把当前时间按 5 分钟向下取整，加上 Windows 纪元偏移，
///     换算成 100 纳秒刻度，拼上固定客户端令牌，再做 SHA-256 取大写十六进制；
///   · Sec-MS-GEC-Version 必须是一个**较新的** Edge 版本号。
///     这一项会过期：用 130.x 会拿到 403，换成 143.x 就通了。
///     所以它单独抽成常量并写了注释，将来再遇 403 先改这里；
///   · 文本帧发 speech.config 与 ssml，二进制帧回音频，
///     每帧前两字节是大端的头部长度，头部之后才是 MP3 数据。
///
/// 刻意不引第三方库：这点协议用 ClientWebSocket 直接写就够了，
/// 而教室端要跑在没有外网的机器上，依赖越少越好。
/// </summary>
public sealed class EdgeTtsClient : IDisposable
{
    /// <summary>微软公开的客户端令牌，Edge 浏览器本身就用这个值。</summary>
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    /// <summary>
    /// Edge 版本号。**这一项会过期**：服务端要求它是较新的版本，
    /// 用旧版本会直接 403（实测 130.x 已被拒，143.x 可用）。
    /// 将来再遇到 403，第一件事就是把它换成当前的 Edge 版本。
    /// </summary>
    private const string ChromiumFullVersion = "143.0.3650.75";

    private const string ChromiumMajorVersion = "143";

    private const string BaseUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud";

    /// <summary>输出格式固定为 24 kHz 单声道 48 kbps MP3，这是该接口支持的一种。</summary>
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    /// <summary>默认音色：中文女声，课堂场景下播报清晰度最好。</summary>
    public const string DefaultVoice = "zh-CN-XiaoxiaoNeural";

    /// <summary>单次合成的整体超时。喊话是实时的，等太久不如早点回落。</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    public EdgeTtsClient(HttpClient http) => _http = http;

    /// <summary>
    /// 合成一段文字，返回 MP3 字节。
    ///
    /// 一次性收完再返回而不是边收边播：一句话的 MP3 通常只有几十 KB，
    /// 攒完再解码能避开流式解码的帧边界问题，代价是首字延迟多了几百毫秒 ——
    /// 而"在线语音"本来就是可选引擎，音质优先。
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(
        string text,
        string? voice = null,
        int ratePercent = 0,
        int volumePercent = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var voiceName = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice;
        var locale = LocaleOf(voiceName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);

        using var socket = new ClientWebSocket();
        Prepare(socket);

        await socket.ConnectAsync(new Uri(BuildUrl()), timeout.Token).ConfigureAwait(false);

        var stamp = Timestamp();
        var requestId = Guid.NewGuid().ToString("N");

        await SendTextAsync(socket, BuildConfigFrame(stamp), timeout.Token).ConfigureAwait(false);
        await SendTextAsync(
            socket,
            BuildSsmlFrame(stamp, requestId, locale, voiceName, text, ratePercent, volumePercent),
            timeout.Token).ConfigureAwait(false);

        return await ReceiveAudioAsync(socket, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取可用音色列表。
    ///
    /// 不写死在代码里：微软会增删音色，写死的话用户看到的列表会慢慢和实际可用对不上，
    /// 而且每个音色都试一遍才知道哪个能用，体验很差。拉不到就返回空，
    /// 由界面提示"改用系统语音"，而不是拿一份可能过期的内置列表去糊弄人。
    /// </summary>
    public async Task<IReadOnlyList<EdgeVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list"
                    + $"?trustedclienttoken={TrustedClientToken}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var voices = new List<EdgeVoiceInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var shortName = item.TryGetProperty("ShortName", out var s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(shortName))
                {
                    continue;
                }

                voices.Add(new EdgeVoiceInfo(
                    shortName,
                    item.TryGetProperty("FriendlyName", out var f) ? f.GetString() ?? shortName : shortName,
                    item.TryGetProperty("Locale", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("Gender", out var g) ? g.GetString() ?? string.Empty : string.Empty));
            }

            return voices;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 拿不到列表不是错误路径：说明这台机器连不上 Edge，
            // 调用方会据此把引擎回落到系统语音。
            return [];
        }
    }

    private static string UserAgent =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + $"Chrome/{ChromiumMajorVersion}.0.0.0 Safari/537.36 Edg/{ChromiumMajorVersion}.0.0.0";

    private static void Prepare(ClientWebSocket socket)
    {
        // 这几个头不是可选的：缺了 Origin 或 User-Agent 会直接被拒
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
    }

    private static string BuildUrl()
        => $"{BaseUrl}/edge/v1"
         + $"?TrustedClientToken={TrustedClientToken}"
         + $"&Sec-MS-GEC={ComputeSecMsGec()}"
         + $"&Sec-MS-GEC-Version=1-{ChromiumFullVersion}"
         + $"&ConnectionId={Guid.NewGuid():N}";

    /// <summary>
    /// 计算 Sec-MS-GEC。
    ///
    /// 步骤（与 edge-tts 的实现一致）：当前 Unix 秒数加 Windows 纪元偏移 →
    /// 按 300 秒向下取整 → 乘 10^7 换成 100 纳秒刻度 → 拼上客户端令牌 → SHA-256 取大写十六进制。
    ///
    /// 先取整再换算，和先换算再取整是等价的（300 秒正好是 3×10^9 个刻度），
    /// 但写成"先按秒取整"更容易和参考实现对得上 —— 这种值一旦算错就是 403，
    /// 而 403 看不出是哪里错了，所以宁可照着写。
    /// </summary>
    private static string ComputeSecMsGec()
    {
        const long winEpochSeconds = 11644473600;
        const long ticksPerSecond = 10_000_000;
        const long windowSeconds = 300;

        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var windowed = unixSeconds - (unixSeconds % windowSeconds);
        var ticks = (windowed + winEpochSeconds) * ticksPerSecond;

        var payload = ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(payload)));
    }

    /// <summary>协议要求的时间戳格式。</summary>
    private static string Timestamp()
        => DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>从音色名推出语言区域：zh-CN-XiaoxiaoNeural → zh-CN。</summary>
    private static string LocaleOf(string voiceName)
    {
        var parts = voiceName.Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "zh-CN";
    }

    private static string BuildConfigFrame(string stamp)
        => $"X-Timestamp:{stamp}\r\n"
         + "Content-Type:application/json; charset=utf-8\r\n"
         + "Path:speech.config\r\n\r\n"
         + $"{{\"context\":{{\"synthesis\":{{\"audio\":{{\"metadataoptions\":"
         + "{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
         + $"\"outputFormat\":\"{OutputFormat}\"}}}}}}}}\r\n";

    private static string BuildSsmlFrame(
        string stamp, string requestId, string locale, string voice, string text,
        int ratePercent, int volumePercent)
    {
        // XML 转义交给框架：文字来自老师输入，里面出现 & 或 < 是完全正常的
        var escaped = System.Security.SecurityElement.Escape(text) ?? string.Empty;
        var rate = SignedPercent(ratePercent);
        var volume = SignedPercent(volumePercent);

        return $"X-RequestId:{requestId}\r\n"
             + "Content-Type:application/ssml+xml\r\n"
             + $"X-Timestamp:{stamp}Z\r\n"
             + "Path:ssml\r\n\r\n"
             + $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{locale}'>"
             + $"<voice name='{voice}'>"
             + $"<prosody pitch='+0Hz' rate='{rate}' volume='{volume}'>{escaped}</prosody>"
             + "</voice></speak>\r\n";
    }

    private static string SignedPercent(int value)
        => value >= 0 ? $"+{value}%" : $"{value}%";

    private static async Task SendTextAsync(ClientWebSocket socket, string payload, CancellationToken cancellationToken)
        => await socket.SendAsync(
            Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<byte[]> ReceiveAudioAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // 文本帧里只有 turn.end 值得关心：它表示这一句读完了
                if (IsTurnEnd(buffer, result.Count))
                {
                    break;
                }

                continue;
            }

            // 二进制帧：前两字节是大端的头部长度，跳过头部才是 MP3 数据
            if (result.Count < 2)
            {
                continue;
            }

            var headerLength = (buffer[0] << 8) | buffer[1];
            var start = 2 + headerLength;

            if (result.Count > start)
            {
                audio.Write(buffer, start, result.Count - start);
            }
        }

        return audio.ToArray();
    }

    private static bool IsTurnEnd(byte[] buffer, int count)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        return text.Contains("Path:turn.end", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        // 每次合成各自建连接、用完即弃：长连接要自己处理保活与重连，
        // 而喊话是低频操作（一节课几十次），每次握手的代价可以忽略。
    }
}