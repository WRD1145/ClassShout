namespace ClassShout.Core.Remote;

/// <summary>
/// 分享链接的解析。
///
/// 一条分享链接可能以三种样子送到老师手上，都得认：
///   · 完整网页链接：<c>https://relay.example.com/share/&lt;token&gt;</c>（管理员从控制台复制的）
///   · 应用链接：<c>classshout://claim?token=&lt;token&gt;&amp;server=&lt;地址&gt;</c>（点"用教师端打开"）
///   · 光秃秃一个令牌（老师从聊天记录里挑出来复制的）
///
/// 前两种里第二种最好用：它自带服务器地址，老师不必知道自己的账号在哪台服务器上。
/// 所以解析时把地址也一并取出来，而不是让用户再去"中继服务器"那张卡片里填空。
/// </summary>
public static class ShareLink
{
    /// <summary>应用自定义协议。注册了它，网页上的按钮才能把教师端叫起来。</summary>
    public const string Scheme = "classshout";

    /// <summary>认出来的令牌长度（十六进制 32 位）。</summary>
    private const int TokenLength = 32;

    /// <summary>
    /// 从用户输入里取出分享令牌。
    /// </summary>
    /// <param name="input">用户粘贴的链接或令牌。</param>
    /// <param name="serverUrl">链接里带的服务器地址（如果有）。</param>
    /// <returns>令牌；认不出来时为 <c>null</c>。</returns>
    public static string? TryExtractToken(string? input, out string? serverUrl)
    {
        serverUrl = null;

        var text = input?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 光秃秃的令牌
        if (IsToken(text))
        {
            return text.ToLowerInvariant();
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // 应用链接：令牌与地址都在查询串里
        if (string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var query = ParseQuery(uri.Query);

            if (query.TryGetValue("server", out var server) &&
                Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
            {
                serverUrl = $"{serverUri.Scheme}://{serverUri.Authority}";
            }

            return query.TryGetValue("token", out var token) && IsToken(token)
                ? token.ToLowerInvariant()
                : null;
        }

        // 网页链接：令牌是最后一段路径，地址是它的来源
        if (uri.Scheme is "http" or "https")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // 形如 /share/<token>
            if (segments.Length >= 2 &&
                segments[^2].Equals("share", StringComparison.OrdinalIgnoreCase) &&
                IsToken(segments[^1]))
            {
                serverUrl = $"{uri.Scheme}://{uri.Authority}";
                return segments[^1].ToLowerInvariant();
            }

            // 也认 /bind/<token> 这种写法：早先的文档里用过它，别人转述时容易混
            if (segments.Length >= 2 &&
                segments[^2].Equals("bind", StringComparison.OrdinalIgnoreCase) &&
                IsToken(segments[^1]))
            {
                serverUrl = $"{uri.Scheme}://{uri.Authority}";
                return segments[^1].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>把一个 URL 查询串拆成字典。不做 URL 解码之外的处理。</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);

            result[key] = value;
        }

        return result;
    }

    /// <summary>32 位十六进制。刻意不认更短的东西 —— 免得把一句普通的话当成令牌。</summary>
    private static bool IsToken(string value)
        => value.Length == TokenLength && value.All(Uri.IsHexDigit);
}
