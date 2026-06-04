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
    // Any public IP works for the UDP-connect trick — no packet is sent.
    private const string RouteProbeIp = "8.8.8.8";
    private const int RouteProbePort = 80;

    public static string? GetLocalIpFast()
    {
        // 1. UDP-connect trick (no packet sent): resolves the routed source IP.
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(RouteProbeIp, RouteProbePort);
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
            if (ip != "0.0.0.0" && !ip.StartsWith("169.254.")) return ip;
        }
        catch { /* no route / offline — fall back to NIC scan */ }

        // 2. Fallback: first up, non-loopback NIC with a usable IPv4 (prefer one
        //    that has a default gateway). Works offline / without an 8.8.8.8 route.
        try
        {
            string? any = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                  && !g.Address.ToString().StartsWith("0."));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    if (hasGateway) return ip;     // best candidate
                    any ??= ip;                    // remember as fallback
                }
            }
            return any;
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
