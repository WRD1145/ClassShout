using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClassShout.Core.Net;

/// <summary>局域网地址工具。</summary>
public static class NetworkUtility
{
    /// <summary>列出本机所有已启用网卡的 IPv4 广播地址（不含回环）。</summary>
    public static IReadOnlyList<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(address[i] | (mask[i] ^ 0xFF));
                    }

                    result.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 某些受限环境下枚举网卡会失败，退回有限广播地址即可。
        }

        if (result.Count == 0)
        {
            result.Add(IPAddress.Broadcast);
        }

        return result;
    }

    /// <summary>
    /// 取本机对外使用的 IPv4 地址 —— 也就是"别的设备该往哪个地址连我"。
    ///
    /// **不能简单地"取第一个已启用的网卡"**：装了 WSL / Docker / Hyper-V / 虚拟机 / VPN 的机器上，
    /// 那多半是虚拟网卡（`vEthernet (WSL)` 之类，地址常是 172.x），于是教室端会在界面上、
    /// 在广播包的 Host 字段里报出一个**别的设备根本连不上**的地址：
    /// 教师端"能搜到教室、却怎么都连不上"，两端都看不出原因。
    ///
    /// 所以这里给每张网卡打个分，取最高的那张：
    ///   · 有默认网关（说明真能出网） 明显加分；
    ///   · 以太网 / 无线 这类物理类型 加分；
    ///   · 名字像虚拟网卡（vEthernet、WSL、Docker、VMware、VirtualBox、TAP、Tailscale…）减分；
    ///   · 169.254.x（没拿到 DHCP 的自动地址）、172.16–31.x（WSL/Docker 的常用段）减分。
    /// 全都拿不到分时退回原来的行为（第一张能用的网卡），不至于返回空。
    /// </summary>
    public static string GetLocalIPv4()
    {
        try
        {
            string? best = null;
            var bestScore = int.MinValue;

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork
                                    && !gateway.Address.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var text = unicast.Address.ToString();
                    var score = Score(adapter, text, hasGateway);

                    // 同分时保留先遇到的那张（网卡顺序通常就是系统的优先级顺序）
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = text;
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }
        }
        catch (NetworkInformationException)
        {
            // 忽略，返回回环地址
        }

        return IPAddress.Loopback.ToString();
    }

    /// <summary>名字看着像虚拟网卡（WSL / Docker / 虚拟机 / VPN 那一类）。</summary>
    public static bool LooksVirtual(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return false;
        }

        var name = adapterName.ToLowerInvariant();

        return VirtualNameMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }

    private static readonly string[] VirtualNameMarkers =
    [
        "vethernet",   // Hyper-V / WSL
        "wsl",
        "docker",
        "vmware",
        "virtualbox",
        "vbox",
        "hyper-v",
        "tap-",        // OpenVPN 的 TAP 网卡
        "tap0",
        "tailscale",
        "zerotier",
        "wireguard",
        "loopback",
    ];

    /// <summary>给一张网卡上的一个地址打分，见 <see cref="GetLocalIPv4"/> 的说明。</summary>
    public static int Score(NetworkInterface adapter, string address, bool hasGateway)
        => Score(adapter.Name, adapter.NetworkInterfaceType, address, hasGateway);

    /// <summary>打分的纯函数版本（不依赖真实网卡，便于自检直接断言规则本身）。</summary>
    public static int Score(string adapterName, NetworkInterfaceType type, string address, bool hasGateway)
    {
        var score = 0;

        if (hasGateway)
        {
            score += 100;
        }

        if (type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Wireless80211)
        {
            score += 50;
        }

        if (LooksVirtual(adapterName) || type == NetworkInterfaceType.Tunnel)
        {
            score -= 200;
        }

        if (address.StartsWith("169.254.", StringComparison.Ordinal))
        {
            score -= 100;
        }

        // WSL 与 Docker 默认就吃 172.16.0.0/12，而真实的校园网很少用这一段
        if (address.StartsWith("172.", StringComparison.Ordinal))
        {
            var second = int.TryParse(address.Split('.')[1], out var value) ? value : 0;
            if (second is >= 16 and <= 31)
            {
                score -= 60;
            }
        }

        return score;
    }

    /// <summary>广播探测要发给哪些地址（含本机地址与回环，见 DiscoveryTargets 的说明）。</summary>
    public static IReadOnlyList<IPAddress> DiscoveryTargets() => BuildDiscoveryTargets();

    private static List<IPAddress> BuildDiscoveryTargets()
    {
        var targets = new List<IPAddress>();

        void Add(IPAddress address)
        {
            if (!targets.Contains(address))
            {
                targets.Add(address);
            }
        }

        foreach (var broadcast in GetBroadcastAddresses())
        {
            Add(broadcast);
        }

        Add(IPAddress.Broadcast);

        // 本机自己的地址与回环也要发一份：
        //   · 教室端与教师端跑在同一台机器上时（拿桌面端当教师端的人就是这么用的），
        //     广播包可能被 VPN / WSL 那种"默认路由"吞掉，从而谁也收不到；
        //   · 直接发给本机地址与 127.0.0.1 一定到得了，两端在同一台机器上就永远能发现。
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    Add(unicast.Address);
                }
            }
        }

        Add(IPAddress.Loopback);

        return targets;
    }
}
