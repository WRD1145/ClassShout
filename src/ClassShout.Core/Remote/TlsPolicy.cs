using System.Net;
using System.Net.Security;

namespace ClassShout.Core.Remote;

/// <summary>
/// 连接自己的服务器时要不要"证书有问题也继续连"。
///
/// 为什么需要这个开关：学校里的中继服务器常常是**自签证书**（或者干脆是内网 IP 上的
/// 临时证书），而浏览器的做法是让人点一下"继续访问"——教师端与教室端没有那个按钮，
/// 于是表现就是一句"SSL 证书有问题"，之后什么也做不了。这个开关就是那个"继续访问"。
///
/// 代价必须说清楚：**打开它，这条链路上的人就能看到并改动内容**。
/// 证书本来要证明的正是"对面就是那台服务器"，绕过它之后，同一个网络里的任何设备
/// 都可以冒充服务器 —— 包括那些要求你别开浏览器警告的人想做的事。
/// 所以默认关闭、界面上带警告、文档里也写一遍。
///
/// 作用范围刻意**只给"连我们自己的服务器"与"检查更新"**：
///   · 目标服务器是运维自己部署的那台，证书也可以由他自己签发；
///   · 更新下载来自 GitHub 与镜像，学校里的 TLS 代理常常把它们的证书换掉。
/// 在线朗读（Edge 语音）那类第三方的连接**不**受它影响：那不是我们的服务器，
/// 为它放开证书校验没有任何正当理由。
/// </summary>
public static class TlsPolicy
{
    /// <summary>
    /// 是否接受"证书有问题"的连接。
    ///
    /// 做成静态可写而不是往十个构造函数里传一个参数：这条链路上有
    /// 账号、绑定、名单、定时、更新下载等一串客户端，它们共用同一个
    /// <see cref="HttpClient"/> —— 而校验回调在**每次握手**时才被调用，
    /// 所以改这里立刻对**下一次连接**生效，不必重启应用。
    /// 已经建立的连接不受影响（TLS 会话已经协商完了），这一点界面上也写明了。
    /// </summary>
    public static bool AllowUntrusted { get; set; }

    /// <summary>
    /// 证书该不该被接受。
    ///
    /// 单独抽出来是为了能直接断言：这条判断是"安全开关"的全部逻辑，
    /// 而它的错法（该拒的放过去）在界面上完全看不出来。
    /// </summary>
    /// <param name="errors">系统给出的校验结果。</param>
    /// <param name="allowUntrusted">是否打开了那个开关。</param>
    public static bool IsAcceptable(SslPolicyErrors errors, bool allowUntrusted)
        => errors == SslPolicyErrors.None || allowUntrusted;

    /// <summary>
    /// 建一个按当前策略校验证书的 HttpClient。
    ///
    /// 注意 <see cref="HttpClientHandler"/> 与 <see cref="SocketsHttpHandler"/> 的区别：
    /// 只有后者能同时给出"不走系统代理"这类控制，而两者都支持
    /// <c>SslOptions.RemoteCertificateValidationCallback</c>。这里用后者。
    /// </summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
        => new(CreateHandler())
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };

    /// <summary>按当前策略校验证书的处理器。连自己的服务器、检查更新都用它。</summary>
    public static SocketsHttpHandler CreateHandler()
        => new()
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // 一次握手调用一次：所以开关改完，下一次连接就按新策略走
                RemoteCertificateValidationCallback = (_, _, _, errors) => IsAcceptable(errors, AllowUntrusted),
            },
        };

    /// <summary>
    /// 一把**永远严格**的客户端：给第三方服务用（例如在线朗读）。
    ///
    /// 把它与"连自己服务器"分开是有意的：那个开关的正当理由只有"服务器证书是我自己签的"，
    /// 而微软那台服务器的证书没有任何理由由我们来放宽 —— 它的证书坏了，
    /// 要么是网络被人插手了，要么是系统时间错了，两种都该如实失败。
    /// </summary>
    public static HttpClient CreateStrictClient(TimeSpan? timeout = null)
        => new(new SocketsHttpHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>给界面用的一句话：现在是什么策略、代价是什么。</summary>
    /// <remarks>
    /// 刻意不写"打开下面这一项"之类的方位词：这一行在教师端排在开关下面、
    /// 在教室端也挨着开关，但两处的上下顺序不同 —— 一句带方向的提示总有一边是错的。
    /// </remarks>
    public static string Describe(bool allowUntrusted) => allowUntrusted
        ? "已允许证书有问题的连接：连自签证书的服务器能用了，但这条链路上的中间人能读到并改动内容 —— 只在自家服务器上用。"
        : "只接受证书正常的连接。服务器用自签证书时连不上，打开「允许不信任的 TLS 连接」即可，改完重新点一次登录或连接。";
}
