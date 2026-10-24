using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner.Discovery;

/// <summary>Runs the selected discovery modes against one target and reports which one found it.</summary>
public sealed class DiscoveryRunner
{
    public DiscoveryRunner(IReadOnlyList<IDiscoveryProbe> probes)
    {
        ListScan = probes.Any(p => p.Mode == DiscoveryMode.ListScan);
        SkipDiscovery = probes.Any(p => p.Mode == DiscoveryMode.SkipDiscovery);
        Live = probes.Where(p => p.Mode is not (DiscoveryMode.ListScan or DiscoveryMode.SkipDiscovery)).ToArray();
    }

    /// <summary>Resolve and list the targets, send nothing.</summary>
    public bool ListScan { get; }

    /// <summary>Every target counts as up at discovery.</summary>
    public bool SkipDiscovery { get; }

    /// <summary>The probes that put packets on the wire.</summary>
    public IReadOnlyList<IDiscoveryProbe> Live { get; }

    public static DiscoveryRunner Create(IEnumerable<DiscoveryMode> modes, string tcpPorts, string udpPorts, IDiscoveryNet net,
        Func<string, int, PingResult> ping, int timeoutMs)
    {
        var tcp = DiscoveryModes.ParsePorts(tcpPorts, DiscoveryModes.DefaultTcpPorts);
        var udp = DiscoveryModes.ParsePorts(udpPorts, DiscoveryModes.DefaultUdpPorts);
        var probes = new List<IDiscoveryProbe>();
        foreach (var m in modes.Distinct())
        {
            switch (m)
            {
                case DiscoveryMode.Arp: probes.Add(new ArpProbe(net)); break;
                case DiscoveryMode.IcmpEcho: probes.Add(new IcmpEchoProbe(ping, timeoutMs)); break;
                case DiscoveryMode.IcmpTimestamp:
                case DiscoveryMode.IcmpMask: probes.Add(new IcmpRawProbe(net, m, timeoutMs)); break;
                case DiscoveryMode.TcpConnect: probes.Add(new TcpConnectProbe(net, tcp, timeoutMs)); break;
                case DiscoveryMode.TcpSyn:
                case DiscoveryMode.TcpAck: probes.Add(new RawTcpPingProbe(net, m, tcp, timeoutMs)); break;
                case DiscoveryMode.Udp: probes.Add(new UdpPingProbe(net, udp, timeoutMs)); break;
                case DiscoveryMode.ListScan: probes.Add(new ListScanProbe()); break;
                case DiscoveryMode.SkipDiscovery: probes.Add(new SkipDiscoveryProbe()); break;
            }
        }
        return new DiscoveryRunner(probes);
    }

    /// <summary>Mode that produced a method code ("tcp:445" is the TCP connect mode), null when unknown.</summary>
    public static DiscoveryMode? ModeOfMethod(string? method)
    {
        if (string.IsNullOrEmpty(method)) return null;
        string key = method.Split(':')[0];
        foreach (var m in DiscoveryModes.All) if (DiscoveryModes.KeyOf(m) == key) return m;
        return null;
    }

    /// <summary>All live probes race; the first to find the host wins. A mode that found this host before is tried
    /// alone first, so repeated pings keep measuring the same way. With skip selected next to live modes, a host
    /// that no live probe finds still counts as up at discovery (<paramref name="discovery"/>), never at later pings.</summary>
    public async Task<DiscoveryResult> ProbeAsync(string target, string? preferredMethod, CancellationToken ct, bool discovery = false)
    {
        if (Live.Count == 0) return SkipDiscovery ? new DiscoveryResult(true, "skip", null) : DiscoveryResult.None();
        if (Live.Count > 1 && ModeOfMethod(preferredMethod) is { } pm && Live.FirstOrDefault(p => p.Mode == pm) is { } first)
        {
            var r = await first.ProbeAsync(target, ct).ConfigureAwait(false);
            if (r.Found || ct.IsCancellationRequested) return r;
        }
        var raced = await ProbeKit.RaceAsync(Live.Select<IDiscoveryProbe, Func<CancellationToken, Task<DiscoveryResult>>>(
            p => token => p.ProbeAsync(target, token)).ToList(), "", ct).ConfigureAwait(false);
        return !raced.Found && discovery && SkipDiscovery && !ct.IsCancellationRequested ? new DiscoveryResult(true, "skip", null) : raced;
    }

    /// <summary>Synchronous form for the engine's blocking worker threads.</summary>
    public PingResult Probe(string target, string? preferredMethod, CancellationToken ct, bool discovery = false)
    {
        var r = ProbeAsync(target, preferredMethod, ct, discovery).GetAwaiter().GetResult();
        return new PingResult(r.Found, r.RttMs, r.Ttl, r.Found ? r.Method : null);
    }
}
