using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

// These tests reconfigure and measure the process-wide probe gate; any other test that probes a
// port in parallel would skew the peak counter, so this collection runs alone.
[CollectionDefinition("PortScannerGate", DisableParallelization = true)]
public class PortScannerGateCollection { }

[Collection("PortScannerGate")]
public class PortScannerTests
{
    /// <summary>The engine over explicit port lists, reduced to the open services like the tool does.</summary>
    private static async Task<IReadOnlyList<NetService>> RunCustomAsync(
        string ip, IReadOnlyCollection<int> tcpPorts, IReadOnlyCollection<int> udpPorts,
        IProgress<int>? progress, CancellationToken ct, Action<NetService>? onFound = null)
    {
        var results = await PortScanEngine.RunAsync(ip, new PortSpec(tcpPorts, udpPorts), ScanOptions.Default,
            progress, ct, r => { if (r.IsOpen && r.Service is not null) onFound?.Invoke(r.Service); });
        return results.Where(r => r.IsOpen && r.Service is not null).Select(r => r.Service! with { Ip = ip })
            .OrderBy(x => x.Port).ToList();
    }

    [Fact]
    public async Task ScanRangeAsync_ThreeDevices_NeverExceedsTheSharedProbeLimit()
    {
        using var fast = new FastProbeTimeouts();
        int previous = PortScanner.MaxConcurrentProbes;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        PortScanner.ConfigureParallelism(4);
        PortScanner.ResetProbeGateMetrics();
        PortScanner.ProbeGateTestHook = ct =>
        {
            if (Interlocked.Increment(ref entered) == 4) release.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        try
        {
            await Task.WhenAll(
                PortScanner.ScanRangeAsync("127.0.0.1", 1, 3, null, default),
                PortScanner.ScanRangeAsync("127.0.0.2", 1, 3, null, default),
                PortScanner.ScanRangeAsync("127.0.0.3", 1, 3, null, default));

            Assert.Equal(4, PortScanner.PeakGlobalProbes);
        }
        finally
        {
            PortScanner.ProbeGateTestHook = null;
            PortScanner.ConfigureParallelism(previous);
        }
    }

    [Fact]
    public async Task ConfigureParallelism_DuringActiveProbes_KeepsTheExistingGlobalLimit()
    {
        int previous = PortScanner.MaxConcurrentProbes;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        PortScanner.ConfigureParallelism(2);
        PortScanner.ResetProbeGateMetrics();
        PortScanner.ProbeGateTestHook = ct =>
        {
            if (Interlocked.Increment(ref entered) == 2) PortScanner.ConfigureParallelism(4);
            return release.Task.WaitAsync(ct);
        };
        try
        {
            var scan = Task.WhenAll(
                PortScanner.ScanRangeAsync("127.0.0.1", 1, 2, null, default),
                PortScanner.ScanRangeAsync("127.0.0.2", 1, 2, null, default));
            await Task.Delay(50);
            release.TrySetResult();
            await scan;

            Assert.Equal(2, PortScanner.PeakGlobalProbes);
        }
        finally
        {
            PortScanner.ProbeGateTestHook = null;
            PortScanner.ConfigureParallelism(previous);
        }
    }

    [Fact]
    public void TcpPorts_NoAccidentalNameCollision()
    {
        // A name shared by two ports must mean the same real service:
        // an alt port for the same protocol, or one of the deliberately grouped
        // "genuinely ambiguous" HTTP buckets. Anything else sharing a name would
        // be an editing mistake - two unrelated services silently rendered
        // identically in the UI.
        var allowedToRepeat = new[]
        {
            "HTTPS", "Kerberos", "SMTP", "MS-RPC", "SSH", "IRC", "Elasticsearch",
            "VNC", "RTSP", "UPnP", "HTTP-Alt", "HTTP-Dev",
        };
        var unexpectedDuplicates = PortScanner.TcpPorts
            .GroupBy(p => p.Name)
            .Where(g => g.Count() > 1 && !allowedToRepeat.Contains(g.Key))
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(unexpectedDuplicates);
    }

    [Fact]
    public async Task ScanAsync_Udp161ValidSnmpReply_IsConfirmed()
    {
        using var fast = new FastProbeTimeouts();
        // A reply matching SNMP's own response shape (SEQUENCE containing a
        // GetResponse-PDU) proves the port, unlike a bare UDP datagram.
        using var snmp161 = new LoopbackUdpAs161();
        var listener = snmp161.Listener;
        var replyTask = Task.Run(async () =>
        {
            var received = await listener.ReceiveAsync();
            byte[] snmpReply = { 0x30, 0x03, 0xA2, 0x01, 0x00 };
            await listener.SendAsync(snmpReply, snmpReply.Length, received.RemoteEndPoint);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var found = await PortScanner.ScanAsync("127.0.0.1", cts.Token);
        await replyTask;

        var snmp = Assert.Single(found, s => s.Port == 161);
        Assert.False(snmp.IsProbable);
    }

    [Fact]
    public async Task ScanAsync_Udp161ReplyFromDifferentAddress_IsIgnored()
    {
        using var fast = new FastProbeTimeouts();
        // A UDP probe socket must reject a spoofed reply from any
        // address other than the one actually probed - otherwise another host
        // on the same LAN could fake an open port on someone else's address.
        // The real target (127.0.0.1:161) never answers; an "attacker" on a
        // second loopback address sends a perfectly valid SNMP reply anyway -
        // it must never be attributed to the probed target.
        using var snmp161 = new LoopbackUdpAs161();
        var target = snmp161.Listener;
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var spoofTask = Task.Run(async () =>
        {
            var received = await target.ReceiveAsync();
            byte[] snmpReply = { 0x30, 0x03, 0xA2, 0x01, 0x00 };
            await attacker.SendAsync(snmpReply, snmpReply.Length,
                new IPEndPoint(IPAddress.Loopback, received.RemoteEndPoint.Port));
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var found = await PortScanner.ScanAsync("127.0.0.1", cts.Token);
        await Task.WhenAny(spoofTask, Task.Delay(2000));

        Assert.DoesNotContain(found, s => s.Port == 161);
    }

    [Fact]
    public async Task ScanAsync_Udp161GarbageReply_IsProbable()
    {
        using var fast = new FastProbeTimeouts();
        // A datagram that doesn't look like an SNMP response must show as
        // Probable, not Confirmed.
        using var snmp161 = new LoopbackUdpAs161();
        var listener = snmp161.Listener;
        var replyTask = Task.Run(async () =>
        {
            var received = await listener.ReceiveAsync();
            byte[] garbage = { 0x01, 0x02, 0x03, 0x04 };
            await listener.SendAsync(garbage, garbage.Length, received.RemoteEndPoint);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var found = await PortScanner.ScanAsync("127.0.0.1", cts.Token);
        await replyTask;

        var snmp = Assert.Single(found, s => s.Port == 161);
        Assert.True(snmp.IsProbable);
    }

    [Theory]
    [InlineData(8080, "HTTP-Proxy")]
    [InlineData(3000, "HTTP-Dev")]
    [InlineData(9090, "HTTP-Admin")]
    [InlineData(80, "HTTP")]
    public void TcpPorts_RenamedHttpPortsStillOpenAndBannerGrabAsHttp(int port, string expectedName)
    {
        // Giving these ports a distinguishing name must not silently
        // disable "open in browser" or the HTTP banner grab - neither may key
        // off the literal string "HTTP".
        var entry = Assert.Single(PortScanner.TcpPorts, p => p.Port == port);
        Assert.Equal(expectedName, entry.Name);

        var svc = new NetService(entry.Name, port, NetProtocol.Tcp) { Ip = "192.168.1.1" };
        Assert.True(svc.IsOpenable);
        Assert.StartsWith("http://", svc.DisplayTarget);
    }

    [Fact]
    public async Task ScanAsync_WithAlreadyCancelledToken_StopsBeforeProbingAnyPort()
    {
        // A pre-cancelled token must stop every probe at the throttling gate,
        // before a single connection attempt starts - proves the
        // "abbrechbar" requirement without depending on real network timing.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PortScanner.ScanAsync("203.0.113.1", cts.Token));   // RFC 5737 TEST-NET, never routes
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Cancelled scan should return near-instantly, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ScanAsync_ProbesTimeOut_LeavesNoUnobservedTaskFaults()
    {
        // A timed-out connect/receive has to be cancelled, not just abandoned: an abandoned op
        // keeps running and later faults with ObjectDisposedException against a disposed client.
        var prevConnect = PortScanner.ConnectTimeoutMs;
        var prevUdp = PortScanner.UdpTimeoutMs;
        PortScanner.ConnectTimeoutMs = 80;
        PortScanner.UdpTimeoutMs = 80;

        bool unobserved = false;
        // The event is process-wide and fires on whichever test's garbage this GC collects. A reverse
        // lookup abandoned by an unrelated test is not a port scanner fault; every other fault counts.
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (!e.Exception.ToString().Contains("System.Net.Dns.", StringComparison.Ordinal))
                unobserved = true;
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            // RFC 5737 TEST-NET, never routes - every probe times out rather than refuses,
            // exercising the timeout path (not the instant-refusal path) for all probes.
            await PortScanner.ScanAsync("203.0.113.1");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
            PortScanner.ConnectTimeoutMs = prevConnect;
            PortScanner.UdpTimeoutMs = prevUdp;
        }

        Assert.False(unobserved, "A timed-out probe left an unobserved task fault.");
    }

    [Fact]
    public async Task ScanAsync_DeviceAcceptsTcpButNeverSpeaksTls_DoesNotHangForever()
    {
        using var fast = new FastProbeTimeouts();
        // A device that accepts the TCP connection on 443 but never
        // starts/finishes the TLS handshake must not leave PortScanner.ScanAsync
        // hung on that one probe. The whole scan has to complete in bounded
        // time against a real listener that connects and then goes silent.
        var listener = new TcpListener(IPAddress.Loopback, 0);   // ephemeral, so a parallel test process never collides
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var prevCustom = PortScanner.CustomTcpPorts;
        PortScanner.CustomTcpPorts = new() { (port, "HTTPS") };   // named HTTPS so the TLS handshake starts
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = client;   // accept and hold open; never send a byte
                }
            }
            catch { /* listener stopped */ }
        });

        try
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));   // test-level backstop
            var found = await RunCustomAsync("127.0.0.1", new[] { port }, Array.Empty<int>(), null, cts.Token);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"Scan should finish well within its own per-probe timeouts, took {sw.Elapsed}");
            Assert.NotNull(found);   // scan completed and returned a result, didn't hang/throw
        }
        finally
        {
            PortScanner.CustomTcpPorts = prevCustom;
            listener.Stop();
        }
    }

    [Fact]
    public async Task ScanRangeAsync_FindsAnOpenPortOutsideTheKnownList()
    {
        // The whole point of the full 1-65535 sweep (ScanFullRangeAsync,
        // a thin wrapper around this) is finding services the 189-port known
        // list doesn't cover. Uses the internal range entry point with a small
        // window so the test runs in milliseconds instead of scanning all 65535.
        var (listener, port) = OpenTcpPort();   // ephemeral, so a parallel test process never collides
        int first = port - 5, last = Math.Min(65535, port + 5);

        try
        {
            // A real Progress<T> marshals each Report onto a captured
            // SynchronizationContext (or the thread pool, absent one) - under
            // xUnit that makes delivery both async and unordered relative to
            // the concurrent probes racing the same Interlocked counter, so
            // reading "the last value received" right after await is flaky.
            // A direct IProgress<int> calls back synchronously on the probe's
            // own thread, and tracking the max survives any write-order race
            // between those concurrent callbacks.
            int maxReported = 0;
            var progress = new MaxTrackingProgress(v => InterlockedMax(ref maxReported, v));
            var found = await PortScanner.ScanRangeAsync("127.0.0.1", first, last, progress, default);

            Assert.Contains(found, s => s.Port == port);
            Assert.Equal(last - first + 1, maxReported);   // progress ran to completion (window inclusive)
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ScanRangeAsync_OnFoundFiresForEveryOpenPortAndMatchesTheReturnedList()
    {
        // OnFound lets the UI show a live "found so far" count without
        // waiting for the whole sweep - it must fire exactly once per hit, with
        // the same NetService the final list contains, not a duplicate or a miss.
        var (listener, port) = OpenTcpPort();
        int first = port - 5, last = Math.Min(65535, port + 5);

        try
        {
            var onFoundHits = new System.Collections.Concurrent.ConcurrentBag<NetService>();
            var found = await PortScanner.ScanRangeAsync("127.0.0.1", first, last, null, default,
                onFound: s => onFoundHits.Add(s));

            Assert.Contains(found, s => s.Port == port);
            Assert.Equal(found.Count, onFoundHits.Count);
            Assert.Contains(onFoundHits, s => s.Port == port);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RunAsync_Custom_FindsAnOpenTcpPortFromAnExplicitList()
    {
        // The port-check tool's "list" scope - a handful of specific
        // port numbers rather than a range or the curated common-services set.
        var (listener, port) = OpenTcpPort();
        var (closedListener, closedPort) = OpenTcpPort();
        closedListener.Stop();   // a port that was free a moment ago and has no listener now

        try
        {
            var found = await RunCustomAsync("127.0.0.1",
                tcpPorts: new[] { port, closedPort }, udpPorts: Array.Empty<int>(), progress: null, ct: default);

            Assert.Contains(found, s => s.Port == port && s.Protocol == NetProtocol.Tcp);
            Assert.DoesNotContain(found, s => s.Port == closedPort);
        }
        finally { listener.Stop(); }
    }

    private static (TcpListener listener, int port) OpenTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try { while (true) { using var c = await listener.AcceptTcpClientAsync(); } }
            catch { /* listener stopped */ }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Theory]
    [InlineData(true, ServiceConfidence.Probable)]
    [InlineData(false, ServiceConfidence.Confirmed)]
    public async Task RunAsync_Custom_PortThatTheCanaryAnswersToo_IsOnlyProbable(bool canaryAnswers, ServiceConfidence expected)
    {
        // A gateway that intercepts a port answers for addresses nobody owns: the handshake
        // succeeds, yet the target itself never spoke.
        var (listener, port) = OpenTcpPort();
        var seen = new System.Collections.Generic.List<NetService>();
        var asked = 0;
        PortScanner.CanaryOverride.Value = (_, _) => { Interlocked.Increment(ref asked); return Task.FromResult(canaryAnswers); };
        try
        {
            var found = await RunCustomAsync("127.0.0.1",
                tcpPorts: new[] { port }, udpPorts: Array.Empty<int>(), progress: null, ct: default,
                onFound: x => { lock (seen) seen.Add(x); });

            var hit = Assert.Single(found);
            Assert.Equal(expected, hit.Confidence);
            Assert.Equal(canaryAnswers, hit.AnsweredByNetwork);
            Assert.Equal(expected, Assert.Single(seen).Confidence);
            Assert.Equal(1, asked);
            if (canaryAnswers)
            {
                IpScanner.Core.Localization.Loc.SetLanguage("en");
                Assert.Contains(IpScanner.Core.Localization.Loc.ServiceNetworkAnsweredHint, hit.TooltipText);
            }
        }
        finally { PortScanner.CanaryOverride.Value = null; listener.Stop(); }
    }

    [Fact]
    public async Task ScanRangeAsync_PortThatTheCanaryAnswersToo_IsOnlyProbable()
    {
        var (listener, port) = OpenTcpPort();
        PortScanner.CanaryOverride.Value = (proto, p) => Task.FromResult(proto == NetProtocol.Tcp && p == port);
        try
        {
            var found = await PortScanner.ScanRangeAsync("127.0.0.1", port, port, null, default);

            var hit = Assert.Single(found);
            Assert.True(hit.IsProbable);
            Assert.True(hit.AnsweredByNetwork);
        }
        finally { PortScanner.CanaryOverride.Value = null; listener.Stop(); }
    }

    [Fact]
    public async Task StopDuringACanaryProbe_EndsTheScanWithinASecond()
    {
        var (listener, port) = OpenTcpPort();
        var canaryStarted = new TaskCompletionSource();
        PortScanner.CanaryOverride.Value = (_, _) => { canaryStarted.TrySetResult(); return new TaskCompletionSource<bool>().Task; };
        using var cts = new CancellationTokenSource();
        try
        {
            var scan = PortScanner.ScanRangeAsync("127.0.0.1", port, port, null, cts.Token);
            await canaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopped = System.Diagnostics.Stopwatch.StartNew();

            cts.Cancel();
            var found = await scan.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(1), "took " + stopped.Elapsed);
            Assert.Empty(found);
        }
        finally { PortScanner.CanaryOverride.Value = null; listener.Stop(); }
    }

    [Fact]
    public async Task CanaryThatFails_LeavesTheAnswerConfirmed()
    {
        var (listener, port) = OpenTcpPort();
        PortScanner.CanaryOverride.Value = (_, _) => throw new InvalidOperationException("no route");
        try
        {
            var found = await PortScanner.ScanRangeAsync("127.0.0.1", port, port, null, default);

            Assert.False(Assert.Single(found).IsProbable);
        }
        finally { PortScanner.CanaryOverride.Value = null; listener.Stop(); }
    }

    [Fact]
    public async Task RunAsync_Custom_ReportsOnlyAnsweringPorts_NotSilentOrClosedUdpPorts()
    {
        using var fast = new FastProbeTimeouts();
        // Nothing listens here: the port is Closed (or OpenFiltered), never an open service.
        var found = await RunCustomAsync("127.0.0.1",
            tcpPorts: Array.Empty<int>(), udpPorts: new[] { 61247 }, progress: null, ct: default);

        Assert.Empty(found);
    }

    [Fact]
    public async Task RunAsync_Custom_AlreadyCancelled_ReturnsQuicklyWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var found = await RunCustomAsync("127.0.0.1",
            tcpPorts: new[] { 61248 }, udpPorts: Array.Empty<int>(), progress: null, ct: cts.Token);

        Assert.Empty(found);
    }

    [Fact]
    public async Task ScanRangeAsync_AlreadyCancelled_ReturnsQuicklyWithoutThrowing()
    {
        // Unlike the known-list ScanAsync, a full sweep must not throw on
        // cancellation - a user-cancelled full scan should just show whatever
        // partial results it already had, not an unhandled exception.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var found = await PortScanner.ScanFullRangeAsync("203.0.113.1", null, cts.Token);
        sw.Stop();

        Assert.Empty(found);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Cancelled full scan should return near-instantly, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ScanAsync_CancelledDuringTheUdpPhase_Throws()
    {
        using var fast = new FastProbeTimeouts();
        using var cts = new CancellationTokenSource();
        int tcpProbes = PortScanner.TcpPorts.Length + PortScanner.CustomTcpPorts.Count;
        int entered = 0;
        // Cancel once the last of the six UDP probes is past its gates: every UDP probe is then
        // waiting for a reply and sees the cancel inside its receive.
        PortScanner.ProbeGateTestHook = _ =>
        {
            if (Interlocked.Increment(ref entered) == tcpProbes + 6) cts.Cancel();
            return Task.CompletedTask;
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PortScanner.ScanAsync("127.0.0.1", cts.Token));
        }
        finally { PortScanner.ProbeGateTestHook = null; }
    }

    [Fact]
    public async Task RunAsync_Custom_CancelDuringALongBannerRead_StopsWithinOneSecond()
    {
        // A device that accepts the connection and then stays silent keeps the banner read
        // waiting for its full timeout; a stop must cut that wait short.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            try { while (true) held.Add(await listener.AcceptTcpClientAsync()); }
            catch { /* listener stopped */ }
        });
        var prevConnect = PortScanner.ConnectTimeoutMs;
        var prevBanner = PortScanner.BannerTimeoutMs;
        var prevCustom = PortScanner.CustomTcpPorts;
        PortScanner.ConnectTimeoutMs = 5000;
        PortScanner.BannerTimeoutMs = 5000;
        PortScanner.CustomTcpPorts = new() { (port, "HTTP-Alt") };   // named like HTTP so the banner read starts
        try
        {
            using var cts = new CancellationTokenSource();
            var scan = RunCustomAsync("127.0.0.1", new[] { port }, Array.Empty<int>(), null, cts.Token);
            await Task.Delay(300);
            var sw = Stopwatch.StartNew();
            cts.Cancel();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000, $"Stop took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            PortScanner.ConnectTimeoutMs = prevConnect;
            PortScanner.BannerTimeoutMs = prevBanner;
            PortScanner.CustomTcpPorts = prevCustom;
            listener.Stop();
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public void BuildSnmpGetSysDescr_DefaultCommunity_MatchesHistoricalPacket()
    {
        // The original hardcoded byte array baked in community "public" - the
        // dynamic builder must reproduce it exactly for the default,
        // since it's the packet every device in the field has been tested against.
        byte[] historical =
        {
            0x30, 0x29, 0x02, 0x01, 0x00, 0x04, 0x06, 0x70, 0x75, 0x62, 0x6C, 0x69, 0x63,
            0xA0, 0x1C, 0x02, 0x04, 0x00, 0x00, 0x00, 0x01, 0x02, 0x01, 0x00, 0x02, 0x01, 0x00,
            0x30, 0x0E, 0x30, 0x0C, 0x06, 0x08, 0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x00,
            0x05, 0x00,
        };

        Assert.Equal(historical, PortScanner.BuildSnmpGetSysDescr("public"));
    }

    [Fact]
    public void BuildSnmpGetSysDescr_CustomCommunity_EncodesLengthCorrectly()
    {
        var packet = PortScanner.BuildSnmpGetSysDescr("mycommunity");
        Assert.Equal(0x30, packet[0]);                       // outer SEQUENCE tag
        Assert.Equal(35 + "mycommunity".Length, packet[1]);  // outer content length
        Assert.Equal(0x04, packet[5]);                       // community OCTET STRING tag
        Assert.Equal("mycommunity".Length, packet[6]);
        Assert.Equal("mycommunity", System.Text.Encoding.ASCII.GetString(packet, 7, "mycommunity".Length));
    }

    [Fact]
    public void BuildSnmpGetSysDescr_OverlongCommunity_TruncatesTo32Bytes()
    {
        var packet = PortScanner.BuildSnmpGetSysDescr(new string('x', 100));
        Assert.Equal(32, packet[6]);
        Assert.True(packet[1] <= 127);   // still single-byte BER length
    }

    [Fact]
    public async Task ScanAsync_CustomTcpPortNotInKnownList_IsProbed()
    {
        using var fast = new FastProbeTimeouts();
        var (listener, port) = OpenTcpPort();   // ephemeral, outside PortScanner's known-port list
        var prevCustom = PortScanner.CustomTcpPorts;
        PortScanner.CustomTcpPorts = new() { (port, "MyService") };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var found = await PortScanner.ScanAsync("127.0.0.1", cts.Token);
            Assert.Contains(found, s => s.Port == port && s.Name == "MyService");
        }
        finally
        {
            PortScanner.CustomTcpPorts = prevCustom;
            listener.Stop();
        }
    }

    [Fact]
    public void CustomTcpPorts_CollidingWithKnownPort_DoesNotDuplicate()
    {
        var prevCustom = PortScanner.CustomTcpPorts;
        var knownPort = PortScanner.TcpPorts[0].Port;
        PortScanner.CustomTcpPorts = new() { (knownPort, "ShouldBeIgnored") };
        try
        {
            var known = new System.Collections.Generic.HashSet<int>(PortScanner.TcpPorts.Select(p => p.Port));
            var merged = PortScanner.TcpPorts
                .Select(p => p.Port)
                .Concat(PortScanner.CustomTcpPorts.Where(p => !known.Contains(p.Port)).Select(p => p.Port))
                .ToList();
            Assert.Single(merged, p => p == knownPort);   // still just one entry, not duplicated
        }
        finally { PortScanner.CustomTcpPorts = prevCustom; }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial;
        do { initial = target; if (value <= initial) return; }
        while (Interlocked.CompareExchange(ref target, value, initial) != initial);
    }

    /// <summary>Calls back synchronously on the reporting thread, unlike the real
    /// Progress&lt;T&gt;, which marshals through a captured SynchronizationContext
    /// (or the thread pool) and so delivers asynchronously and out of order.</summary>
    private sealed class MaxTrackingProgress : IProgress<int>
    {
        private readonly Action<int> _onReport;
        public MaxTrackingProgress(Action<int> onReport) => _onReport = onReport;
        public void Report(int value) => _onReport(value);
    }

    [Fact]
    public void SnmpCommunityFor_TrustsByRange_NotByWholeSlash24()
    {
        var oldCommunity = PortScanner.SnmpCommunity;
        var oldNetworks = PortScanner.SnmpTrustedNetworks;
        try
        {
            PortScanner.SnmpCommunity = "private1";
            PortScanner.SnmpTrustedNetworks = new[] { "203.0.113.5/32", "198.51.100.64/28", "10.0.4.9/22" };
            Assert.Equal("private1", PortScanner.SnmpCommunityFor("203.0.113.5"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("203.0.113.6"));
            Assert.Equal("private1", PortScanner.SnmpCommunityFor("198.51.100.79"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("198.51.100.80"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("198.51.100.1"));
            Assert.Equal("private1", PortScanner.SnmpCommunityFor("10.0.7.200"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("10.0.8.1"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("not an ip"));
        }
        finally
        {
            PortScanner.SnmpCommunity = oldCommunity;
            PortScanner.SnmpTrustedNetworks = oldNetworks;
        }
    }

    [Fact]
    public void SnmpCommunityFor_SendsTheConfiguredCommunityOnlyInsideTrustedNetworks()
    {
        var oldCommunity = PortScanner.SnmpCommunity;
        var oldNetworks = PortScanner.SnmpTrustedNetworks;
        try
        {
            PortScanner.SnmpCommunity = "private1";
            PortScanner.SnmpTrustedNetworks = new[] { "192.168.1.0/24", "10.5.0.0/16" };
            Assert.Equal("private1", PortScanner.SnmpCommunityFor("192.168.1.20"));
            Assert.Equal("private1", PortScanner.SnmpCommunityFor("10.5.7.9"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("192.168.2.20"));
            Assert.Equal("public", PortScanner.SnmpCommunityFor("8.8.8.8"));

            PortScanner.SnmpTrustedNetworks = Array.Empty<string>();
            Assert.Equal("public", PortScanner.SnmpCommunityFor("192.168.1.20"));
        }
        finally
        {
            PortScanner.SnmpCommunity = oldCommunity;
            PortScanner.SnmpTrustedNetworks = oldNetworks;
        }
    }

    [Theory]
    [InlineData("127.0.0.0/24", "private2")]
    [InlineData("203.0.113.0/24", "public")]
    public async Task RunAsync_Custom_Udp161_SendsTheCommunityOnlyToATrustedTarget(string trusted, string expectedCommunity)
    {
        var oldCommunity = PortScanner.SnmpCommunity;
        var oldNetworks = PortScanner.SnmpTrustedNetworks;
        using var snmp161 = new LoopbackUdpAs161();
        var listener = snmp161.Listener;
        try
        {
            PortScanner.SnmpCommunity = "private2";
            PortScanner.SnmpTrustedNetworks = new[] { trusted };
            var received = Task.Run(async () =>
            {
                var r = await listener.ReceiveAsync();
                return System.Text.Encoding.ASCII.GetString(r.Buffer);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _ = await RunCustomAsync("127.0.0.1", Array.Empty<int>(), new[] { 161 }, null, cts.Token);
            var packet = await received.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(expectedCommunity, packet);
            if (expectedCommunity == "public") Assert.DoesNotContain("private2", packet);
        }
        finally
        {
            PortScanner.SnmpCommunity = oldCommunity;
            PortScanner.SnmpTrustedNetworks = oldNetworks;
        }
    }

    /// <summary>A fake UDP service on an ephemeral loopback port that the scanner reaches whenever it probes
    /// port 161, so parallel test processes never fight over the real port.</summary>
    private sealed class LoopbackUdpAs161 : IDisposable
    {
        public UdpClient Listener { get; } = new(new IPEndPoint(IPAddress.Loopback, 0));

        public LoopbackUdpAs161()
        {
            int actual = ((IPEndPoint)Listener.Client.LocalEndPoint!).Port;
            PortScanner.UdpPortRedirect.Value = p => p == 161 ? actual : p;
        }

        public void Dispose()
        {
            PortScanner.UdpPortRedirect.Value = null;
            Listener.Dispose();
        }
    }
}
