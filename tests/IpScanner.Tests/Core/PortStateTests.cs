using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>One test per port state through the real engine against loopback and TEST-NET.</summary>
[Collection("PortScannerGate")]
public class PortStateTests
{
    private static Task<System.Collections.Generic.IReadOnlyList<PortResult>> Run(string ip, int[] tcp, int[] udp)
        => PortScanEngine.RunAsync(ip, new PortSpec(tcp, udp), ScanOptions.Default, null, default);

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Tcp_LocalListener_IsOpen()
    {
        using var fast = new FastProbeTimeouts();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            var r = (await Run("127.0.0.1", new[] { port }, Array.Empty<int>())).Single();
            Assert.Equal(PortState.Open, r.State);
            Assert.Equal(PortReason.Connected, r.Reason);
            Assert.True(r.IsOpen);
            Assert.Equal(NetProtocol.Tcp, r.Protocol);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Tcp_RefusedLoopbackPort_IsClosedWithTheRefusalReason()
    {
        using var fast = new FastProbeTimeouts();
        PortScanner.ConnectTimeoutMs = 4000; // Windows answers a refused loopback connect only after about a second
        var r = (await Run("127.0.0.1", new[] { FreeTcpPort() }, Array.Empty<int>())).Single();
        Assert.Equal(PortState.Closed, r.State);
        Assert.Equal(PortReason.ConnectRefused, r.Reason);
        Assert.False(r.IsOpen);
        Assert.Null(r.Service);
    }

    [Fact]
    public async Task Tcp_DroppedAddress_IsFiltered()
    {
        using var fast = new FastProbeTimeouts();
        // RFC 5737 TEST-NET-3 never answers: the connect times out instead of being refused.
        var r = (await Run("203.0.113.1", new[] { 81 }, Array.Empty<int>())).Single();
        Assert.Equal(PortState.Filtered, r.State);
    }

    [Fact]
    public async Task Udp_SilentListener_IsOpenFiltered()
    {
        using var fast = new FastProbeTimeouts();
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var r = (await Run("127.0.0.1", Array.Empty<int>(), new[] { port })).Single();
        Assert.Equal(PortState.OpenFiltered, r.State);
        Assert.Equal(PortReason.NoResponse, r.Reason);
        Assert.Equal(NetProtocol.Udp, r.Protocol);
        Assert.False(r.IsOpen);
    }

    [Fact]
    public async Task Udp_PortWithNoSocket_IsClosedViaIcmpUnreachable()
    {
        using var fast = new FastProbeTimeouts();
        int port;
        using (var tmp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)tmp.Client.LocalEndPoint!).Port;
        var r = (await Run("127.0.0.1", Array.Empty<int>(), new[] { port })).Single();
        Assert.Equal(PortState.Closed, r.State);
        Assert.Equal(PortReason.IcmpUnreachable, r.Reason);
    }

    [Fact]
    public async Task Udp_Responder_IsOpenWithTheReplyReason()
    {
        using var fast = new FastProbeTimeouts();
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        var server = Task.Run(async () =>
        {
            var got = await echo.ReceiveAsync();
            await echo.SendAsync(new byte[] { 1, 2, 3 }, 3, got.RemoteEndPoint);
        });
        var r = (await Run("127.0.0.1", Array.Empty<int>(), new[] { port })).Single();
        await server;
        Assert.Equal(PortState.Open, r.State);
        Assert.Equal(PortReason.UdpResponse, r.Reason);
        Assert.NotNull(r.Service);
    }

    [Fact]
    public async Task Engine_ReturnsResultsOrderedByPortThenProtocol_AndReportsProgress()
    {
        using var fast = new FastProbeTimeouts();
        int a = FreeTcpPort();
        int b = FreeTcpPort();
        var ports = new[] { Math.Max(a, b), Math.Min(a, b) };
        int last = 0;
        var results = await PortScanEngine.RunAsync("127.0.0.1", new PortSpec(ports, Array.Empty<int>()),
            ScanOptions.Default, new Progress<int>(d => last = Math.Max(last, d)), default);
        Assert.Equal(ports.OrderBy(p => p), results.Select(r => r.Port));
        await Task.Delay(50);
        Assert.True(last <= 2);
    }

    [Fact]
    public async Task Engine_AlreadyCancelled_ReturnsWithoutThrowing()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var results = await PortScanEngine.RunAsync("127.0.0.1", PortSpec.Range(1, 50), ScanOptions.Default, null, cts.Token);
        Assert.True(results.Count < 100);
    }
}
