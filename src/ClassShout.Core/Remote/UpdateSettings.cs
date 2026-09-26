namespace ClassShout.Core.Remote;

/// <summary>
/// 检查更新这一块的设置。
///
/// 关于隐私有一条刻意为之的规矩：**只在你按下按钮时才发请求**。
/// 后台定时去问"有没有新版本"，等于每次开应用都向外面报一次到 ——
/// 而对一台放在教室里的机器来说，这件事没有任何必要。
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>默认仓库（GitHub 上的那个）。</summary>
    public const string DefaultRepository = "WRD1145/ClassShout";

    /// <summary>取哪个仓库的发行版。镜像没有单独指定仓库时用它。</summary>
    public string Repository { get; set; } = DefaultRepository;

    /// <summary>
    /// 所有镜像源（内置的 + 自己加的）。
    ///
    /// 存一份完整的列表而不是"只存自定义的那几个"：内置的也可能被用户改掉
    /// （比如 ghproxy 挂了、他改成另一个能用的），改完要能记住。
    /// 列表为空时用 <see cref="UpdateMirror.BuiltIns"/> 补上。
    /// </summary>
    public List<UpdateMirror> Mirrors { get; set; } = [];

    /// <summary>当前用哪个镜像（按 <see cref="UpdateMirror.Id"/>）。找不到就用第一个。</summary>
    public string? SelectedMirrorId { get; set; }

    /// <summary>走不走代理。</summary>
    public UpdateProxyMode ProxyMode { get; set; } = UpdateProxyMode.System;

    /// <summary>自定义代理地址（<see cref="UpdateProxyMode.Custom"/> 时用），例如 http://127.0.0.1:7890。</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>最近一次检查的时间，界面上显示"上次检查"用。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>最近一次查到的最新版本号（不含 v）。</summary>
    public string? LatestVersion { get; set; }

    /// <summary>用户选择跳过的那一版：不再提示它，但更新的一版照样提示。</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>取当前选中的镜像。</summary>
    public UpdateMirror SelectedMirror
    {
        get
        {
            var list = Mirrors.Count > 0 ? Mirrors : UpdateMirror.BuiltIns();

            return list.FirstOrDefault(mirror => string.Equals(mirror.Id, SelectedMirrorId, StringComparison.OrdinalIgnoreCase))
                   ?? list[0];
        }
    }

    /// <summary>收拾手改坏的值。</summary>
    public UpdateSettings Normalized()
    {
        var mirrors = (Mirrors is { Count: > 0 } ? Mirrors : UpdateMirror.BuiltIns())
            .Select(mirror => mirror.Normalized())
            .ToList();

        // 同 Id 只留一条：手改配置时很容易复制出一份一模一样的
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        mirrors.RemoveAll(mirror => !seen.Add(mirror.Id));

        // 内置的兜底项一个都不能少：用户删着删着删光了的话，
        // 界面上就一条源都不剩、连"直连 GitHub"都试不了。
        foreach (var builtIn in UpdateMirror.BuiltIns())
        {
            if (!mirrors.Any(mirror => string.Equals(mirror.Id, builtIn.Id, StringComparison.OrdinalIgnoreCase)))
            {
                mirrors.Add(builtIn);
            }
        }

        var selectedId = mirrors.Any(m => string.Equals(m.Id, SelectedMirrorId, StringComparison.OrdinalIgnoreCase))
            ? SelectedMirrorId
            : mirrors[0].Id;

        var proxyUrl = (ProxyUrl ?? string.Empty).Trim();

        return new UpdateSettings
        {
            Repository = NormalizeRepository(Repository),
            Mirrors = mirrors,
            SelectedMirrorId = selectedId,

            // 自定义代理却没填地址，等于"没配好" —— 退回跟随系统。
            // 否则用户会以为"我明明设了代理"，而实际上一个请求都没走代理。
            ProxyMode = ProxyMode == UpdateProxyMode.Custom && proxyUrl.Length == 0 ? UpdateProxyMode.System : ProxyMode,
            ProxyUrl = proxyUrl,

            LastCheckedAt = LastCheckedAt,
            LatestVersion = string.IsNullOrWhiteSpace(LatestVersion) ? null : LatestVersion.Trim().TrimStart('v', 'V'),
            SkippedVersion = string.IsNullOrWhiteSpace(SkippedVersion) ? null : SkippedVersion.Trim().TrimStart('v', 'V'),
        };
    }

    /// <summary>
    /// 把"owner/repo"或一条完整的地址都收拾成 owner/repo。
    ///
    /// 之所以容错：让填这一栏的人去记"只能填 owner/repo"是没必要的，
    /// 而粘一整条地址进来是最自然的做法（GitHub 与 Gitee 的地址都要认）。
    /// </summary>
    public static string NormalizeRepository(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return DefaultRepository;
        }

        foreach (var prefix in new[]
                 {
                     "https://github.com/", "https://gitee.com/",
                     "http://github.com/", "http://gitee.com/",
                 })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
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

        var path = url;

        foreach (var prefix in new[] { "https://github.com/", "https://gitee.com/" })
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = url[prefix.Length..];
                break;
            }
        }

        return template.Trim().Replace("{url}", url).Replace("{path}", path);
    }
}
