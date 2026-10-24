using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner.Discovery;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>A neighbour found on a local link.</summary>
/// <param name="Method">"icmp6" when it answered the multicast echo, "ndp" when only the neighbour cache knows it.</param>
public sealed record Ipv6Host(
    Ipv6Address Address, string? Mac, int InterfaceIndex, bool IsRouter, double? RttMs, string Method, string? Name = null);

/// <summary>IPv6 discovery. A /64 cannot be swept, so the link is found the way IPv6 does it: an echo to
/// the all-nodes group makes every host on the link resolve us (filling the neighbour cache), then the cache
/// is read. Explicit targets get a unicast echo and TCP connects. Nothing here walks an address range.</summary>
public sealed class Ipv6Discovery
{
    /// <summary>The most addresses a prefix may hold to be walked one by one (the IPv4 cap).</summary>
    public const int MaxSweepAddresses = 65536;

    private readonly INeighborTable _table;
    private readonly IIpv6Net _net;
    private readonly IDiscoveryNet? _tcp;
    private readonly Func<string, string?>? _nameOf;

    public Ipv6Discovery(INeighborTable table, IIpv6Net net, IDiscoveryNet? tcp = null, Func<string, string?>? nameOf = null)
    {
        _table = table;
        _net = net;
        _tcp = tcp;
        _nameOf = nameOf;
    }

    /// <summary>False for a prefix that holds more than <see cref="MaxSweepAddresses"/> addresses (every /64).</summary>
    public static bool CanSweep(Ipv6Prefix prefix) => !prefix.ExceedsAddresses(MaxSweepAddresses);

    public async Task<IReadOnlyList<Ipv6Host>> DiscoverLinkAsync(
        IEnumerable<OwnIpv6Address> own, int echoTimeoutMs = 1500, int settleMs = 800, CancellationToken ct = default)
    {
        var mine = own.ToList();
        var ownValues = mine.Select(o => o.Address.Value).ToHashSet();
        var ifaces = mine.Where(o => o.IsLinkLocal).Select(o => o.InterfaceIndex).Distinct().ToList();
        if (ifaces.Count == 0) ifaces = mine.Select(o => o.InterfaceIndex).Distinct().ToList();
        var ifaceSet = ifaces.ToHashSet();

        var replies = new Dictionary<(UInt128, int), double>();
        var echoes = ifaces.Select(async idx =>
        {
            var group = Ipv6Address.AllNodes.WithZone(idx.ToString(CultureInfo.InvariantCulture));
            var got = await _net.EchoAsync(group, echoTimeoutMs, ct).ConfigureAwait(false);
            lock (replies)
                foreach (var r in got)
                    replies.TryAdd((r.From.Value, idx), r.RttMs);
        }).ToList();
        await Task.WhenAll(echoes).ConfigureAwait(false);

        // Hosts resolve us after the echo; give the cache a moment to fill.
        if (settleMs > 0) await Task.Delay(settleMs, ct).ConfigureAwait(false);

        var hosts = new Dictionary<(UInt128, int), Ipv6Host>();
        foreach (var n in _table.Read())
        {
            if (!ifaceSet.Contains(n.InterfaceIndex) || ownValues.Contains(n.Address.Value) || n.Address.Value == 0) continue;
            double? rtt = replies.TryGetValue((n.Address.Value, n.InterfaceIndex), out var r) ? r : null;
            hosts[(n.Address.Value, n.InterfaceIndex)] =
                new Ipv6Host(n.Address, n.Mac, n.InterfaceIndex, n.IsRouter, rtt, rtt is null ? "ndp" : "icmp6");
        }
        // Answered the echo but no cache row (yet): still a host, without a MAC.
        foreach (var ((value, idx), rtt) in replies)
        {
            if (ownValues.Contains(value) || hosts.ContainsKey((value, idx))) continue;
            hosts[(value, idx)] = new Ipv6Host(
                new Ipv6Address(value, idx.ToString(CultureInfo.InvariantCulture)), null, idx, false, rtt, "icmp6");
        }

        var result = hosts.Values.OrderBy(h => h.InterfaceIndex).ThenBy(h => h.Address.Value).ToList();
        if (_nameOf is not null)
            for (int i = 0; i < result.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = _nameOf(result[i].Address.ToScopedString());
                if (!string.IsNullOrEmpty(name)) result[i] = result[i] with { Name = name };
            }
        return result;
    }

    /// <summary>Probes one explicit target: an echo first, then TCP connects. An open or refused port both
    /// prove the host is up. A link-local target needs its interface index as the zone.</summary>
    public async Task<DiscoveryResult> ProbeTargetAsync(
        Ipv6Address target, IReadOnlyList<int> ports, int timeoutMs, CancellationToken ct = default)
    {
        var echo = await _net.EchoAsync(target, timeoutMs, ct).ConfigureAwait(false);
        foreach (var r in echo)
            if (r.From.Value == target.Value)
                return new DiscoveryResult(true, "icmp6", r.RttMs);

        if (_tcp is null) return DiscoveryResult.None("icmp6");
        var text = target.ToScopedString();
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _tcp.TcpConnectAsync(text, port, timeoutMs, ct).ConfigureAwait(false);
            if (outcome is TcpOutcome.Open or TcpOutcome.Refused)
                return new DiscoveryResult(true, "tcp:" + port, null);
        }
        return DiscoveryResult.None("icmp6");
    }
}
