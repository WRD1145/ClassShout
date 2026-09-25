using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 投给 ClassIsland 的这条通知属于哪一种喊话。
///
/// 教室端只负责说清"这是文字还是语音"，标题怎么写由插件决定 ——
/// 展示层的事留在展示层，教室端不必知道那边的遮罩长什么样。
/// </summary>
public enum ShoutNoticeKind
{
    /// <summary>文字喊话，内容就是老师发的字。</summary>
    Text,

    /// <summary>语音喊话，内容只有一句说明（没配语音转文字，或者结果还没出来）。</summary>
    Voice,

    /// <summary>语音喊话的转写结果，内容就是识别出来的字。</summary>
    VoiceTranscript,

    /// <summary>图片喊话，内容是随图的那句说明（没有说明时是一句占位）。</summary>
    Image,
}

/// <summary>
/// 投给 ClassIsland 的那条通知长什么样。
///
/// 单独拆出来是为了能测：文案是给人看的，写错了不会有任何异常，
/// 只会让教室里那块屏幕上出现一句不通顺的话 —— 这种错只能靠断言逮住。
/// </summary>
public static class ClassIslandNotice
{
    /// <summary>
    /// 正文文案。
    ///
    /// 语音喊话本身没有文字，得给一句人看得懂的说明，否则那边会是一条空提醒；
    /// 转写结果出来之后是第二条，说明那句后面接上识别出来的字。
    /// </summary>
    public static string ContentFor(ShoutNoticeKind kind, string text) => kind switch
    {
        ShoutNoticeKind.Voice => "语音消息",
        ShoutNoticeKind.VoiceTranscript => $"语音消息{Environment.NewLine}识别结果：{text}",
        _ => text,
    };

    /// <summary>
    /// 类别在协议里用固定的小写词，不用数字。
    ///
    /// 老插件不认识这个字段时会忽略它（当成文字喊话），而数字一旦在两边排错序，
    /// 表现是"语音喊话显示成文字喊话"这种不报错的错 —— 用词就没有这种风险。
    /// </summary>
    public static string KindText(ShoutNoticeKind kind) => kind switch
    {
        ShoutNoticeKind.Voice => "voice",
        ShoutNoticeKind.VoiceTranscript => "voiceTranscript",
        ShoutNoticeKind.Image => "image",
        _ => "text",
    };
}

/// <summary>
/// 把喊话投递给本机的 ClassIsland 联动插件。
///
/// 为什么是"教室端主动 POST 到本机端口"：ClassIsland 的跨进程通信只能从外部调用它，
/// 而它公开的远程服务里没有"显示一条提醒"（只有课程、档案、Uri 导航），
/// 所以必须由插件自己开一个入口。详见插件仓库。
///
/// 这个类**绝不抛异常、也绝不阻塞喊话主流程**：
/// 它只是"顺便再通知一处"，插件没装、端口没开、ClassIsland 没运行都很正常，
/// 那些情况下喊话本身照常（教室端自己的弹窗还在）。
/// </summary>
public sealed class ClassIslandNotifier : IDisposable
{
    /// <summary>插件的监听地址。只绑回环，所以这里也只连回环。</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:45902/shout";

    /// <summary>
    /// 中文原样发出，不要转义成 \uXXXX。
    ///
    /// 这不仅是为了好看：教室里排障时最常见的动作就是抓一下这个包看看喊话内容对不对，
    /// 而一串 \u5f20\u8001\u5e08 得先解码才能读。这是本机回环投递，不存在注入面。
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;

    /// <summary>连续失败后不再反复写日志。教室里没人看日志，但刷屏会淹没真正的问题。</summary>
    private int _consecutiveFailures;

    /// <param name="endpoint">投递地址。默认就是本机插件那个端口，只有测试才会传别的值。</param>
    public ClassIslandNotifier(string? endpoint = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;

        // 回环地址绝不该走代理，而且这里必须显式关掉。
        //
        // 这不是假想问题：开发机上设着 HTTP_PROXY，.NET 的 HttpClient 默认会读它，
        // 于是"投给本机插件"的请求先发给了代理，再由代理转发回来。同一个包被重新编码成
        // chunked 之后，插件那边按 Content-Length 读体就读成了空 —— 判成 400，
        // 喊话在 ClassIsland 里彻底不出现，而两端都看不出发生了什么。
        // 教室里那台电脑设没设代理不归我们决定，所以这里自己拿主意。
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            // 真正起作用的是下面那个 2 秒的取消令牌；这里给一个更宽松的上限，
            // 免得两层超时互相打架。
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>
    /// 投递一条喊话。<paramref name="from"/> 是老师的姓名，会显示在提醒的遮罩上。
    /// </summary>
    /// <returns>是否投递成功。调用方可以用它决定要不要记日志。</returns>
    public async Task<bool> TryNotifyAsync(
        string from,
        string text,
        ShoutNoticeKind kind = ShoutNoticeKind.Text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 超时压得很短：这是一个本机回环请求，正常情况是毫秒级。
            // 若不设上限，插件卡住就会把喊话这条链路一起拖住 —— 而那正是不能接受的。
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new { from, text, kind = ClassIslandNotice.KindText(kind) }, PayloadOptions);

            // 刻意用 ByteArrayContent 而不是 PostAsJsonAsync：
            // 后者的长度是未知的，HttpClient 会改用 chunked 传输，而插件的桥接服务
            // 是按 Content-Length 读体的。长度已知就不会有这种歧义。
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = content,
            };

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            _consecutiveFailures++;
            return false;
        }
    }

    /// <summary>
    /// 是否该把这次失败写进日志。
    ///
    /// 只在第一次和每 20 次时写：插件没装是最常见的情况，那时每次喊话都记一条
    /// 会把教室端的日志刷满，真正的异常反而被埋掉。
    /// </summary>
    public bool ShouldLogFailure() => _consecutiveFailures == 1 || _consecutiveFailures % 20 == 0;

    public void Dispose() => _http.Dispose();
}
