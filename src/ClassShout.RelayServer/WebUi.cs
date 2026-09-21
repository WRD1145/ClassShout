using System.Reflection;

namespace ClassShout.RelayServer;

/// <summary>
/// 管理控制台页面的加载。
///
/// 页面作为嵌入资源编译进程序集，所以服务器是一个自包含的产物：
/// 换工作目录、单文件发布、被服务方式托管，都不会出现"页面找不到"。
/// 内容在第一次访问时读入并缓存 —— 页面不会变，没必要每次请求都解析一遍资源流。
/// </summary>
public static class WebUi
{
    private const string ResourceName = "ClassShout.RelayServer.wwwroot.index.html";

    private static readonly Lazy<string> LazyPage = new(Load, isThreadSafe: true);

    /// <summary>控制台页面的完整 HTML。</summary>
    public static string Page => LazyPage.Value;

    private static string Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            // 与其返回空白页让人猜，不如把实际原因直接显示出来
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            return $"""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8"><title>ClassShout 控制台</title></head>
                <body style="font-family:sans-serif;padding:40px">
                <h1>控制台页面缺失</h1>
                <p>未能找到嵌入资源 <code>{ResourceName}</code>。</p>
                <p>当前程序集包含的资源：</p>
                <pre>{available}</pre>
                </body></html>
                """;
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
