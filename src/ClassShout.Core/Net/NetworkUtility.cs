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

    /// <summary>取本机对外使用的 IPv4 地址（优先非回环、非 APIPA）。</summary>
    public static string GetLocalIPv4()
    {
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
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var text = unicast.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return text;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 忽略，返回回环地址
        }

        return IPAddress.Loopback.ToString();
    }
}
