using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>The extra connections of service detection follow the run's delay rule.</summary>
[Collection("PortScannerGate")]
public class ServiceThrottleTests
{
    private sealed class CountingClock : IScanClock
    {
        private readonly object _sync = new();
        private long _now;
        public long Slept;

        public long NowMs { get { lock (_sync) return _now; } }

        public Task DelayAsync(int ms, CancellationToken ct)
        {
            lock (_sync) { _now += ms; Slept += ms; }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task IdentifyAsync_ExtraConnectionsWaitForTheScanDelay()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepted = 0;
        var oldBanner = PortScanner.BannerTimeoutMs;
        try
        {
            PortScanner.BannerTimeoutMs = 40;
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var c = await listener.AcceptTcpClientAsync();
                        Interlocked.Increment(ref accepted);
                        _ = Task.Delay(1000).ContinueWith(_ => c.Dispose());
                    }
                }
                catch (Exception) { /* listener stopped */ }
            });

            var clock = new CountingClock();
            var timing = ScanTiming.FromSettings(ScanTiming.Normal, 0, 0, 0, 0, 700, 0);
            // The routine time cap is wall clock (two banner waits); under load the first probe alone
            // can use it up. The deep run has no cap, so the number of connections stops depending on speed.
            using (PortScanner.UseDeepDetection())
            using (PortScanner.UseServiceThrottle(new ProbeThrottle(timing, clock)))
                await ServiceProbeRunner.IdentifyAsync("127.0.0.1", port, 7, CancellationToken.None);

            // Every connection after the first waited exactly one scan delay on the fake clock, which
            // tells how many were opened; the accept loop only counts them a moment later.
            long made = Interlocked.Read(ref clock.Slept) / 700 + 1;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref accepted) < made && DateTime.UtcNow < deadline) await Task.Delay(5);

            int connections = Volatile.Read(ref accepted);
            Assert.True(connections >= 2, "connections: " + connections);
            Assert.Equal(made, connections);
            Assert.True(clock.Slept >= (connections - 1) * 700L, $"slept {clock.Slept} for {connections} connections");
        }
        finally
        {
            PortScanner.BannerTimeoutMs = oldBanner;
            listener.Stop();
        }
    }
}
