using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

// Reads and sets the process-wide probe timeouts.
[Collection("PortScannerGate")]
public class ClosedPortTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Theory]
    [InlineData(500)]
    [InlineData(400)]
    [InlineData(250)]
    public async Task RefusedConnect_ReadsClosedWithinTheTimeoutNotFiltered(int timeoutMs)
    {
        int port = FreePort();
        int old = PortScanner.ConnectTimeoutMs;
        PortScanner.ConnectTimeoutMs = timeoutMs;
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await PortScanner.ProbeTcpStateRawAsync("127.0.0.1", port, "test");
            watch.Stop();

            Assert.NotNull(result);
            Assert.Equal(PortState.Closed, result!.State);
            Assert.Equal(PortReason.ConnectRefused, result.Reason);
            Assert.True(watch.ElapsedMilliseconds < 200, $"took {watch.ElapsedMilliseconds} ms");
        }
        finally { PortScanner.ConnectTimeoutMs = old; }
    }

    [Fact]
    public async Task ServiceProbeConnect_ToAClosedPortFailsFast()
    {
        int port = FreePort();
        int old = PortScanner.ConnectTimeoutMs;
        PortScanner.ConnectTimeoutMs = 500;
        try
        {
            var watch = Stopwatch.StartNew();
            var session = await TcpProbeSession.OpenAsync("127.0.0.1", port, false, default);
            watch.Stop();
            if (session is IAsyncDisposable d) await d.DisposeAsync();

            Assert.Null(session);
            Assert.True(watch.ElapsedMilliseconds < 200, $"took {watch.ElapsedMilliseconds} ms");
        }
        finally { PortScanner.ConnectTimeoutMs = old; }
    }
}
