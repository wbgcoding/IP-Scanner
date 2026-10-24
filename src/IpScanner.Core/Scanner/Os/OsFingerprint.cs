using System.Diagnostics;
using System.Net;
using IpScanner.Core.Scanner.Discovery;

namespace IpScanner.Core.Scanner.Os;

/// <summary>What the active probes saw. Every field is optional: a probe that got no reply leaves its fields empty.</summary>
public sealed record OsFingerprintSample
{
    /// <summary>SYN/ACK from an open port.</summary>
    public int? OpenTtl { get; init; }
    public int? OpenWindow { get; init; }
    public string? OpenOptions { get; init; }
    public bool? OpenDf { get; init; }
    public int OpenWindowScale { get; init; } = -1;
    /// <summary>Answer from a closed port.</summary>
    public int? ClosedTtl { get; init; }
    public bool? ClosedRstAck { get; init; }
    public int? ClosedWindow { get; init; }
    /// <summary>Echo reply to the probe with the defined payload.</summary>
    public int? EchoTtl { get; init; }
    public bool? EchoDf { get; init; }
    public bool? EchoPayloadIntact { get; init; }

    public int? AnyTtl => OpenTtl ?? ClosedTtl ?? EchoTtl;
}

/// <summary>One entry of the embedded signature table.</summary>
internal sealed record OsSignature(string Name, OsFamily Family, int Ttl, int[] Windows, string Options, int[] WindowScales,
                                   bool Df, bool? ClosedRstAck, bool? ClosedWindowZero);

/// <summary>Small, hand-written signature table for the common stack families. It is not a database: unknown stacks
/// simply find no close match and the heuristic stays in charge.</summary>
internal static class OsSignatures
{
    public const string Note = "Small built-in table of common stack families, not a full database.";

    public static readonly IReadOnlyList<OsSignature> All = new[]
    {
        new OsSignature("Windows 10 / 11 / Server 2016+", OsFamily.Windows, 128, new[] { 64240, 65535 },
            "MSS,NOP,WS,NOP,NOP,SACK", new[] { 8 }, true, true, true),
        new OsSignature("Windows 7 / 8 / Server 2008-2012", OsFamily.Windows, 128, new[] { 8192 },
            "MSS,NOP,WS,NOP,NOP,SACK", new[] { 8 }, true, true, true),
        new OsSignature("Windows XP / Server 2003", OsFamily.Windows, 128, new[] { 64240, 65535, 16384 },
            "MSS,NOP,NOP,SACK", new[] { -1 }, true, true, true),
        new OsSignature("Linux 4.x - 6.x", OsFamily.UnixLike, 64, new[] { 29200, 64240, 65160 },
            "MSS,SACK,TS,NOP,WS", new[] { 7 }, true, true, true),
        new OsSignature("Linux 2.6 - 3.x", OsFamily.UnixLike, 64, new[] { 5840, 14600 },
            "MSS,SACK,TS,NOP,WS", new[] { 5, 6, 7 }, true, true, true),
        new OsSignature("Android", OsFamily.UnixLike, 64, new[] { 65535, 65700 },
            "MSS,SACK,TS,NOP,WS", new[] { 6, 8, 9 }, true, true, true),
        new OsSignature("macOS / iOS", OsFamily.UnixLike, 64, new[] { 65535 },
            "MSS,NOP,WS,NOP,NOP,TS,SACK,EOL", new[] { 5, 6 }, true, true, true),
        new OsSignature("FreeBSD / OpenBSD", OsFamily.UnixLike, 64, new[] { 65535, 16384 },
            "MSS,NOP,WS,SACK,TS", new[] { 6, 3 }, true, true, true),
        new OsSignature("Router or switch (TTL 255 stack)", OsFamily.NetworkGear, 255, new[] { 4128, 16384, 8192 },
            "MSS", new[] { -1 }, false, true, true),
        new OsSignature("Embedded stack, MSS only (printer, camera)", OsFamily.Printer, 64, new[] { 5744, 5840, 2920, 1460 },
            "MSS", new[] { -1 }, false, null, null),
    };
}

/// <summary>Weighted comparison of a sample against the signature table.</summary>
public static class OsFingerprintMatcher
{
    private const int WTtl = 40, WOptions = 40, WWindow = 15, WDf = 10, WScale = 10, WClosedFlags = 8, WClosedWindow = 7;
    private const int WFull = WTtl + WOptions + WWindow + WDf + WScale + WClosedFlags + WClosedWindow;
    internal const int MinPercent = 30;
    internal const double MinCloseness = 0.6;
    internal const int MaxPercent = 95;

    public static OsEstimate? Match(OsFingerprintSample s)
    {
        if (s.AnyTtl is null) return null;
        var ranked = OsSignatures.All.Select(sig => (sig, d: Distance(s, sig, out int w), w))
            .OrderBy(x => x.d).ToList();
        var best = ranked[0];
        double closeness = 1.0 - (double)best.d / best.w;
        if (closeness < MinCloseness) return null;
        double coverage = (double)best.w / WFull;
        int percent = (int)Math.Round(MaxPercent * closeness * (0.5 + 0.5 * coverage));
        var rival = ranked.Skip(1).FirstOrDefault(x => x.sig.Family != best.sig.Family);
        if (rival.sig is not null && rival.d - best.d < 5) percent -= 15;
        percent = Math.Min(MaxPercent, percent);
        if (percent < MinPercent) return null;
        // The reply TTL counts down from the stack's start value, so a match on that value also gives the hop count.
        int? hops = s.AnyTtl is { } raw && OsHeuristic.InitialTtl(raw) == best.sig.Ttl ? best.sig.Ttl - raw : null;
        return new OsEstimate(best.sig.Family, percent, true, best.sig.Name, (int)Math.Round(100 * closeness), hops);
    }

    internal static int Distance(OsFingerprintSample s, OsSignature sig, out int compared)
    {
        int d = 0; compared = 0;
        if (s.AnyTtl is { } rawTtl)
        {
            compared += WTtl;
            if (OsHeuristic.InitialTtl(rawTtl) != sig.Ttl) d += WTtl;
        }
        if (s.OpenOptions is not null)
        {
            compared += WOptions;
            d += Math.Min(WOptions, 10 * TokenDistance(s.OpenOptions, sig.Options));
        }
        if (s.OpenWindow is { } win)
        {
            compared += WWindow;
            if (!sig.Windows.Contains(win)) d += WWindow;
        }
        bool? df = s.OpenDf ?? s.EchoDf;
        if (df is { } flag)
        {
            compared += WDf;
            if (flag != sig.Df) d += WDf;
        }
        if (s.OpenOptions is not null)
        {
            compared += WScale;
            if (!sig.WindowScales.Contains(s.OpenWindowScale)) d += WScale;
        }
        if (s.ClosedRstAck is { } ra && sig.ClosedRstAck is { } expectRa)
        {
            compared += WClosedFlags;
            if (ra != expectRa) d += WClosedFlags;
        }
        if (s.ClosedWindow is { } cw && sig.ClosedWindowZero is { } expectZero)
        {
            compared += WClosedWindow;
            if ((cw == 0) != expectZero) d += WClosedWindow;
        }
        return d;
    }

    /// <summary>Edit distance over option tokens, so a shifted or missing option costs one step each.</summary>
    internal static int TokenDistance(string a, string b)
    {
        var x = a.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var row = Enumerable.Range(0, y.Length + 1).ToArray();
        for (int i = 1; i <= x.Length; i++)
        {
            int diag = row[0];
            row[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (x[i - 1] == y[j - 1] ? 0 : 1));
                diag = up;
            }
        }
        return row[y.Length];
    }
}

/// <summary>Active probing: a SYN to an open and to a closed port plus an echo with a defined payload. Needs the
/// packet driver; without it the caller keeps the heuristic.</summary>
public static class OsFingerprinter
{
    private static readonly int[] ClosedCandidates = { 1, 9, 33433 };
    private static readonly int[] OpenPreference = { 443, 80, 22, 445, 8080, 3389 };

    public static bool Available => Capabilities.Evaluate(Capability.OsFingerprint).Available;

    /// <summary>Set from the settings: whether opening a device may run the active fingerprint.</summary>
    public static bool Enabled { get; set; }
    public static IDiscoveryNet Net { get; set; } = SystemDiscoveryNet.Instance;
    public static int TimeoutMs { get; set; } = 1500;

    public static Task<OsEstimate?> EstimateAsync(IDiscoveryNet net, string ip, IReadOnlyCollection<int> openPorts,
        int timeoutMs, CancellationToken ct)
        => Available ? Task.Run(() => Estimate(net, ip, openPorts, timeoutMs, ct), ct) : Task.FromResult<OsEstimate?>(null);

    internal static OsEstimate? Estimate(IDiscoveryNet net, string ip, IReadOnlyCollection<int> openPorts, int timeoutMs, CancellationToken ct)
    {
        var sample = Probe(net, ip, openPorts, timeoutMs, ct);
        if (sample is null) return null;
        if (OsFingerprintMatcher.Match(sample) is { } match) return match;
        // No close signature (a stack the small table does not know): the reply TTL the probe just measured still feeds
        // the heuristic, which matters when the scan skipped the ping and so has no TTL of its own.
        var guess = OsHeuristic.Estimate(new OsInputs(sample.AnyTtl, openPorts));
        return guess.IsKnown ? guess : null;
    }

    internal static OsFingerprintSample? Probe(IDiscoveryNet net, string ip, IReadOnlyCollection<int> openPorts, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var dst) || dst.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var route = net.RouteTo(dst);
        if (route is null) return null;
        int? openPort = OpenPreference.Where(openPorts.Contains).Cast<int?>().FirstOrDefault()
                        ?? openPorts.Where(p => p is > 0 and < 65536).Cast<int?>().FirstOrDefault();
        int? closedPort = ClosedCandidates.Where(p => !openPorts.Contains(p)).Cast<int?>().FirstOrDefault();
        int srcPort = Random.Shared.Next(40000, 60000);
        int echoId = Random.Shared.Next(1, 65535);
        uint seq = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        using var link = net.OpenLink(route.DeviceName,
            $"src host {dst} and ((tcp and dst port {srcPort}) or (icmp and icmp[icmptype] = icmp-echoreply))");
        if (link is null) return null;

        if (openPort is { } op)
            link.Send(RawPackets.BuildTcp(route.SrcMac, route.NextHopMac, route.SrcIp, dst, srcPort, op, seq, 0, TcpFlags.Syn, 1024, RawPackets.ProbeTcpOptions));
        if (closedPort is { } cp)
            link.Send(RawPackets.BuildTcp(route.SrcMac, route.NextHopMac, route.SrcIp, dst, srcPort, cp, seq, 0, TcpFlags.Syn, 1024, RawPackets.ProbeTcpOptions));
        link.Send(RawPackets.BuildIcmpEcho(route.SrcMac, route.NextHopMac, route.SrcIp, dst, echoId, 1));

        ParsedPacket? open = null, closed = null, echo = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            if ((openPort is null || open is not null) && (closedPort is null || closed is not null) && echo is not null) break;
            var raw = link.Receive((int)Math.Min(100, Math.Max(1, timeoutMs - sw.ElapsedMilliseconds)));
            if (raw is null) continue;
            var p = RawPackets.Parse(raw);
            if (p?.SrcIp is null || !p.SrcIp.Equals(dst)) continue;
            if (p.Kind == PacketKind.Tcp && p.DstPort == srcPort)
            {
                if (p.Flags.HasFlag(TcpFlags.Syn) && p.Flags.HasFlag(TcpFlags.Ack) && p.SrcPort == openPort) open ??= p;
                else if (p.Flags.HasFlag(TcpFlags.Rst) && p.SrcPort == closedPort) closed ??= p;
            }
            else if (p.Kind == PacketKind.Icmp && p.IcmpType == 0 && p.IcmpId == echoId) echo ??= p;
        }
        // Tear the half-open connection down so the target does not keep it around.
        if (open is not null && openPort is { } port)
            link.Send(RawPackets.BuildTcp(route.SrcMac, route.NextHopMac, route.SrcIp, dst, srcPort, port, seq + 1, 0, TcpFlags.Rst, 0));

        if (open is null && closed is null && echo is null) return null;
        return new OsFingerprintSample
        {
            OpenTtl = open?.Ttl, OpenWindow = open?.Window, OpenOptions = open?.TcpOptions, OpenDf = open?.DontFragment,
            OpenWindowScale = open?.WindowScale ?? -1,
            ClosedTtl = closed?.Ttl, ClosedRstAck = closed is null ? null : closed.Flags.HasFlag(TcpFlags.Ack), ClosedWindow = closed?.Window,
            EchoTtl = echo?.Ttl, EchoDf = echo?.DontFragment,
            EchoPayloadIntact = echo is null ? null : echo.Payload.AsSpan().SequenceEqual(RawPackets.FingerprintEchoPayload),
        };
    }
}
