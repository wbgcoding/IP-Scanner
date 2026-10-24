using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IpScanner.Core.Models;
using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Fast local-network detection. Uses a UDP-connect trick for the own IP and
/// .NET NetworkInformation for gateway/MAC/mask/DNS.
/// </summary>
public static class NetworkDetector
{
    // Any public IP works for the UDP-connect trick - no packet is sent.
    private const string RouteProbeIp = "8.8.8.8";
    private const int RouteProbePort = 80;

    /// <summary>Local address every ping and TCP/UDP probe binds from when
    /// a specific network adapter is selected instead of the OS-routed one -
    /// null/empty means automatic. The single cross-cutting setting for the whole
    /// scan pipeline (read by IcmpPinger's caller and by PortScanner).</summary>
    public static string? SelectedSourceIp { get; set; }

    /// <summary>The selected source address when it can talk to <paramref name="target"/>, else null so the
    /// socket stays unbound. The selection is an IPv4 address, so an IPv6 target must not bind to it.</summary>
    internal static IPAddress? SourceFor(string target)
    {
        if (SelectedSourceIp is not { Length: > 0 } src || !IPAddress.TryParse(src, out var srcAddr)) return null;
        if (!IPAddress.TryParse(target, out var dst)) return srcAddr;
        return dst.AddressFamily == srcAddr.AddressFamily ? srcAddr : null;
    }

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
        catch (Exception ex) { Log.Warn(LogArea.Scan, "Route based network detection failed", ex); /* no route / offline - fall back to NIC scan */ }

        // 2. Fallback: first up, non-loopback NIC with a usable IPv4 (prefer one
        //    that has a default gateway). Works offline / without an 8.8.8.8 route.
        try
        {
            string? any = null;
            foreach (var (ni, ua) in Ipv4Interfaces())
            {
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;      // link-local APIPA
                bool hasGateway = ni.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                              && !g.Address.ToString().StartsWith("0."));
                if (hasGateway) return ip;     // best candidate
                any ??= ip;                    // remember as fallback
            }
            return any;
        }
        catch (Exception ex) { Log.Warn(LogArea.Scan, "Network detection failed", ex); return null; }
    }

    /// <summary>ForceIp pins detection to one specific adapter's address
    /// instead of the auto-routed one - used when the user picked a card explicitly.
    /// Falls back to the normal auto-detected address if forceIp is not currently
    /// a usable IPv4 address on this machine (e.g. the adapter was unplugged).</summary>
    public static NetworkInfo DetectFast(string? forceIp = null)
    {
        var info = new NetworkInfo();
        var localIp = !string.IsNullOrEmpty(forceIp) && Ipv4Interfaces().Any(t => t.Addr.Address.ToString() == forceIp)
            ? forceIp : GetLocalIpFast();
        info.Ip = localIp;
        if (localIp is null) return info;

        foreach (var (ni, ua) in Ipv4Interfaces())
        {
            if (ua.Address.ToString() != localIp) continue;

            var props = ni.GetIPProperties();
            info.Interface = ni.Name;
            info.Mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());
            info.SubnetMask = ua.IPv4Mask?.ToString();
            info.Gateway = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString();
            info.DnsServers = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString()).ToList();
            info.Ipv6 = ListOwnIpv6(ReadInterfaceAddresses().Where(a => a.Interface == ni.Name));
            return info;
        }
        return info;
    }

    /// <summary>One entry per up, non-loopback network card with a usable
    /// IPv4 address - lets the Wake-on-LAN tool send from a specific card instead of
    /// always guessing the one the app itself is routed through.</summary>
    public sealed record NetworkCard(string Name, string Ip, string? SubnetMask);

    public static List<NetworkCard> ListIpv4Cards()
    {
        var list = new List<NetworkCard>();
        try
        {
            foreach (var (ni, ua) in Ipv4Interfaces())
            {
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;      // link-local APIPA
                list.Add(new NetworkCard(ni.Name, ip, ua.IPv4Mask?.ToString()));
            }
        }
        catch (Exception ex) { Log.Warn(LogArea.Scan, "Listing the network adapters failed", ex); /* best-effort */ }
        return list;
    }

    /// <summary>One unicast IPv6 address as the operating system reports it, before any filtering.</summary>
    public sealed record InterfaceAddress(string Interface, int Index, bool Up, bool Loopback, IPAddress Address, int PrefixLength, bool Usable);

    /// <summary>The IPv6 addresses of every card that is up and not loopback, link-local first within a card.
    /// Tentative and duplicate addresses are left out, so the list holds only addresses a probe can bind to.</summary>
    public static List<OwnIpv6Address> ListOwnIpv6() => ListOwnIpv6(ReadInterfaceAddresses());

    public static List<OwnIpv6Address> ListOwnIpv6(IEnumerable<InterfaceAddress> source)
    {
        var list = new List<OwnIpv6Address>();
        foreach (var a in source)
        {
            if (!a.Up || a.Loopback || !a.Usable || a.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
            var addr = Ipv6Address.FromBytes(a.Address.GetAddressBytes());
            if (addr.Kind is Ipv6Kind.Loopback or Ipv6Kind.Unspecified or Ipv6Kind.Multicast) continue;
            if (addr.IsLinkLocal) addr = addr.WithZone((a.Address.ScopeId != 0 ? a.Address.ScopeId : a.Index).ToString());
            list.Add(new OwnIpv6Address(a.Interface, a.Index, addr, a.PrefixLength));
        }
        return list.OrderBy(o => o.Interface, StringComparer.Ordinal).ThenBy(o => o.IsLinkLocal ? 0 : 1).ToList();
    }

    private static List<InterfaceAddress> ReadInterfaceAddresses()
    {
        var list = new List<InterfaceAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                int index;
                try { index = props.GetIPv6Properties().Index; }
                catch (NetworkInformationException) { continue; }   // card without IPv6
                foreach (var ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        list.Add(new InterfaceAddress(ni.Name, index, ni.OperationalStatus == OperationalStatus.Up,
                            ni.NetworkInterfaceType == NetworkInterfaceType.Loopback, ua.Address, ua.PrefixLength,
                            ua.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred));
            }
        }
        catch (Exception ex) { Log.Warn(LogArea.Scan, "Listing the IPv6 addresses failed", ex); /* best-effort */ }
        return list;
    }

    /// <summary>Up, non-loopback NICs paired with each of their IPv4 unicast addresses.</summary>
    private static IEnumerable<(NetworkInterface Ni, UnicastIPAddressInformation Addr)> Ipv4Interfaces()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return (ni, ua);
        }
    }

    internal static string FormatMac(byte[] bytes)
        => bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
