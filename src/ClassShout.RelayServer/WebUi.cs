using System.Reflection;

namespace ClassShout.RelayServer;

/// <summary>
/// 管理控制台静态资源的加载。
///
/// 页面作为嵌入资源编译进程序集，所以服务器是一个自包含的产物：
/// 换工作目录、单文件发布、被服务方式托管，都不会出现"页面找不到"。
/// 内容在第一次访问时读入并缓存 —— 这些文件不会变，没必要每次请求都解析一遍资源流。
///
/// 分成 HTML / CSS / JS 三个资源，是为了能下发一条严格的 CSP：
/// 只要还有内联脚本或内联事件处理器，script-src 就没法收紧，
/// 于是"某处转义写坏了"就会直接变成可执行的 XSS。分开之后，
/// 注入的脚本拿不到任何可执行的位置。
/// </summary>
public static class WebUi
{
    private const string HtmlResource = "ClassShout.RelayServer.wwwroot.index.html";
    private const string CssResource = "ClassShout.RelayServer.wwwroot.app.css";
    private const string JsResource = "ClassShout.RelayServer.wwwroot.app.js";

    private static readonly Lazy<string> LazyPage = new(() => Load(HtmlResource, "页面"), isThreadSafe: true);
    private static readonly Lazy<string> LazyCss = new(() => Load(CssResource, "样式表"), isThreadSafe: true);
    private static readonly Lazy<string> LazyJs = new(() => Load(JsResource, "脚本"), isThreadSafe: true);

    /// <summary>控制台页面。</summary>
    public static string Page => LazyPage.Value;

    /// <summary>控制台样式表。</summary>
    public static string Css => LazyCss.Value;

    /// <summary>控制台脚本。</summary>
    public static string Js => LazyJs.Value;

    /// <summary>
    /// 控制台的安全响应头。
    ///
    /// CSP 是 XSS 的最后一道兜底：即使将来某处转义又被写坏，注入的脚本也执行不了。
    ///   default-src 'none'   没有显式允许的一律禁止
    ///   script-src  'self'   只跑 /app.js，内联脚本与内联事件处理器全部拒绝
    ///   style-src   'self'   只认 /app.css，行内 style 属性一并拒绝
    ///   connect-src 'self'   只允许同源 fetch
    ///   frame-ancestors / base-uri / form-action 一并关掉，避免点击劫持与基址劫持
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "form-action 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'";

    private static string Load(string resourceName, string label)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            // 与其返回空白页让人猜，不如把实际原因直接显示出来。
            // CSS / JS 缺失时返回一段注释即可 —— 浏览器会忽略它，
            // 而页面上会明显看出样式与交互都没了。
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            return resourceName.EndsWith(".html", StringComparison.Ordinal)
                ? $"""
                   <!doctype html>
                   <html lang="zh-CN"><head><meta charset="utf-8"><title>ClassShout 控制台</title></head>
                   <body style="font-family:sans-serif;padding:40px">
                   <h1>控制台{label}缺失</h1>
                   <p>未能找到嵌入资源 <code>{resourceName}</code>。</p>
                   <p>当前程序集包含的资源：</p>
                   <pre>{available}</pre>
                   </body></html>
                   """
                : $"/* 未能找到嵌入资源 {resourceName}。可用：{available} */";
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
