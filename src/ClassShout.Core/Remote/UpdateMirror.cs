using System.Net;

namespace ClassShout.Core.Remote;

/// <summary>镜像站用的是哪一套发行版接口。</summary>
public enum UpdateMirrorProvider
{
    /// <summary>GitHub 及其兼容代理（kkgithub 这类）。</summary>
    GitHub = 0,

    /// <summary>Gitee（码云）。接口路径与字段和 GitHub 相近但不是同一个站。</summary>
    Gitee = 1,
}

/// <summary>
/// 一个更新镜像源。
///
/// 为什么是"一条一条列出来"而不是"填一个模板"：镜像的可用性一直变
/// （今天能用的明天可能就没了），能同时存好几个、一键全部测一遍、
/// 挑最快的那个用，才是真实用法。只留一个的话，换源就得重新查资料。
/// </summary>
public sealed class UpdateMirror
{
    /// <summary>短标识，用于界面上的操作与去重。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>界面上显示的名字，例如「ghproxy.net」。</summary>
    public string Label { get; set; } = "自定义镜像";

    /// <summary>用哪套接口。</summary>
    public UpdateMirrorProvider Provider { get; set; } = UpdateMirrorProvider.GitHub;

    /// <summary>API 基址。留空用该 provider 的默认值。</summary>
    public string ApiBase { get; set; } = string.Empty;

    /// <summary>
    /// 这个镜像上的仓库（owner/repo）。留空表示与 <see cref="UpdateSettings.Repository"/> 相同。
    ///
    /// 需要它是因为 Gitee 上的镜像仓库与 GitHub 不是同一个名字
    /// （GitHub 是 WRD1145/ClassShout，码云上那份是 li-hansen136/ClassShout）。
    /// </summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// 下载地址模板。留空＝用该站给的原地址。
    /// 支持 <c>{url}</c>（整段原始地址）与 <c>{path}</c>（去掉 github.com/ 之后的部分）。
    /// </summary>
    public string DownloadTemplate { get; set; } = string.Empty;

    /// <summary>内置的镜像（可以改，但不能删 —— 删光了就没有可用的兜底）。</summary>
    public bool BuiltIn { get; set; }

    // —— 最近一次检测的结果 ——

    /// <summary>上次检测是否通。没测过是 null。</summary>
    public bool? LastOk { get; set; }

    /// <summary>上次检测的耗时（毫秒）。</summary>
    public int LastLatencyMs { get; set; }

    /// <summary>上次检测查到的最新版本号。</summary>
    public string? LastVersion { get; set; }

    /// <summary>上次检测的失败原因。</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastTestedAt { get; set; }

    /// <summary>该 provider 的默认 API 基址。</summary>
    public static string DefaultApiBase(UpdateMirrorProvider provider)
        => provider == UpdateMirrorProvider.Gitee ? "https://gitee.com/api/v5" : "https://api.github.com";

    /// <summary>实际使用的 API 基址（去掉末尾斜杠）。</summary>
    public string EffectiveApiBase => string.IsNullOrWhiteSpace(ApiBase)
        ? DefaultApiBase(Provider)
        : ApiBase.Trim().TrimEnd('/');

    /// <summary>实际使用的仓库。</summary>
    public string EffectiveRepository(string fallback)
        => string.IsNullOrWhiteSpace(Repository) ? fallback : UpdateSettings.NormalizeRepository(Repository);

    /// <summary>查最新发行版的完整地址。</summary>
    public string LatestReleaseUrl(string fallbackRepository)
        => $"{EffectiveApiBase}/repos/{EffectiveRepository(fallbackRepository)}/releases/latest";

    /// <summary>界面上一行说明。</summary>
    public string ProviderLabel => Provider == UpdateMirrorProvider.Gitee ? "Gitee" : "GitHub";

    /// <summary>检测结果那一行的文字。</summary>
    public string ResultText => LastTestedAt is null
        ? "还没测过"
        : LastOk == true
            ? $"通 · {LastLatencyMs} ms" + (string.IsNullOrWhiteSpace(LastVersion) ? string.Empty : $" · 最新 v{LastVersion}")
            : $"不通 · {LastError ?? "未知原因"}";

    /// <summary>复制一份（界面上改设置时不希望直接动到原对象）。</summary>
    public UpdateMirror Clone() => new()
    {
        Id = Id,
        Label = Label,
        Provider = Provider,
        ApiBase = ApiBase,
        Repository = Repository,
        DownloadTemplate = DownloadTemplate,
        BuiltIn = BuiltIn,
        LastOk = LastOk,
        LastLatencyMs = LastLatencyMs,
        LastVersion = LastVersion,
        LastError = LastError,
        LastTestedAt = LastTestedAt,
    };

    /// <summary>收拾手改坏的值。</summary>
    public UpdateMirror Normalized() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N")[..8] : Id.Trim(),
        Label = string.IsNullOrWhiteSpace(Label) ? "未命名镜像" : Label.Trim(),
        Provider = Provider,
        ApiBase = (ApiBase ?? string.Empty).Trim().TrimEnd('/'),
        Repository = (Repository ?? string.Empty).Trim(),
        DownloadTemplate = (DownloadTemplate ?? string.Empty).Trim(),
        BuiltIn = BuiltIn,
        LastOk = LastOk,
        LastLatencyMs = LastLatencyMs,
        LastVersion = LastVersion,
        LastError = LastError,
        LastTestedAt = LastTestedAt,
    };

    /// <summary>内置镜像：直连 GitHub、几个常用加速代理，以及码云上那份镜像仓库。</summary>
    public static List<UpdateMirror> BuiltIns() =>
    [
        new()
        {
            Id = "github",
            Label = "直连 GitHub",
            Provider = UpdateMirrorProvider.GitHub,
            BuiltIn = true,
        },
        new()
        {
            Id = "gitee",
            Label = "Gitee 码云（li-hansen136）",
            Provider = UpdateMirrorProvider.Gitee,
            Repository = "li-hansen136/ClassShout",
            BuiltIn = true,
        },
        new()
        {
            Id = "ghproxy",
            Label = "ghproxy.net",
            Provider = UpdateMirrorProvider.GitHub,
            DownloadTemplate = "https://ghproxy.net/{url}",
            BuiltIn = true,
        },
        new()
        {
            Id = "ghproxy-com",
            Label = "gh-proxy.com",
            Provider = UpdateMirrorProvider.GitHub,
            DownloadTemplate = "https://gh-proxy.com/{url}",
            BuiltIn = true,
        },
        new()
        {
            Id = "ghfast",
            Label = "ghfast.top",
            Provider = UpdateMirrorProvider.GitHub,
            DownloadTemplate = "https://ghfast.top/{url}",
            BuiltIn = true,
        },
        new()
        {
            Id = "kkgithub",
            Label = "kkgithub",
            Provider = UpdateMirrorProvider.GitHub,
            ApiBase = "https://api.kkgithub.com",
            DownloadTemplate = "https://kkgithub.com/{path}",
            BuiltIn = true,
        },
    ];
}

/// <summary>更新检查走不走代理。</summary>
public enum UpdateProxyMode
{
    /// <summary>跟随系统（Windows 上读系统代理设置，其它平台读环境变量）。</summary>
    System = 0,

    /// <summary>不使用代理。</summary>
    None = 1,

    /// <summary>用一个自己填的代理地址。</summary>
    Custom = 2,
}

/// <summary>
/// 代理解析的结果。
/// </summary>
/// <param name="UseProxy">是否使用代理。</param>
/// <param name="Proxy">要用的代理实现。</param>
/// <param name="Description">给人看的一句话，例如「系统代理 → http://127.0.0.1:7890」。</param>
/// <param name="Address">解析出来的代理地址（没有则为空）。</param>
public readonly record struct ProxyResolution(bool UseProxy, IWebProxy? Proxy, string Description, string Address);

/// <summary>
/// 「这次请求走不走代理」的解析。
///
/// 为什么需要它：教室里/办公室里常常挂着代理（而且要挂才能出去），
/// 而 .NET 的 HttpClient 默认走系统代理解析 —— 在**某些环境里并不生效**
/// （代理软件只设了环境变量、或者进程从桌面启动时没继承到那些变量）。
/// 表现就是"浏览器能开 GitHub、应用却连不上"，而且没有任何提示。
///
/// 所以这里把三件事说清楚：跟随系统、不用、自己填；并且把**实际解析出来的地址**
/// 显示在界面上 —— 出问题时一眼能看出"系统代理没读到"还是"地址填错了"。
/// </summary>
public static class UpdateProxy
{
    /// <summary>
    /// 解析当前该用哪个代理。
    /// </summary>
    /// <param name="mode">用户选的模式。</param>
    /// <param name="customUrl">自定义代理地址，例如 http://127.0.0.1:7890。</param>
    /// <param name="probeUrl">用来问"系统代理会把它指到哪"，随便一个 https 地址即可。</param>
    public static ProxyResolution Resolve(UpdateProxyMode mode, string? customUrl, string probeUrl = "https://api.github.com")
    {
        if (mode == UpdateProxyMode.None)
        {
            return new ProxyResolution(false, null, "不使用代理（直连）", string.Empty);
        }

        if (mode == UpdateProxyMode.Custom)
        {
            var address = (customUrl ?? string.Empty).Trim();

            if (address.Length == 0)
            {
                return new ProxyResolution(false, null, "自定义代理：还没填地址，按直连处理", string.Empty);
            }

            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                return new ProxyResolution(false, null, $"自定义代理地址看不懂：{address}", string.Empty);
            }

            return new ProxyResolution(true, new WebProxy(uri), $"自定义代理 → {uri}", uri.ToString());
        }

        // 跟随系统。GetSystemWebProxy 在 Windows 上读系统（WinINET）设置，
        // 在 Linux/macOS 上读 http_proxy / https_proxy / all_proxy 环境变量。
        try
        {
            var system = WebRequest.GetSystemWebProxy();
            var probe = new Uri(probeUrl);
            var resolved = system.GetProxy(probe);

            // 解析结果与目标地址相同时，表示"这个地址不走代理"
            if (resolved is null || resolved == probe)
            {
                return new ProxyResolution(false, null, "系统没有配置代理（直连）", string.Empty);
            }

            return new ProxyResolution(true, system, $"系统代理 → {resolved}", resolved.ToString());
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or UriFormatException)
        {
            return new ProxyResolution(false, null, $"读系统代理失败（{ex.Message}），按直连处理", string.Empty);
        }
    }
}
