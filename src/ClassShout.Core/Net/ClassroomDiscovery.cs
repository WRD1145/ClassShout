using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassShout.Core.Protocol;

namespace ClassShout.Core.Net;

/// <summary>教室端向局域网广播的自我介绍。</summary>
public sealed record ClassroomAnnouncement
{
    /// <summary>教室端稳定标识（安装时生成，用于教师端记住上次连接的教室）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>教室名，例如“三年二班”。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>教室端所在主机 IP。</summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    /// <summary>控制连接端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; init; } = ShoutProtocol.DefaultTcpPort;

    /// <summary>协议版本。</summary>
    [JsonPropertyName("pv")]
    public int ProtocolVersion { get; init; } = ShoutProtocol.Version;

    /// <summary>应用版本，仅用于展示。</summary>
    [JsonPropertyName("ver")]
    public string? AppVersion { get; init; }

    /// <summary>发现报文的统一序列化配置。</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>解析出的地址，便于直接构造连接。</summary>
    public IPEndPoint ToEndPoint()
    {
        var candidate = IPAddress.TryParse(Host, out var parsed) ? parsed : IPAddress.Loopback;
        return new IPEndPoint(candidate, Port);
    }
}

/// <summary>
/// 教室端 UDP 发现响应器。
/// 收到 <see cref="ShoutProtocol.DiscoveryProbe"/> 后单播回一条 JSON 自我介绍，
/// 教师端据此列出可连接的教室，无需手输 IP。
/// </summary>
public sealed class ClassroomAnnouncer : IAsyncDisposable
{
    private readonly int _port;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ClassroomAnnouncer(int port = ShoutProtocol.DefaultDiscoveryPort)
    {
        _port = port;
    }

    /// <summary>教室端当前对外信息；名称变化后无需重启响应器。</summary>
    public ClassroomAnnouncement Current { get; set; } = new();

    /// <summary>
    /// 过程日志（"调试"档）：监听起没起来、绑在哪个端口。
    ///
    /// 这两条回调存在的理由只有一个：教室端"扫不到"时，得能看出**这一侧到底在不在听**。
    /// 平时它们是关着的（见 AppLog 的档位），需要排查时把档位调到调试就会显示。
    /// </summary>
    public Action<string>? LogDebug { get; set; }

    /// <summary>最细的日志（"跟踪"档）：每一条收到的探测与回复。</summary>
    public Action<string>? LogTrace { get; set; }

    /// <summary>启动监听。已在运行则直接返回。</summary>
    public void Start()
    {
        if (_udp is not null)
        {
            return;
        }

        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        udp.EnableBroadcast = true;

        _udp = udp;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));

        LogDebug?.Invoke($"UDP 发现监听已开启：0.0.0.0:{_port}（教师端扫描时就是往这个端口发探测）。");
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(ShoutProtocol.DiscoveryProbe, StringComparison.Ordinal))
                {
                    continue;
                }

                var reply = JsonSerializer.SerializeToUtf8Bytes(Current, ClassroomAnnouncement.SerializerOptions);
                await udp.SendAsync(reply, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);

                LogTrace?.Invoke($"收到来自 {result.RemoteEndPoint} 的探测，已回复「{Current.Name}」。");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // 单个报文出错不应终止整个监听循环。
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _udp?.Dispose();
        _udp = null;

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}

/// <summary>教师端使用的教室发现器（UDP 广播扫描）。</summary>
public sealed class ClassroomDiscovery
{
    private readonly int _port;
    private readonly TimeSpan _timeout;

    public ClassroomDiscovery(int port = ShoutProtocol.DefaultDiscoveryPort, TimeSpan? timeout = null)
    {
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(1.5);
    }

    /// <summary>过程日志（"跟踪"档）：发给了哪些地址、哪个地址发失败。给排障用。</summary>
    public Action<string>? LogTrace { get; set; }

    /// <summary>广播探测，收集在 <see cref="_timeout"/> 内响应的所有教室端。</summary>
    public async Task<IReadOnlyList<ClassroomAnnouncement>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, ClassroomAnnouncement>(StringComparer.Ordinal);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = Encoding.UTF8.GetBytes(ShoutProtocol.DiscoveryProbe);

        // 除了各网段的广播地址，还发给本机各地址与回环：教室端与教师端在同一台机器上时
        // （用桌面端当教师端就是这么用的），广播可能被 VPN / WSL 那种默认路由吞掉。
        var targets = NetworkUtility.DiscoveryTargets();

        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(probe, new IPEndPoint(target, _port), cancellationToken).ConfigureAwait(false);
                LogTrace?.Invoke($"已向 {target}:{_port} 发出探测。");
            }
            catch (SocketException ex)
            {
                // 某些网卡不允许广播：跳过它，但把原因记下来 —— "一个回包都没有"时，
                // 这几行正是要说清"到底哪几个地址没发出去"。
                LogTrace?.Invoke($"向 {target}:{_port} 发探测失败（{ex.SocketErrorCode}），跳过。");
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var announcement = JsonSerializer.Deserialize<ClassroomAnnouncement>(result.Buffer, ClassroomAnnouncement.SerializerOptions);
                if (announcement is null)
                {
                    continue;
                }

                // 一律用**回包的来源地址**当连接地址，而不是包里的 Host：
                // 来源地址按定义就是从这台机器能走通的地址（本机探测就是 127.0.0.1），
                // 而包里的 Host 是教室端自己挑的，装了 WSL / VPN 的机器上它可能报出一个
                // 别人连不上的虚拟网卡地址 —— 那会表现成"搜得到、连不上"。
                var source = result.RemoteEndPoint.Address.ToString();
                announcement = announcement with { Host = source };

                var key = string.IsNullOrEmpty(announcement.Id)
                    ? $"{announcement.Host}:{announcement.Port}"
                    : announcement.Id;

                LogTrace?.Invoke($"收到来自 {result.RemoteEndPoint} 的回应：「{announcement.Name}」，连接地址取 {source}:{announcement.Port}。");

                found[key] = announcement;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (JsonException)
            {
                // 忽略非本协议的报文。
            }
        }

        return found.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
