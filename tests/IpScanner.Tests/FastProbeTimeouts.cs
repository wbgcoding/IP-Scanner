using System;
using IpScanner.Core.Scanner;

namespace IpScanner.Tests;

/// <summary>Shortens the probe timeouts for one test so sweeps over closed loopback ports finish
/// quickly. Loopback answers instantly, so the values only bound the silent ports. Tests that use
/// it must run in the "PortScannerGate" collection because the timeouts are process-wide.</summary>
public sealed class FastProbeTimeouts : IDisposable
{
    private readonly int _connect = PortScanner.ConnectTimeoutMs;
    private readonly int _banner = PortScanner.BannerTimeoutMs;
    private readonly int _udp = PortScanner.UdpTimeoutMs;

    public FastProbeTimeouts()
    {
        PortScanner.ConnectTimeoutMs = 80;
        PortScanner.BannerTimeoutMs = 400;
        PortScanner.UdpTimeoutMs = 400;
    }

    public void Dispose()
    {
        PortScanner.ConnectTimeoutMs = _connect;
        PortScanner.BannerTimeoutMs = _banner;
        PortScanner.UdpTimeoutMs = _udp;
    }
}
