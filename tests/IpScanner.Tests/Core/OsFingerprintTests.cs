using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Os;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Answers the three fingerprint probes the way a given TCP/IP stack would.</summary>
internal sealed class StackProfileLink : IRawLink
{
    public sealed record Profile(int Ttl, int OpenWindow, byte[] OpenOptions, int ClosedWindow, bool ClosedAck, bool Df = true);

    public static readonly Profile Windows10 = new(128, 64240, RawPackets.ProbeTcpOptions, 0, true);
    public static readonly Profile Windows7 = new(128, 8192, RawPackets.ProbeTcpOptions, 0, true);
    public static readonly Profile Linux = new(64, 64240,
        new byte[] { 2, 4, 5, 0xB4, 4, 2, 8, 10, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 3, 7 }, 0, true);
    public static readonly Profile Router = new(255, 4128, new byte[] { 2, 4, 5, 0xB4 }, 0, true, false);

    private readonly RawRoute _route;
    private readonly Profile _p;
    private readonly int[] _openPorts;
    private readonly Queue<byte[]> _inbox = new();
    public readonly List<ParsedPacket> Sent = new();

    public StackProfileLink(RawRoute route, Profile profile, params int[] openPorts) { _route = route; _p = profile; _openPorts = openPorts; }

    public bool Send(byte[] frame)
    {
        var p = RawPackets.Parse(frame);
        if (p is null) return true;
        Sent.Add(p);
        if (p.Kind == PacketKind.Tcp && p.Flags == TcpFlags.Syn)
        {
            bool open = _openPorts.Contains(p.DstPort);
            _inbox.Enqueue(RawPackets.BuildTcp(_route.NextHopMac, _route.SrcMac, p.DstIp!, p.SrcIp!, p.DstPort, p.SrcPort, 5000, p.Seq + 1,
                open ? TcpFlags.Syn | TcpFlags.Ack : (_p.ClosedAck ? TcpFlags.Rst | TcpFlags.Ack : TcpFlags.Rst),
                open ? _p.OpenWindow : _p.ClosedWindow, open ? _p.OpenOptions : null, _p.Ttl - 3, 0, _p.Df));
        }
        else if (p.Kind == PacketKind.Icmp && p.IcmpType == 8)
        {
            var reply = RawPackets.BuildIcmpEcho(_route.NextHopMac, _route.SrcMac, p.DstIp!, p.SrcIp!, p.IcmpId, p.IcmpSeq, p.Payload, _p.Ttl - 3, 0, _p.Df);
            reply[14 + 20] = 0;
            _inbox.Enqueue(reply);
        }
        return true;
    }

    public byte[]? Receive(int timeoutMs)
    {
        if (_inbox.Count > 0) return _inbox.Dequeue();
        Thread.Sleep(Math.Min(timeoutMs, 5));
        return null;
    }

    public void Dispose() { }
}

[Collection("PacketDriverState")]
public class OsFingerprintTests
{
    private const string Host = "10.0.0.7";

    private static async Task<OsEstimate?> Run(StackProfileLink.Profile profile, params int[] open)
    {
        var net = new FakeDiscoveryNet();
        net.Link = (_, _) => new StackProfileLink(net.Route!, profile, open);
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "1.0"), elevated: true);
        try { return await OsFingerprinter.EstimateAsync(net, Host, open, 600, default); }
        finally { PacketDriver.OverrideForTests(null); }
    }

    [Fact]
    public async Task Windows10Stack_IsMatchedByName()
    {
        var e = await Run(StackProfileLink.Windows10, 445);
        Assert.NotNull(e);
        Assert.Equal(OsFamily.Windows, e!.Family);
        Assert.True(e.FromFingerprint);
        Assert.StartsWith("Windows 10", e.Match);
        Assert.Equal(100, e.MatchPercent);
        Assert.InRange(e.Percent, 60, 95);
    }

    [Fact]
    public async Task Windows7Stack_IsToldApartFromWindows10ByWindowSize()
    {
        var e = await Run(StackProfileLink.Windows7, 445);
        Assert.StartsWith("Windows 7", e!.Match);
    }

    [Fact]
    public async Task LinuxStack_IsMatched()
    {
        var e = await Run(StackProfileLink.Linux, 22);
        Assert.Equal(OsFamily.UnixLike, e!.Family);
        Assert.StartsWith("Linux 4", e.Match);
    }

    [Fact]
    public async Task RouterStack_IsNetworkGear()
    {
        var e = await Run(StackProfileLink.Router, 23);
        Assert.Equal(OsFamily.NetworkGear, e!.Family);
    }

    [Fact]
    public async Task ClosedPortAndEchoAlone_StillGiveAnEstimateWithLowerConfidence()
    {
        var full = (await Run(StackProfileLink.Windows10, 445))!;
        var partial = (await Run(StackProfileLink.Windows10))!;
        Assert.Equal(OsFamily.Windows, partial.Family);
        Assert.True(partial.Percent < full.Percent);
    }

    [Fact]
    public void UnknownStack_FindsNoCloseMatch()
    {
        var s = new OsFingerprintSample
        {
            OpenTtl = 100, OpenWindow = 1234, OpenOptions = "NOP,SACK,NOP,NOP,NOP", OpenDf = false, OpenWindowScale = 2,
            ClosedRstAck = false, ClosedWindow = 99,
        };
        Assert.Null(OsFingerprintMatcher.Match(s));
    }

    /// <summary>A gateway answer seen live: Linux-like TTL, flags and options, but a window and scale no table entry lists.</summary>
    private static readonly OsFingerprintSample UnknownLinuxLike = new()
    {
        OpenTtl = 64, OpenWindow = 42340, OpenOptions = "MSS,NOP,NOP,SACK,NOP,WS", OpenDf = true, OpenWindowScale = 12,
        ClosedTtl = 64, ClosedRstAck = true, ClosedWindow = 0, EchoTtl = 64, EchoDf = false,
    };

    [Fact]
    public void ApproximateStack_StaysBelowTheCloseMatchBar_AsFourUnrelatedSignaturesTieOnIt()
    {
        // Closeness is 0.58 against a 0.6 bar, but Linux, Android and BSD all score the same: the numbers alone cannot
        // name one of them, so the bar stays where it is.
        Assert.Null(OsFingerprintMatcher.Match(UnknownLinuxLike));
        var best = OsSignatures.All.Select(sig => OsFingerprintMatcher.Distance(UnknownLinuxLike, sig, out _)).OrderBy(d => d).ToList();
        Assert.True(best.Count(d => d == best[0]) >= 4);
    }

    [Fact]
    public async Task UnknownStack_FallsBackToTheHeuristicWithTheProbedTtl()
    {
        var profile = new StackProfileLink.Profile(64, 42340,
            new byte[] { 2, 4, 5, 0xB4, 1, 1, 4, 2, 1, 3, 3, 12 }, 0, true);
        var e = await Run(profile, 443);
        Assert.NotNull(e);
        Assert.False(e!.FromFingerprint);
        Assert.Equal(OsFamily.UnixLike, e.Family);
    }

    [Fact]
    public async Task Probe_SendsSynToOpenAndClosedPortAndEchoWithDefinedPayload_ThenResets()
    {
        StackProfileLink? link = null;
        var net = new FakeDiscoveryNet();
        net.Link = (_, _) => link = new StackProfileLink(net.Route!, StackProfileLink.Linux, 22);
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "1.0"), elevated: true);
        try { await OsFingerprinter.EstimateAsync(net, Host, new[] { 22 }, 600, default); }
        finally { PacketDriver.OverrideForTests(null); }

        Assert.NotNull(link);
        var syns = link!.Sent.Where(p => p.Kind == PacketKind.Tcp && p.Flags == TcpFlags.Syn).Select(p => p.DstPort).ToList();
        Assert.Contains(22, syns);
        Assert.Contains(1, syns);
        var echo = Assert.Single(link.Sent, p => p.Kind == PacketKind.Icmp);
        Assert.Equal(RawPackets.FingerprintEchoPayload, echo.Payload);
        Assert.Contains(link.Sent, p => p.Kind == PacketKind.Tcp && p.Flags == TcpFlags.Rst && p.DstPort == 22);
    }

    [Fact]
    public async Task Probe_WithEveryClosedCandidateOpen_SendsNoClosedPortSynAndDoesNotThrow()
    {
        StackProfileLink? link = null;
        var net = new FakeDiscoveryNet();
        var open = new[] { 1, 9, 33433 };
        net.Link = (_, _) => link = new StackProfileLink(net.Route!, StackProfileLink.Linux, open);
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "1.0"), elevated: true);
        try { await OsFingerprinter.EstimateAsync(net, Host, open, 600, default); }
        finally { PacketDriver.OverrideForTests(null); }

        Assert.NotNull(link);
        var syns = link!.Sent.Where(s => s.Kind == PacketKind.Tcp && s.Flags == TcpFlags.Syn).Select(s => s.DstPort).Distinct().ToList();
        Assert.All(syns, port => Assert.Contains(port, open));
    }

    [Fact]
    public async Task WithoutDriver_NothingIsSent_AndTheHeuristicStaysInCharge()
    {
        var net = new FakeDiscoveryNet();
        PacketDriver.OverrideForTests(new PacketDriverStatus(false, "missing"));
        try
        {
            Assert.False(OsFingerprinter.Available);
            var e = await OsFingerprinter.EstimateAsync(net, Host, new[] { 445 }, 300, default);
            Assert.Null(e);
            Assert.Equal(0, net.Calls);
        }
        finally { PacketDriver.OverrideForTests(null); }

        var dev = new IpScanner.Core.Models.Device("10.0.0.7");
        var est = OsEstimator.For(dev, new[] { 445, 3389 });
        Assert.Equal(OsFamily.Windows, est.Family);
        Assert.False(est.FromFingerprint);
    }

    [Fact]
    public void TokenDistance_CountsEachShiftedOrMissingOption()
    {
        Assert.Equal(0, OsFingerprintMatcher.TokenDistance("MSS,NOP,WS", "MSS,NOP,WS"));
        Assert.Equal(1, OsFingerprintMatcher.TokenDistance("MSS,NOP,WS", "MSS,WS"));
        Assert.Equal(1, OsFingerprintMatcher.TokenDistance("MSS,SACK,TS", "MSS,SACK,NOP"));
    }

    [Fact]
    public void SignatureTable_StatesItIsSmall()
    {
        Assert.Contains("not a full database", OsSignatures.Note);
        Assert.InRange(OsSignatures.All.Count, 5, 40);
    }
}
