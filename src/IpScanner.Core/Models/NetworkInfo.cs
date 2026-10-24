using IpScanner.Core.Net;

namespace IpScanner.Core.Models;

public sealed class NetworkInfo
{
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public string? Gateway { get; set; }
    public string? SubnetMask { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public string Interface { get; set; } = "";

    /// <summary>The usable IPv6 addresses of the same card (link-local first), empty when it has none.</summary>
    public List<OwnIpv6Address> Ipv6 { get; set; } = new();

    /// <summary>Real CIDR prefix length from the detected subnet mask, /24 if
    /// the mask is missing or not a valid contiguous mask.</summary>
    public int CidrLength => Ipv4.MaskToCidr(SubnetMask) ?? 24;

    /// <summary>"192.168.1.0/24" style label reflecting the real detected mask,
    /// or null when no local IP was detected.</summary>
    public string? Cidr
    {
        get
        {
            if (Ip is null || !Ipv4.TryParseUInt(Ip, out var addr)) return null;
            int hostBits = 32 - CidrLength;
            uint mask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
            return $"{Ipv4.FromUInt(addr & mask)}/{CidrLength}";
        }
    }
}
