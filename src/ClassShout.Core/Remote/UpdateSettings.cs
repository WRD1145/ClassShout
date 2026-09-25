namespace ClassShout.Core.Remote;

/// <summary>
/// 一个可选的下载镜像。
///
/// 为什么要预置几个：这类镜像的可用性一直在变（今天能用的明天可能就没了），
/// 让老师自己去查"该填哪个网址"是不现实的。预置几个 + 允许自己填，
/// 镜像全挂了也还有"直连 GitHub"这一条。
/// </summary>
/// <param name="Label">界面上显示的名字。</param>
/// <param name="ApiBase">GitHub API 的基址（镜像自己提供 API 时填它的）。</param>
/// <param name="DownloadTemplate">下载地址模板，见 <see cref="UpdateSettings.DownloadTemplate"/>。</param>
public readonly record struct UpdateMirror(string Label, string ApiBase, string DownloadTemplate);

/// <summary>
/// 检查更新这一块的设置。
///
/// 关于隐私有一条刻意为之的规矩：**只在你按下「检查更新」时才发请求**。
/// 后台定时去问"有没有新版本"，等于每次开应用都向外面报一次到 ——
/// 而对一台放在教室里的机器来说，这件事没有任何必要。
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>默认仓库。自建或用自己的 fork 时改这里。</summary>
    public const string DefaultRepository = "WRD1145/ClassShout";

    public const string DefaultApiBase = "https://api.github.com";

    /// <summary>取哪个仓库的发行版。</summary>
    public string Repository { get; set; } = DefaultRepository;

    /// <summary>GitHub API 的基址。镜像自带 API 时填镜像的（例如 kkgithub）。</summary>
    public string ApiBase { get; set; } = DefaultApiBase;

    /// <summary>
    /// 下载地址模板。留空＝直接用 GitHub 给的原地址。
    ///
    /// 支持两个占位符，覆盖了常见镜像的两种做法：
    ///   · <c>{url}</c>  —— 整个原始地址，用于"前缀式"镜像，如 <c>https://ghproxy.net/{url}</c>；
    ///   · <c>{path}</c> —— 去掉 <c>https://github.com/</c> 之后的部分，
    ///     用于"换域名式"镜像，如 <c>https://kkgithub.com/{path}</c>。
    ///
    /// 做成模板而不是"填个前缀"：真实世界里的镜像这两种都有，
    /// 而多一种占位符的成本只是多一行代码。
    /// </summary>
    public string DownloadTemplate { get; set; } = string.Empty;

    /// <summary>最近一次检查的时间，界面上显示"上次检查"用。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>最近一次查到的最新版本号（不含 v）。</summary>
    public string? LatestVersion { get; set; }

    /// <summary>用户选择跳过的那一版：不再提示它，但更新的一版照样提示。</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>预置镜像。第一项是直连。</summary>
    public static IReadOnlyList<UpdateMirror> Presets { get; } =
    [
        new("直连 GitHub", DefaultApiBase, string.Empty),
        new("ghproxy.net", DefaultApiBase, "https://ghproxy.net/{url}"),
        new("gh-proxy.com", DefaultApiBase, "https://gh-proxy.com/{url}"),
        new("ghfast.top", DefaultApiBase, "https://ghfast.top/{url}"),
        new("kkgithub", "https://api.kkgithub.com", "https://kkgithub.com/{path}"),
    ];

    /// <summary>收拾手改坏的值。</summary>
    public UpdateSettings Normalized() => new()
    {
        Repository = NormalizeRepository(Repository),
        ApiBase = string.IsNullOrWhiteSpace(ApiBase) ? DefaultApiBase : ApiBase.Trim().TrimEnd('/'),
        DownloadTemplate = (DownloadTemplate ?? string.Empty).Trim(),
        LastCheckedAt = LastCheckedAt,
        LatestVersion = string.IsNullOrWhiteSpace(LatestVersion) ? null : LatestVersion.Trim().TrimStart('v', 'V'),
        SkippedVersion = string.IsNullOrWhiteSpace(SkippedVersion) ? null : SkippedVersion.Trim().TrimStart('v', 'V'),
    };

    /// <summary>
    /// 把"owner/repo"或一条完整的 GitHub 地址都收拾成 owner/repo。
    ///
    /// 之所以容错：让填这一栏的人去记"只能填 owner/repo"是没必要的，
    /// 而粘一整条地址进来是最自然的做法。
    /// </summary>
    public static string NormalizeRepository(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return DefaultRepository;
        }

        const string prefix = "https://github.com/";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        text = text.TrimEnd('/');

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        // 只保留 owner/repo 两段，多余的（例如 /releases）丢掉
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : text;
    }

    /// <summary>
    /// 按模板算出真正的下载地址。模板为空时原样返回。
    /// </summary>
    public static string ApplyTemplate(string? template, string url)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return url;
        }

        const string githubPrefix = "https://github.com/";
        var path = url.StartsWith(githubPrefix, StringComparison.OrdinalIgnoreCase)
            ? url[githubPrefix.Length..]
            : url;

        return template.Trim().Replace("{url}", url).Replace("{path}", path);
    }
}
