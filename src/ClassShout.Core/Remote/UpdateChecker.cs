using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.Core.Remote;

/// <summary>发行版里的一个附件。</summary>
/// <param name="Name">文件名（就是发布时定的那个名字）。</param>
/// <param name="Size">字节数。</param>
/// <param name="Url">GitHub 上的原始下载地址（已经过镜像模板换算）。</param>
public sealed record UpdateAsset(string Name, long Size, string Url);

/// <summary>一次检查更新的结果。</summary>
/// <param name="Ok">这次检查本身是否成功（网络、解析）；不代表"有没有新版本"。</param>
/// <param name="Error">失败原因，可直接显示给用户。</param>
/// <param name="LatestVersion">查到的最新版本号（不含 v）。</param>
/// <param name="HasUpdate">是否有比当前更新的版本。</param>
/// <param name="Notes">发行说明（Markdown 原文）。</param>
/// <param name="PageUrl">发行版页面地址（给人看的）。</param>
/// <param name="Assets">附件列表。</param>
public sealed record UpdateCheckResult(
    bool Ok,
    string? Error = null,
    string? LatestVersion = null,
    bool HasUpdate = false,
    string? Notes = null,
    string? PageUrl = null,
    IReadOnlyList<UpdateAsset>? Assets = null)
{
    /// <summary>按文件名找一个附件（精确匹配，找不到就返回 null）。</summary>
    public UpdateAsset? FindAsset(string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? null
            : Assets?.FirstOrDefault(asset => string.Equals(asset.Name, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>有没有名字以某个后缀结尾的附件（例如 .apk）。</summary>
    public UpdateAsset? FindAssetEndingWith(string suffix)
        => Assets?.FirstOrDefault(asset => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 检查有没有新版本。
///
/// 三件事值得说明：
///
/// · **只比对发行版标签，不去猜代码**。客户端拿到的就是发行版，
///   所以"有没有新版本"以 GitHub 上的 release tag 为准，与安装包一一对应。
///
/// · **镜像只影响下载地址**，不影响"有没有新版本"这个判断。
///   API 走镜像（kkgithub 那类）时才算镜像，否则 API 直连、下载走镜像 ——
///   这正是那些"加速下载"的代理镜像的工作方式。
///
/// · **失败要说人话**。教室里那台机器可能是内网、可能装了拦截，
///   "连不上"和"这个仓库没有发行版"对使用者是两件事。
/// </summary>
public sealed class UpdateChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly UpdateSettings _settings;

    public UpdateChecker(HttpClient http, UpdateSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>
    /// 问一次"最新的是哪一版"。
    /// </summary>
    /// <param name="currentVersion">当前版本号，例如 1.8.0。由调用方给 —— Core 不该去读哪个程序集的版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Normalized();
        var url = $"{settings.ApiBase}/repos/{settings.Repository}/releases/latest";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // GitHub 要求带 User-Agent，缺了会直接 403
            request.Headers.TryAddWithoutValidation("User-Agent", "ClassShout-Updater");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false,
                    $"没找到 {settings.Repository} 的发行版。检查仓库名是否写对（私有仓库读不到）。");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                return new UpdateCheckResult(false,
                    "GitHub 拒绝了这次请求（多半是访问频率限制）。换个镜像源或过一会儿再试。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, $"服务器返回 HTTP {(int)response.StatusCode}。");
            }

            var release = await response.Content
                .ReadFromJsonAsync<GitHubRelease>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(false, "返回的内容看不懂（可能被网络中间层改写过）。");
            }

            var latest = Normalize(release.TagName);
            var notes = release.Body ?? string.Empty;

            var assets = (release.Assets ?? [])
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                .Select(asset => new UpdateAsset(
                    asset.Name!,
                    asset.Size,
                    UpdateSettings.ApplyTemplate(settings.DownloadTemplate, asset.BrowserDownloadUrl!)))
                .ToList();

            return new UpdateCheckResult(
                true,
                null,
                latest,
                IsNewer(latest, currentVersion),
                notes,
                release.HtmlUrl,
                assets);
        }
        catch (JsonException)
        {
            // 与"连不上"分开说：校园网里被中间层塞一个登录页进来是很常见的事，
            // 把 JsonReaderException 的原文甩给老师看毫无帮助。
            return new UpdateCheckResult(false,
                "更新服务器返回的内容看不懂（可能被网络中间层改写过）。换个镜像源再试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateCheckResult(false,
                $"连不上更新服务器：{ex.Message}。校园网里可以换一个镜像源再试。");
        }
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

    /// <summary>GitHub 发行版接口里我们用得上的那部分。</summary>
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
