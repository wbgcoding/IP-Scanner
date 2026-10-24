using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

// Touches the process-wide packet driver override and transport factory.
[Collection("PortScannerGate")]
public class WpcapRawTransportTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress Target = IPAddress.Parse("192.168.1.20");
    private static readonly byte[] MacLocal = { 2, 0, 0, 0, 0, 1 };
    private static readonly byte[] MacHop = { 2, 0, 0, 0, 0, 2 };
    private static readonly RawRoute Route = new(@"\Device\NPF_{TEST}", MacLocal, Local, MacHop);

    /// <summary>A capture handle that records sent frames and answers through a callback.</summary>
    private sealed class FakeLink : IRawLink
    {
        private readonly BlockingCollection<byte[]> _inbox = new();
        private readonly Func<ParsedPacket, IEnumerable<byte[]>> _responder;
        public readonly List<byte[]> Sent = new();
        public bool FailSend;
        public volatile bool Disposed;

        public FakeLink(Func<ParsedPacket, IEnumerable<byte[]>> responder) => _responder = responder;

        public bool Send(byte[] frame)
        {
            lock (Sent) Sent.Add(frame);
            if (FailSend) return false;
            foreach (var reply in _responder(RawPackets.Parse(frame)!)) _inbox.Add(reply);
            return true;
        }

        public byte[]? Receive(int timeoutMs) => _inbox.TryTake(out var f, timeoutMs) ? f : null;

        public void Dispose() => Disposed = true;
    }

    private static readonly IEnumerable<byte[]> Silence = Array.Empty<byte[]>();

    private static byte[] ReplyFrame(ParsedPacket probe, TcpFlags flags)
        => RawPackets.BuildTcp(MacHop, MacLocal, probe.DstIp!, probe.SrcIp!, probe.DstPort, probe.SrcPort, 1, probe.Seq + 1, flags, 1024);

    private static byte[] Probe(int srcPort, int dstPort)
        => RawPackets.StripEthernet(RawPackets.BuildTcp(MacLocal, MacHop, Local, Target, srcPort, dstPort, 100, 0, TcpFlags.Syn, 1024))!;

    private static Func<byte[], bool> Answer(int srcPort, int dstPort) => ip =>
        RawPackets.ParseIp(ip) is { } r && RawPacketParser.Answers(r, Target, dstPort, srcPort);

    [Fact]
    public void RegisterDriverTransport_OnlyWithAUsableDriver()
    {
        var old = RawTransportProvider.Factory;
        try
        {
            RawTransportProvider.Factory = null;
            PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
            RawTransportProvider.RegisterDriverTransport();
            Assert.Null(RawTransportProvider.Factory);
            Assert.False(ScanTypes.Availability(ScanType.Syn).Available);

            PacketDriver.OverrideForTests(new PacketDriverStatus(true, null));
            RawTransportProvider.RegisterDriverTransport();
            Assert.NotNull(RawTransportProvider.Factory);
            foreach (var type in ScanTypes.All)
                Assert.True(ScanTypes.Availability(type).Available, type.ToString());
        }
        finally
        {
            RawTransportProvider.Factory = old;
            PacketDriver.OverrideForTests(null);
        }
    }

    [Fact]
    public void Create_OpensTheRoutedDeviceWithATargetFilter()
    {
        string? device = null, filter = null;
        var link = new FakeLink(_ => Silence);
        using var transport = WpcapRawTransport.Create(Target, _ => Route, (d, f) => { device = d; filter = f; return link; })!;
        Assert.Equal(Route.DeviceName, device);
        Assert.Contains("src host 192.168.1.20", filter);
        Assert.Contains("dst host 192.168.1.10", filter);
        Assert.Equal(Local, transport.LocalAddress);
    }

    [Fact]
    public void Create_ReturnsNullWithoutRouteOrHandle()
    {
        Assert.Null(WpcapRawTransport.Create(Target, _ => null, (_, _) => new FakeLink(_ => Silence)));
        Assert.Null(WpcapRawTransport.Create(Target, _ => Route, (_, _) => null));
    }

    [Fact]
    public async Task Exchange_SendsAnEthernetFrameToTheNextHopAndReturnsTheIpReply()
    {
        var link = new FakeLink(p => new[] { ReplyFrame(p, TcpFlags.Syn | TcpFlags.Ack) });
        using var transport = new WpcapRawTransport(link, Route);
        var probe = Probe(41000, 80);

        var reply = await transport.ExchangeAsync(probe, Answer(41000, 80), 1000, default);

        Assert.NotNull(reply);
        byte[] sent;
        lock (link.Sent) sent = Assert.Single(link.Sent);
        Assert.Equal(MacHop, sent[..6]);
        Assert.Equal(MacLocal, sent[6..12]);
        Assert.Equal(probe, sent[14..]);
        var r = RawPackets.ParseIp(reply!)!;
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, r.Flags);
    }

    [Fact]
    public async Task Exchange_ParallelProbesEachGetTheirOwnReply()
    {
        // The answers arrive in the reverse order of the probes; each exchange must still see only its own.
        var held = new List<ParsedPacket>();
        var link = new FakeLink(p =>
        {
            lock (held)
            {
                held.Add(p);
                if (held.Count < 2) return Silence;
                return new[] { ReplyFrame(held[1], TcpFlags.Rst | TcpFlags.Ack), ReplyFrame(held[0], TcpFlags.Syn | TcpFlags.Ack) };
            }
        });
        using var transport = new WpcapRawTransport(link, Route);
        var a = transport.ExchangeAsync(Probe(41000, 80), Answer(41000, 80), 2000, default);
        var b = transport.ExchangeAsync(Probe(41001, 81), Answer(41001, 81), 2000, default);
        var ra = await a;
        var rb = await b;

        var pa = RawPackets.ParseIp(ra!)!;
        var pb = RawPackets.ParseIp(rb!)!;
        Assert.Equal((80, TcpFlags.Syn | TcpFlags.Ack), (pa.SrcPort, pa.Flags));
        Assert.Equal((81, TcpFlags.Rst | TcpFlags.Ack), (pb.SrcPort, pb.Flags));
    }

    [Fact]
    public async Task Exchange_SilenceReturnsNullAfterTheTimeout()
    {
        var link = new FakeLink(_ => Silence);
        using var transport = new WpcapRawTransport(link, Route);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await transport.ExchangeAsync(Probe(41000, 80), Answer(41000, 80), 120, default));
        Assert.InRange(watch.ElapsedMilliseconds, 80, 3000);
    }

    [Fact]
    public async Task Exchange_SendFailureThrows()
    {
        var link = new FakeLink(_ => Silence) { FailSend = true };
        using var transport = new WpcapRawTransport(link, Route);
        await Assert.ThrowsAsync<IOException>(() => transport.ExchangeAsync(Probe(41000, 80), Answer(41000, 80), 500, default));
    }

    [Fact]
    public async Task Exchange_CancelThrows()
    {
        var link = new FakeLink(_ => Silence);
        using var transport = new WpcapRawTransport(link, Route);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExchangeAsync(Probe(41000, 80), Answer(41000, 80), 5000, cts.Token));
    }

    [Fact]
    public async Task Dispose_ReleasesTheHandleAndPendingExchanges()
    {
        var link = new FakeLink(_ => Silence);
        var transport = new WpcapRawTransport(link, Route);
        var pending = transport.ExchangeAsync(Probe(41000, 80), Answer(41000, 80), 10000, default);
        transport.Dispose();
        Assert.Null(await pending);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task Engine_SynScanThroughTheRegisteredTransport_ReportsOpenAndClosed()
    {
        var old = RawTransportProvider.Factory;
        try
        {
            PacketDriver.OverrideForTests(new PacketDriverStatus(true, null));
            RawTransportProvider.Factory = t => WpcapRawTransport.Create(t, _ => Route, (_, _) => new FakeLink(p =>
                p.DstPort switch
                {
                    80 => new[] { ReplyFrame(p, TcpFlags.Syn | TcpFlags.Ack) },
                    81 => new[] { ReplyFrame(p, TcpFlags.Rst | TcpFlags.Ack) },
                    _ => Silence,
                }));
            var spec = new PortSpec(new[] { 80, 81, 82 }, Array.Empty<int>());
            var results = await PortScanEngine.RunAsync(Target.ToString(), spec,
                new ScanOptions(ScanType.Syn, Tcp: true, Udp: false), null, default);
            var byPort = results.ToDictionary(r => r.Port, r => r.State);
            Assert.Equal(PortState.Open, byPort[80]);
            Assert.Equal(PortState.Closed, byPort[81]);
            Assert.Equal(PortState.Filtered, byPort[82]);
        }
        finally
        {
            RawTransportProvider.Factory = old;
            PacketDriver.OverrideForTests(null);
        }
    }
}
