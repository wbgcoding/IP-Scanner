using IpScanner.Core.Net;

namespace IpScanner.Core.Models;

/// <summary>One IPv6 address configured on a local network card. <see cref="Address"/> carries the interface
/// scope id as its zone for link-local addresses, which is what a link-local send or bind needs.</summary>
public sealed record OwnIpv6Address(string Interface, int InterfaceIndex, Ipv6Address Address, int PrefixLength)
{
    public Ipv6Prefix Prefix => new(Address.Value, PrefixLength);
    public bool IsLinkLocal => Address.IsLinkLocal;
}
