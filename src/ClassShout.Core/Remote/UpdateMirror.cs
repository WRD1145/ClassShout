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
/// 为什么需要它，以及为什么不能只靠 .NET 自带的解析：
///
/// 课堂上/办公室里常常挂着代理（而且要挂才能出去 GitHub），而 .NET 的
/// <c>HttpClient.DefaultProxy</c> 在装了代理的机器上**未必走代理** ——
/// 它在存在代理环境变量时会选那个"只看环境变量"的实现
/// （<c>HttpEnvironmentProxy</c>），而它按 URL 的 scheme 取变量：
/// https 请求只看 <c>HTTPS_PROXY</c>。代理软件往往只设了 <c>HTTP_PROXY</c>，
/// 于是 https 请求一个都不走代理 —— 表现就是"浏览器能开 GitHub、ClassShout 连不上"，
/// 而且没有任何提示。Windows 的"系统代理"（注册表里那份）它同样不读。
///
/// 所以这里按可靠程度依次来：Windows 注册表 → 环境变量 → .NET 自带解析（保底），
/// 并把**实际解析出来的地址**显示在界面上，让"到底走没走代理"一眼可见。
/// </summary>
public static class UpdateProxy
{
    /// <summary>
    /// 解析当前该用哪个代理。
    /// </summary>
    /// <param name="mode">用户选的模式。</param>
    /// <param name="customUrl">自定义代理地址，例如 http://127.0.0.1:7890。</param>
    /// <param name="probeUrl">用来问"系统代理会把它指到哪"，随便一个 https 地址即可。</param>
    /// <param name="windowsProxyReader">只给自检用：替换"读 Windows 系统代理"这一步。</param>
    public static ProxyResolution Resolve(
        UpdateProxyMode mode,
        string? customUrl,
        string probeUrl = "https://api.github.com",
        Func<(bool Enabled, string? Server, string? Bypass)>? windowsProxyReader = null)
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

            if (!LooksLikeProxyAddress(address) ||
                !Uri.TryCreate(NormalizeProxyAddress(address), UriKind.Absolute, out var uri))
            {
                return new ProxyResolution(false, null, $"自定义代理地址看不懂：{address}", string.Empty);
            }

            return new ProxyResolution(true, new WebProxy(uri), $"自定义代理 → {uri}", uri.ToString());
        }

        // —— 跟随系统 ——

        // 1) Windows：读系统代理设置。代理软件（Clash / v2ray 等）的"系统代理"开关
        //    写的就是这里，而 .NET 自带的解析根本不读它。
        //    自检会注入一份假的读取器，所以这里走的是委托而不是直接调用。
        if (OperatingSystem.IsWindows())
        {
            var readWindows = windowsProxyReader ?? ReadWindowsSystemProxy;

#pragma warning disable CA1416 // 上面那行已经挡过平台；分析器看不出一份委托背后是不是 Windows 专有实现
            var (enabled, server, bypass) = readWindows();
#pragma warning restore CA1416

            if (enabled && ParseWindowsProxyServer(server, probeUrl) is { Length: > 0 } windowsAddress &&
                Uri.TryCreate(windowsAddress, UriKind.Absolute, out var windowsUri))
            {
                var proxy = new WebProxy(windowsUri) { BypassList = BuildBypassList(bypass) };
                return new ProxyResolution(true, proxy, $"系统代理 → {windowsUri}", windowsUri.ToString());
            }
        }

        // 2) 环境变量。Linux / macOS 上主要靠它；Windows 上代理软件也可能只设了这些。
        if (ResolveFromEnvironment(probeUrl) is { Length: > 0 } envAddress &&
            Uri.TryCreate(envAddress, UriKind.Absolute, out var envUri))
        {
            return new ProxyResolution(true, new WebProxy(envUri), $"环境变量代理 → {envUri}", envUri.ToString());
        }

        // 3) .NET 自带的解析（保底；它可能什么都不返回）
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

    /// <summary>
    /// 把系统代理设置里的那一串解析成当前这个地址该用的代理。
    ///
    /// 它可能是：
    ///   · 单个地址 <c>127.0.0.1:7890</c>（最常见）；
    ///   · 按协议分开写 <c>http=1.2.3.4:8080;https=5.6.7.8:9090</c>；
    ///   · 带 socks 前缀 <c>socks=127.0.0.1:1080</c>。
    /// 只取当前 probe 协议对应的那一段；都没有就返回 null（表示"别用代理"）。
    /// </summary>
    /// <param name="raw">系统设置里的 ProxyServer 字符串。</param>
    /// <param name="probeUrl">要访问的地址（用来决定取哪一段）。</param>
    public static string? ParseWindowsProxyServer(string? raw, string probeUrl)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var isHttps = probeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!text.Contains('=', StringComparison.Ordinal))
        {
            // 单个地址：补上 scheme（注册表里存的通常就是 host:port）
            return NormalizeProxyAddress(text);
        }

        string? https = null;
        string? http = null;
        string? socks = null;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.Split('=', 2);

            if (split.Length != 2)
            {
                continue;
            }

            var scheme = split[0].Trim().ToLowerInvariant();
            var value = split[1].Trim();

            switch (scheme)
            {
                case "https":
                    https = value;
                    break;

                case "http":
                    http = value;
                    break;

                case "socks":
                case "socks5":
                    socks = value;
                    break;
            }
        }

        if (isHttps && https is { Length: > 0 })
        {
            return NormalizeProxyAddress(https);
        }

        if (!isHttps && http is { Length: > 0 })
        {
            return NormalizeProxyAddress(http);
        }

        // https 没单独配时用 http 那一份（http 代理同样能转发 https 请求，靠 CONNECT）
        if (isHttps && http is { Length: > 0 })
        {
            return NormalizeProxyAddress(http);
        }

        // 只剩 socks 也认：.NET 6 起 SocketsHttpHandler 支持 socks5
        return socks is { Length: > 0 } ? NormalizeProxyAddress(socks, "socks5") : null;
    }

    /// <summary>
    /// 这一串看起来像不像一个代理地址。
    ///
    /// 为什么光靠 Uri.TryCreate 不够：`http://这不是地址` 在 Uri 眼里"语法合法"
    /// （它只是把主机名当成一个奇怪的名字），于是用户把"这不是地址"填进去时，
    /// 程序会一本正经地拿它当代理用，然后所有请求都失败在一个没人看得懂的地方。
    /// </summary>
    private static bool LooksLikeProxyAddress(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out _);
        }

        // host[:port]：主机只允许字母数字、点、横线、下划线（IPv4、域名、localhost 都覆盖）
        var parts = text.Split(':', 2);
        var host = parts[0];

        if (host.Length == 0 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return false;
        }

        return parts.Length == 1 || (int.TryParse(parts[1], out var port) && port is > 0 and <= 65535);
    }

    /// <summary>给一个没写 scheme 的 host:port 补上默认的 scheme。</summary>
    private static string NormalizeProxyAddress(string address, string fallbackScheme = "http")
    {
        var text = address.Trim();

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }

        return $"{fallbackScheme}://{text}";
    }

    /// <summary>把系统的"例外列表"变成 WebProxy 认的正则列表。</summary>
    private static string[] BuildBypassList(string? bypassRaw)
    {
        if (string.IsNullOrWhiteSpace(bypassRaw))
        {
            return [];
        }

        var patterns = new List<string>();

        foreach (var item in bypassRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // <local> 是 IE 的"本地地址不走代理"，在 WebProxy 里没有对应写法，跳过
            if (item.StartsWith('<'))
            {
                continue;
            }

            // 通配符换成正则：127.* → 127\..*，localhost → 原样
            var pattern = System.Text.RegularExpressions.Regex.Escape(item)
                .Replace("\\*", ".*", StringComparison.Ordinal);

            patterns.Add("^" + pattern + "$");
        }

        return [.. patterns];
    }

    /// <summary>环境变量里的代理（按 scheme 取，与 .NET 的约定一致）。</summary>
    private static string? ResolveFromEnvironment(string probeUrl)
    {
        static string? Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name)
                        ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());

            return string.IsNullOrWhiteSpace(value) ? null : NormalizeProxyAddress(value);
        }

        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                      ?? Environment.GetEnvironmentVariable("no_proxy");

        // 例外列表里有 * 就等于"所有地址都不走代理"
        if (noProxy is { Length: > 0 } && noProxy.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        return probeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Read("HTTPS_PROXY") ?? Read("ALL_PROXY")
            : Read("HTTP_PROXY") ?? Read("ALL_PROXY");
    }

    /// <summary>
    /// 读 Windows 的"系统代理"设置（代理软件的开关写的就是这里）。
    ///
    /// 标注 SupportedOSPlatform：注册表 API 在别的平台上会抛。
    /// 调用处已经用 OperatingSystem.IsWindows() 挡过一道，这个标注是给分析器看的。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (bool Enabled, string? Server, string? Bypass) ReadWindowsSystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            if (key is null)
            {
                return (false, null, null);
            }

            var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            var server = key.GetValue("ProxyServer") as string;
            var bypass = key.GetValue("ProxyOverride") as string;

            return (enabled, server, bypass);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, null, null);
        }
    }
}
