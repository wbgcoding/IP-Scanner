using System.Diagnostics;
using System.Net;
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner.Discovery;

internal static class ProbeKit
{
    public static double Ms(Stopwatch sw) => sw.Elapsed.TotalMilliseconds;

    /// <summary>Runs all attempts at once; the first one that finds the host wins and the rest are cancelled.</summary>
    public static async Task<DiscoveryResult> RaceAsync(IReadOnlyList<Func<CancellationToken, Task<DiscoveryResult>>> work,
        string noneMethod, CancellationToken ct)
    {
        if (work.Count == 0) return DiscoveryResult.None(noneMethod);
        if (work.Count == 1) return await Guard(work[0], ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = work.Select(w => Guard(w, cts.Token)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var r = await done.ConfigureAwait(false);
                if (r.Found) { cts.Cancel(); return r; }
            }
        }
        finally { cts.Cancel(); }
        return DiscoveryResult.None(noneMethod);
    }

    private static async Task<DiscoveryResult> Guard(Func<CancellationToken, Task<DiscoveryResult>> w, CancellationToken ct)
    {
        try { return await w(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return DiscoveryResult.None(); }
    }
}

public sealed class IcmpEchoProbe : IDiscoveryProbe
{
    private readonly Func<string, int, PingResult> _ping;
    private readonly int _timeoutMs;
    public IcmpEchoProbe(Func<string, int, PingResult> ping, int timeoutMs) { _ping = ping; _timeoutMs = timeoutMs; }
    public DiscoveryMode Mode => DiscoveryMode.IcmpEcho;

    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
        => Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return DiscoveryResult.None("icmp");
            var r = _ping(target, _timeoutMs);
            return new DiscoveryResult(r.Success, "icmp", r.LatencyMs, r.Ttl);
        }, ct);
}

public sealed class ArpProbe : IDiscoveryProbe
{
    private readonly IDiscoveryNet _net;
    public ArpProbe(IDiscoveryNet net) { _net = net; }
    public DiscoveryMode Mode => DiscoveryMode.Arp;

    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
        => Task.Run(() =>
        {
            if (ct.IsCancellationRequested || !_net.IsOnLink(target)) return DiscoveryResult.None("arp");
            var sw = Stopwatch.StartNew();
            return _net.ArpResolve(target) is null ? DiscoveryResult.None("arp") : new DiscoveryResult(true, "arp", ProbeKit.Ms(sw));
        }, ct);
}

public sealed class IcmpRawProbe : IDiscoveryProbe
{
    private readonly IDiscoveryNet _net;
    private readonly int _timeoutMs;
    private readonly byte _type;
    private readonly string _method;

    public IcmpRawProbe(IDiscoveryNet net, DiscoveryMode mode, int timeoutMs)
    {
        if (mode is not (DiscoveryMode.IcmpTimestamp or DiscoveryMode.IcmpMask)) throw new ArgumentOutOfRangeException(nameof(mode));
        _net = net; _timeoutMs = timeoutMs; Mode = mode;
        _type = mode == DiscoveryMode.IcmpTimestamp ? (byte)13 : (byte)17;
        _method = DiscoveryModes.KeyOf(mode);
    }

    public DiscoveryMode Mode { get; }

    public async Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        bool ok = await _net.IcmpRawAsync(target, _type, _timeoutMs, ct).ConfigureAwait(false);
        return ok ? new DiscoveryResult(true, _method, ProbeKit.Ms(sw)) : DiscoveryResult.None(_method);
    }
}

public sealed class TcpConnectProbe : IDiscoveryProbe
{
    private readonly IDiscoveryNet _net;
    private readonly int[] _ports;
    private readonly int _timeoutMs;
    public TcpConnectProbe(IDiscoveryNet net, IEnumerable<int> ports, int timeoutMs) { _net = net; _ports = ports.ToArray(); _timeoutMs = timeoutMs; }
    public DiscoveryMode Mode => DiscoveryMode.TcpConnect;

    /// <summary>A completed handshake and a refused connection both prove the host is up.</summary>
    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
        => ProbeKit.RaceAsync(_ports.Select<int, Func<CancellationToken, Task<DiscoveryResult>>>(port => async token =>
        {
            var sw = Stopwatch.StartNew();
            var outcome = await _net.TcpConnectAsync(target, port, _timeoutMs, token).ConfigureAwait(false);
            return outcome == TcpOutcome.NoAnswer ? DiscoveryResult.None("tcp") : new DiscoveryResult(true, "tcp:" + port, ProbeKit.Ms(sw));
        }).ToList(), "tcp", ct);
}

public sealed class UdpPingProbe : IDiscoveryProbe
{
    private readonly IDiscoveryNet _net;
    private readonly int[] _ports;
    private readonly int _timeoutMs;
    public UdpPingProbe(IDiscoveryNet net, IEnumerable<int> ports, int timeoutMs) { _net = net; _ports = ports.ToArray(); _timeoutMs = timeoutMs; }
    public DiscoveryMode Mode => DiscoveryMode.Udp;

    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
        => ProbeKit.RaceAsync(_ports.Select<int, Func<CancellationToken, Task<DiscoveryResult>>>(port => async token =>
        {
            var sw = Stopwatch.StartNew();
            bool up = await _net.UdpExchangeAsync(target, port, DiscoveryPayloads.For(port), _timeoutMs, token).ConfigureAwait(false);
            return up ? new DiscoveryResult(true, "udp:" + port, ProbeKit.Ms(sw)) : DiscoveryResult.None("udp");
        }).ToList(), "udp", ct);
}

/// <summary>SYN or ACK probes sent through the packet driver (the OS refuses to send these itself). A SYN/ACK or a
/// reset coming back proves the host is up.</summary>
public sealed class RawTcpPingProbe : IDiscoveryProbe
{
    private readonly IDiscoveryNet _net;
    private readonly int[] _ports;
    private readonly int _timeoutMs;
    private readonly TcpFlags _flags;
    private readonly string _prefix;

    public RawTcpPingProbe(IDiscoveryNet net, DiscoveryMode mode, IEnumerable<int> ports, int timeoutMs)
    {
        if (mode is not (DiscoveryMode.TcpSyn or DiscoveryMode.TcpAck)) throw new ArgumentOutOfRangeException(nameof(mode));
        _net = net; Mode = mode; _ports = ports.ToArray(); _timeoutMs = timeoutMs;
        _flags = mode == DiscoveryMode.TcpSyn ? TcpFlags.Syn : TcpFlags.Ack;
        _prefix = DiscoveryModes.KeyOf(mode);
    }

    public DiscoveryMode Mode { get; }

    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct)
        => Task.Run(() => Probe(target, ct), ct);

    private DiscoveryResult Probe(string target, CancellationToken ct)
    {
        var none = DiscoveryResult.None(_prefix);
        if (!IPAddress.TryParse(target, out var dst) || _ports.Length == 0) return none;
        var route = _net.RouteTo(dst);
        if (route is null) return none;
        int srcPort = Random.Shared.Next(40000, 60000);
        uint seq = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        using var link = _net.OpenLink(route.DeviceName, $"tcp and src host {dst} and dst port {srcPort}");
        if (link is null) return none;

        var sw = Stopwatch.StartNew();
        foreach (int port in _ports)
        {
            var frame = RawPackets.BuildTcp(route.SrcMac, route.NextHopMac, route.SrcIp, dst, srcPort, port, seq,
                _flags == TcpFlags.Ack ? 1u : 0u, _flags, 1024, _flags == TcpFlags.Syn ? RawPackets.ProbeTcpOptions : null);
            if (!link.Send(frame)) return none;
        }
        while (sw.ElapsedMilliseconds < _timeoutMs && !ct.IsCancellationRequested)
        {
            var raw = link.Receive((int)Math.Min(100, Math.Max(1, _timeoutMs - sw.ElapsedMilliseconds)));
            if (raw is null) continue;
            var p = RawPackets.Parse(raw);
            if (p is not { Kind: PacketKind.Tcp } || p.SrcIp is null || !p.SrcIp.Equals(dst) || p.DstPort != srcPort) continue;
            if (!p.Flags.HasFlag(TcpFlags.Rst) && !(p.Flags.HasFlag(TcpFlags.Syn) && p.Flags.HasFlag(TcpFlags.Ack))) continue;
            return new DiscoveryResult(true, $"{_prefix}:{p.SrcPort}", ProbeKit.Ms(sw), p.Ttl);
        }
        return none;
    }
}

/// <summary>Nothing is sent: the targets are only listed (and named by the engine).</summary>
public sealed class ListScanProbe : IDiscoveryProbe
{
    public DiscoveryMode Mode => DiscoveryMode.ListScan;
    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct) => Task.FromResult(DiscoveryResult.None("list"));
}

/// <summary>Treats every target as up without sending anything.</summary>
public sealed class SkipDiscoveryProbe : IDiscoveryProbe
{
    public DiscoveryMode Mode => DiscoveryMode.SkipDiscovery;
    public Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct) => Task.FromResult(new DiscoveryResult(true, "skip", null));
}
