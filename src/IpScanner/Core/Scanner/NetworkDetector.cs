using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Fast local-network detection. Uses a UDP-connect trick for the own IP and
/// .NET NetworkInformation for gateway/MAC/mask/DNS (replaces Python P/Invoke).
/// </summary>
public static class NetworkDetector
{
    public static string? GetLocalIpFast()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);              // no packet sent for UDP
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
            return ip != "0.0.0.0" && !ip.StartsWith("169.254.") ? ip : null;
        }
        catch { return null; }
    }

    public static NetworkInfo DetectFast()
    {
        var info = new NetworkInfo();
        var localIp = GetLocalIpFast();
        info.Ip = localIp;
        if (localIp is null) return info;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString() != localIp) continue;

                info.Interface = ni.Name;
                info.Mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());
                info.SubnetMask = ua.IPv4Mask?.ToString();
                info.Gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
                info.DnsServers = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString()).ToList();
                return info;
            }
        }
        return info;
    }

    private static string FormatMac(byte[] bytes)
        => bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
