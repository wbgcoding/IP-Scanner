using System.Net;
using System.Net.Sockets;

namespace IpScanner.Core.Scanner;

/// <summary>The single-port probes that report a state instead of just "found or not".</summary>
public static partial class PortScanner
{
    private const int SioTcpInitialRto = unchecked((int)0x98000011);

    /// <summary>Stops Windows from re-sending the SYN after a RST. Without this a refused connect takes about
    /// two seconds, longer than the probe timeouts, and a closed port would read as filtered. Best effort:
    /// a system without the option keeps the default behaviour.</summary>
    internal static void ReadRefusalPromptly(Socket socket)
    {
        try
        {
            // TCP_INITIAL_RTO_PARAMETERS: Rtt 0xFFFF (system default), MaxSynRetransmissions 0xFE (none), one pad byte.
            socket.IOControl(SioTcpInitialRto, new byte[] { 0xFF, 0xFF, 0xFE, 0x00 }, null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug(LogArea.Ports, "Initial RTO option not applied: " + ex.GetType().Name);
        }
    }

    /// <summary>Full-handshake probe: Open when the connect completes, Closed on a refusal (RST),
    /// Filtered on a timeout or an unreachable report. Null when the caller stopped. The answer is
    /// not yet vetted against network interception.</summary>
    internal static async Task<PortResult?> ProbeTcpStateRawAsync(string ip, int port, string name, CancellationToken ct = default)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            ReadRefusalPromptly(client.Client);
            if (NetworkDetector.SourceFor(ip) is { } srcAddr)
                client.Client.Bind(new IPEndPoint(srcAddr, 0));
            // The token (not a Task.WhenAny/Task.Delay race) aborts the pending connect on timeout, and the
            // caller's token is linked in so a stop does not wait out the connect timeout.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EffectiveConnectTimeoutMs);
            try { await client.ConnectAsync(ip, port, cts.Token); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return null;
                return new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse);
            }
            catch (SocketException ex)
            {
                return ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => new PortResult(port, NetProtocol.Tcp, PortState.Closed, PortReason.ConnectRefused),
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown
                        or SocketError.HostDown => new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.IcmpUnreachable),
                    _ => new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse),
                };
            }
            if (!client.Connected) return new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse);
            var version = await GrabBanner(client, ip, port, name, ct);
            return new PortResult(port, NetProtocol.Tcp, PortState.Open, PortReason.Connected,
                new NetService(name, port, NetProtocol.Tcp, version));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Debug(LogArea.Ports, "TCP probe failed: " + ex.GetType().Name);
            return new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse);
        }
        finally { client?.Dispose(); }
    }

    /// <summary>The connect probe followed by the interception check for an open port.</summary>
    internal static async Task<PortResult?> ProbeTcpStateAsync(string ip, int port, string name, CancellationToken ct = default)
    {
        var result = await ProbeTcpStateRawAsync(ip, port, name, ct);
        if (result is not { State: PortState.Open, Service: { } svc }) return result;
        var vetted = await VetAsync(ip, svc, ct);
        if (vetted is null) return null;
        if (vetted.AnsweredByNetwork) return result with { Service = vetted };
        if (!ServiceDetection) return result with { Service = vetted };
        // Name the service from what it says, not from the port number; a stop leaves the result as the connect saw it.
        var detection = await ServiceProbeRunner.IdentifyAsync(ip, port, ServiceProbeIntensity, ct);
        if (ct.IsCancellationRequested) return null;
        return result with { Service = detection is null ? vetted : ServiceProbeRunner.Apply(vetted, detection) };
    }

    /// <summary>Test seam: maps the port a UDP probe is about (which picks the payload and the reply check)
    /// to the port its datagram is actually sent to, so a test can stand a fake service on an ephemeral
    /// loopback port instead of a fixed one that a parallel test run may hold.</summary>
    internal static readonly AsyncLocal<Func<int, int>?> UdpPortRedirect = new();

    /// <summary>UDP probe. A reply is Open (Confirmed when it has the protocol's shape), an ICMP
    /// port-unreachable that the OS surfaces on the connected socket is Closed, an unreachable
    /// network report is Filtered and silence is OpenFiltered, because UDP cannot tell a silent
    /// service from a dropped packet. Null when the caller stopped.</summary>
    internal static async Task<PortResult?> ProbeUdpStateRawAsync(string ip, int port, string name, CancellationToken ct = default)
    {
        var probe = UdpPayloadFor(ip, port);
        try
        {
            using var udp = NetworkDetector.SourceFor(ip) is { } srcAddr
                ? new UdpClient(new IPEndPoint(srcAddr, 0))
                : new UdpClient(IPAddress.TryParse(ip, out var dst) && dst.AddressFamily == AddressFamily.InterNetworkV6
                    ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            // Connect() before send restricts this socket at the OS level to datagrams from exactly this
            // ip:port; without it any other host on the LAN could spoof a reply and fake an open port.
            // It also makes the OS report an ICMP port-unreachable as a ConnectionReset on receive.
            udp.Connect(ip, UdpPortRedirect.Value?.Invoke(port) ?? port);
            await udp.SendAsync(probe.AsMemory(), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EffectiveUdpTimeoutMs);
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return null;
                return new PortResult(port, NetProtocol.Udp, PortState.OpenFiltered, PortReason.NoResponse);
            }
            var confidence = IsConfirmedUdpReply(port, result.Buffer)
                ? ServiceConfidence.Confirmed : ServiceConfidence.Probable;
            return new PortResult(port, NetProtocol.Udp, PortState.Open, PortReason.UdpResponse,
                new NetService(name, port, NetProtocol.Udp, Confidence: confidence));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return new PortResult(port, NetProtocol.Udp, PortState.Closed, PortReason.IcmpUnreachable);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable
                                             or SocketError.NetworkDown or SocketError.HostDown)
        {
            return new PortResult(port, NetProtocol.Udp, PortState.Filtered, PortReason.IcmpUnreachable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Debug(LogArea.Ports, "UDP probe failed: " + ex.GetType().Name);
            return new PortResult(port, NetProtocol.Udp, PortState.OpenFiltered, PortReason.NoResponse);
        }
    }

    internal static async Task<PortResult?> ProbeUdpStateAsync(string ip, int port, string name, CancellationToken ct = default)
    {
        var result = await ProbeUdpStateRawAsync(ip, port, name, ct);
        if (result is not { State: PortState.Open, Service: { } svc }) return result;
        var vetted = await VetAsync(ip, svc, ct);
        return vetted is null ? null : result with { Service = vetted };
    }

    /// <summary>Highest valid TCP port - the "scan everything" ceiling.</summary>
    public const int MaxPort = PortSpec.MaxPort;

    private static IReadOnlyList<NetService> OpenServices(string ip, IEnumerable<PortResult> results)
        => results.Where(r => r.IsOpen && r.Service is not null).Select(r => r.Service! with { Ip = ip })
            .OrderBy(s => s.Port).ToList();

    /// <summary>Probe every known TCP + UDP port and return the ones that answered, with a
    /// version banner where the protocol offers one. Runs through <see cref="PortScanEngine"/>
    /// with the common-services preset; <paramref name="ct"/> stops launching further probes
    /// and, unlike the range scans, makes the call throw so a partial list is never mistaken
    /// for a complete one.</summary>
    public static async Task<IReadOnlyList<NetService>> ScanAsync(string ip, CancellationToken ct = default)
    {
        var results = await PortScanEngine.RunAsync(ip, PortSpec.Common(), ScanOptions.Default, null, ct);
        ct.ThrowIfCancellationRequested();
        return OpenServices(ip, results);
    }

    /// <summary>Every TCP port from 1 to <see cref="MaxPort"/>: opt-in only, since a full sweep
    /// reads as a port scan to firewalls and IDS. A stop returns what was found so far.</summary>
    public static Task<IReadOnlyList<NetService>> ScanFullRangeAsync(
        string ip, IProgress<int>? progress = null, CancellationToken ct = default, Action<NetService>? onFound = null)
        => ScanRangeAsync(ip, 1, MaxPort, progress, ct, onFound);

    /// <summary>TCP connect scan over [<paramref name="firstPort"/>, <paramref name="lastPort"/>].
    /// <paramref name="onFound"/> fires for every port that answers, so a UI counter can show
    /// "found so far" during a long sweep.</summary>
    internal static async Task<IReadOnlyList<NetService>> ScanRangeAsync(
        string ip, int firstPort, int lastPort, IProgress<int>? progress, CancellationToken ct,
        Action<NetService>? onFound = null)
    {
        var spec = new PortSpec(Enumerable.Range(firstPort, Math.Max(0, lastPort - firstPort + 1)), Array.Empty<int>());
        var results = await PortScanEngine.RunAsync(ip, spec, new ScanOptions(ScanType.Connect, Tcp: true, Udp: false),
            progress, ct, r => { if (r.IsOpen && r.Service is not null) onFound?.Invoke(r.Service); });
        return OpenServices(ip, results);
    }

    /// <summary>Probe used by the interception check: the open service on that port, or null.</summary>
    private static async Task<NetService?> ProbeTcpRaw(string ip, int port, string name, CancellationToken ct = default)
        => (await ProbeTcpStateRawAsync(ip, port, name, ct)) is { State: PortState.Open } r ? r.Service : null;

    private static async Task<NetService?> ProbeUdpRaw(string ip, int port, string name, CancellationToken ct = default)
        => (await ProbeUdpStateRawAsync(ip, port, name, ct)) is { State: PortState.Open } r ? r.Service : null;

    /// <summary>The datagram a UDP port is asked with: the protocol's own request where one is known,
    /// an empty datagram otherwise (a closed port still answers that with an ICMP unreachable).</summary>
    private static byte[] UdpPayloadFor(string ip, int port) => port switch
    {
        53 => BuildDnsQuery(),
        123 => NtpProbe,
        161 => BuildSnmpGetSysDescr(SnmpCommunityFor(ip)),
        137 => NetBiosNbstatProbe,
        1900 => SsdpProbe,
        5353 => MdnsHelper.BuildPtrQuery(ip),
        _ => Array.Empty<byte>(),
    };
}
