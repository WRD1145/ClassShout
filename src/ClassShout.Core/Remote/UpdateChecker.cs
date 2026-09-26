using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.Core.Remote;

/// <summary>发行版里的一个附件。</summary>
/// <param name="Name">文件名（就是发布时定的那个名字）。</param>
/// <param name="Size">字节数。</param>
/// <param name="Url">下载地址（已经过镜像模板换算）。</param>
public sealed record UpdateAsset(string Name, long Size, string Url);

/// <summary>一次检查更新的结果。</summary>
/// <param name="Ok">这次检查本身是否成功（网络、解析）；不代表"有没有新版本"。</param>
/// <param name="Error">失败原因，可直接显示给用户。</param>
/// <param name="LatestVersion">查到的最新版本号（不含 v）。</param>
/// <param name="HasUpdate">是否有比当前更新的版本。</param>
/// <param name="Notes">发行说明（Markdown 原文）。</param>
/// <param name="PageUrl">发行版页面地址（给人看的）。</param>
/// <param name="Assets">附件列表。</param>
/// <param name="MirrorLabel">这次走的是哪个镜像（出问题时能说清）。</param>
/// <param name="ProxyDescription">这次实际走的代理（"系统代理 → …"或"直连"）。</param>
/// <param name="LatencyMs">耗时（毫秒）。</param>
public sealed record UpdateCheckResult(
    bool Ok,
    string? Error = null,
    string? LatestVersion = null,
    bool HasUpdate = false,
    string? Notes = null,
    string? PageUrl = null,
    IReadOnlyList<UpdateAsset>? Assets = null,
    string? MirrorLabel = null,
    string? ProxyDescription = null,
    int LatencyMs = 0)
{
    /// <summary>按文件名找一个附件（精确匹配，找不到就返回 null）。</summary>
    public UpdateAsset? FindAsset(string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? null
            : Assets?.FirstOrDefault(asset => string.Equals(asset.Name, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>有没有名字以某个后缀结尾的附件（例如 .apk、.zip）。</summary>
    public UpdateAsset? FindAssetEndingWith(string suffix)
        => Assets?.FirstOrDefault(asset => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一次「这个镜像通不通」的检测结果。</summary>
/// <param name="MirrorId">镜像 Id。</param>
/// <param name="Label">镜像名，用于显示。</param>
/// <param name="Ok">通不通。</param>
/// <param name="LatencyMs">耗时。</param>
/// <param name="LatestVersion">查到的最新版本号。</param>
/// <param name="Error">失败原因。</param>
public sealed record MirrorTestResult(
    string MirrorId,
    string Label,
    bool Ok,
    int LatencyMs,
    string? LatestVersion = null,
    string? Error = null);

/// <summary>
/// 检查有没有新版本。
///
/// 三件事值得说明：
///
/// · **只比对发行版标签，不去猜代码**。客户端拿到的就是发行版，
///   所以"有没有新版本"以站点上的 release tag 为准，与安装包一一对应。
///
/// · **镜像可以有很多条，一条一条测**。镜像的可用性一直在变，
///   所以"全部并发测一遍、挑最快的那个"是常用动作（见 <see cref="TestAllAsync"/>）。
///
/// · **走不走代理是明确的**。教室里/办公室里常常挂着代理，而 .NET 默认那套
///   在有些环境里读不到系统设置 —— 表现就是"浏览器能开 GitHub、应用却连不上"。
///   这里每次都用当次设置现算一遍代理，并把结果带回去显示。
/// </summary>
public sealed class UpdateChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly UpdateSettings _settings;

    /// <summary>自检用的处理器工厂；为空时按代理设置自己建（正常运行就是这条）。</summary>
    private readonly Func<ProxyResolution, HttpMessageHandler>? _handlerFactory;

    private readonly Func<UpdateProxyMode, string?, ProxyResolution> _proxyResolver;

    /// <param name="settings">当前设置。</param>
    /// <param name="handlerFactory">
    /// 只给自检用：按代理解析结果造一个处理器。为 null 时用真实网络栈。
    /// </param>
    /// <param name="proxyResolver">只给自检用：替换代理解析（真实环境读系统设置）。</param>
    public UpdateChecker(
        UpdateSettings settings,
        Func<ProxyResolution, HttpMessageHandler>? handlerFactory = null,
        Func<UpdateProxyMode, string?, ProxyResolution>? proxyResolver = null)
    {
        _settings = settings.Normalized();
        _handlerFactory = handlerFactory;
        _proxyResolver = proxyResolver ?? ((mode, url) => UpdateProxy.Resolve(mode, url));
    }

    /// <summary>当前设置下实际会走的代理（界面显示用）。</summary>
    public ProxyResolution CurrentProxy => _proxyResolver(_settings.ProxyMode, _settings.ProxyUrl);

    /// <summary>
    /// 用当前选中的镜像，问一次"最新的是哪一版"。
    /// </summary>
    /// <param name="currentVersion">当前版本号，例如 1.9.0。由调用方给 —— Core 不该去读哪个程序集的版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        => CheckViaAsync(_settings.SelectedMirror, currentVersion, cancellationToken);

    /// <summary>用指定的镜像问一次。</summary>
    public async Task<UpdateCheckResult> CheckViaAsync(
        UpdateMirror mirror,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var proxy = _proxyResolver(_settings.ProxyMode, _settings.ProxyUrl);
        var url = mirror.LatestReleaseUrl(_settings.Repository);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var http = BuildClient(proxy);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "ClassShout-Updater");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail(
                    $"这个镜像上没有 {mirror.EffectiveRepository(_settings.Repository)} 的发行版。",
                    mirror,
                    proxy,
                    stopwatch);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                return Fail("被拒绝了（多半是访问频率限制）。换一个镜像或过一会儿再试。", mirror, proxy, stopwatch);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"服务器返回 HTTP {(int)response.StatusCode}。", mirror, proxy, stopwatch);
            }

            var release = await response.Content
                .ReadFromJsonAsync<ReleasePayload>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return Fail("返回的内容看不懂（可能被网络中间层改写过）。", mirror, proxy, stopwatch);
            }

            var latest = Normalize(release.TagName);

            var assets = (release.Assets ?? [])
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                .Select(asset => new UpdateAsset(
                    asset.Name!,
                    asset.Size,
                    UpdateSettings.ApplyTemplate(mirror.DownloadTemplate, asset.BrowserDownloadUrl!)))
                .ToList();

            return new UpdateCheckResult(
                true,
                null,
                latest,
                IsNewer(latest, currentVersion),
                release.Body ?? string.Empty,
                release.HtmlUrl,
                assets,
                mirror.Label,
                proxy.Description,
                (int)stopwatch.ElapsedMilliseconds);
        }
        catch (JsonException)
        {
            stopwatch.Stop();

            // 与"连不上"分开说：校园网里被中间层塞一个登录页进来是很常见的事，
            // 把 JsonReaderException 的原文甩给老师看毫无帮助。
            return Fail("返回的内容看不懂（可能被网络中间层改写过）。", mirror, proxy, stopwatch);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            stopwatch.Stop();

            return Fail(
                $"连不上：{ex.Message}" + (proxy.UseProxy ? string.Empty : "（当前没有走代理，可以在下面给「代理」设一个）"),
                mirror,
                proxy,
                stopwatch);
        }
    }

    /// <summary>把一个镜像测一遍：能不能查到发行版、要多久。</summary>
    public async Task<MirrorTestResult> TestAsync(UpdateMirror mirror, CancellationToken cancellationToken = default)
    {
        var result = await CheckViaAsync(mirror, "0.0.0", cancellationToken).ConfigureAwait(false);

        return new MirrorTestResult(
            mirror.Id,
            mirror.Label,
            result.Ok,
            result.LatencyMs,
            result.LatestVersion,
            result.Error);
    }

    /// <summary>
    /// **同时**测所有镜像，返回逐个结果。
    ///
    /// 并发而不是一条一条来：这些请求互不相干，串行测六条要等六次超时
    /// （每条 10 秒的话就是 60 秒），而用户按下按钮时想立刻知道"哪个能用"。
    /// </summary>
    public async Task<IReadOnlyList<MirrorTestResult>> TestAllAsync(
        IEnumerable<UpdateMirror> mirrors,
        CancellationToken cancellationToken = default)
    {
        var list = mirrors.ToList();
        var tasks = list.Select(mirror => TestAsync(mirror, cancellationToken)).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static UpdateCheckResult Fail(string error, UpdateMirror mirror, ProxyResolution proxy, Stopwatch stopwatch)
        => new(false, error, null, false, null, null, null, mirror.Label, proxy.Description, (int)stopwatch.ElapsedMilliseconds);

    private HttpClient BuildClient(ProxyResolution proxy)
    {
        var handler = _handlerFactory is not null
            ? _handlerFactory(proxy)
            : new SocketsHttpHandler
            {
                // 明确按解析结果来：UseProxy=false 就是直连，否则用解析出来的代理
                UseProxy = proxy.UseProxy,
                Proxy = proxy.UseProxy ? proxy.Proxy : null,

                // 教室里的网络可能很慢，但也不该无限等
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>版本号去掉前缀 v。</summary>
    public static string Normalize(string? tag)
        => (tag ?? string.Empty).Trim().TrimStart('v', 'V');

    /// <summary>
    /// latest 是否比 current 新。
    ///
    /// 按段比数字，而不是按字符串 —— "1.10.0" 与 "1.9.0" 按字符串比会得出相反的结论，
    /// 而这种错法要到第 10 个次版本才会显形。带预发布后缀的（1.9.0-rc1）算比同号正式版旧。
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        var left = Parse(Normalize(latest));
        var right = Parse(Normalize(current));

        for (var i = 0; i < Math.Max(left.Numbers.Length, right.Numbers.Length); i++)
        {
            var a = i < left.Numbers.Length ? left.Numbers[i] : 0;
            var b = i < right.Numbers.Length ? right.Numbers[i] : 0;

            if (a != b)
            {
                return a > b;
            }
        }

        // 数字部分一样时：正式的比预发布的新
        return right.IsPrerelease && !left.IsPrerelease;
    }

    private static (int[] Numbers, bool IsPrerelease) Parse(string version)
    {
        var isPrerelease = version.Contains('-', StringComparison.Ordinal);
        var core = isPrerelease ? version.Split('-')[0] : version;

        var numbers = core
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();

        return (numbers.Length == 0 ? [0] : numbers, isPrerelease);
    }

    /// <summary>
    /// 发行版接口里我们用得上的那部分。
    ///
    /// GitHub 与 Gitee 的字段名一致（tag_name / body / html_url / assets[]），
    /// 所以一套解析够用；两站的差别只在**接口地址**与下载地址的域名上。
    /// </summary>
    private sealed record ReleasePayload(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] ReleaseAsset[]? Assets);

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
