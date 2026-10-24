using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IpScanner.Core.Scanner;

public enum NetProtocol { Tcp, Udp }

/// <summary>How sure a probe is that the port is genuinely open. A UDP
/// reply alone does not prove that - only a response matching the protocol's own
/// shape does; everything else (including every TCP hit, which requires a real
/// handshake) is Confirmed - unless the network itself answers on that port for every
/// address, even an unroutable one (see <see cref="NetService.AnsweredByNetwork"/>).</summary>
public enum ServiceConfidence { Confirmed, Probable }

/// <summary>An open port on a device: name, protocol, and (when detected) the
/// service's version banner. Ip is filled in by the caller for the open-in-app buttons.</summary>
public sealed record NetService(string Name, int Port, NetProtocol Protocol, string? Version = null,
                                ServiceConfidence Confidence = ServiceConfidence.Confirmed,
                                bool AnsweredByNetwork = false)
{
    public string Ip { get; init; } = "";
    public string ProtoLabel => Protocol == NetProtocol.Udp ? "UDP" : "TCP";
    public string PortLabel => $"{Port}/{ProtoLabel}";
    /// <summary>UDP-only: a datagram came back, but its content did not match the
    /// protocol's expected reply shape - shown as a dashed tile instead of a solid one.</summary>
    public bool IsProbable => Confidence == ServiceConfidence.Probable;

    /// <summary>An unroutable canary address answers on this port too: a gateway or resolver
    /// intercepts the traffic, so the reply is not necessarily from the device.</summary>
    public bool AnsweredByNetwork { get; init; } = AnsweredByNetwork;

    /// <summary>TLS facts seen while probing this port (protocol, certificate subject, issuer, expiry); null when it was not probed over TLS.</summary>
    internal TlsInfo? Tls { get; init; }

    /// <summary>Version banner trimmed for the tile so one long response never stretches
    /// a single tile across the row - the tooltip always shows the full text.</summary>
    private const int VersionDisplayMax = 24;
    public string VersionShort => Version is { Length: > VersionDisplayMax } v ? v[..VersionDisplayMax] + "…" : Version ?? "";

    /// <summary>Full name + version, used for exports/tooltips - never truncated.</summary>
    public string DisplayName => Version is { Length: > 0 } v ? $"{Name} {v}" : Name;
    /// <summary>Name + truncated version, used for the tile's own text.</summary>
    public string DisplayNameShort => Version is { Length: > 0 } ? $"{Name} {VersionShort}" : Name;

    /// <summary>Speaking names for ports that are still plain HTTP under the
    /// hood - a distinguishing label must not stop "open in browser" (and the banner
    /// grab / launcher in <see cref="PortScanner"/>) from working.</summary>
    internal static readonly HashSet<string> HttpNames = new()
    { "HTTP", "HTTP-Alt", "HTTP-Dev", "HTTP-Proxy", "HTTP-Admin", "Spark-UI" };

    /// <summary>True for the handful of services we know how to launch a client for.</summary>
    public bool IsOpenable => Protocol == NetProtocol.Tcp &&
        (HttpNames.Contains(Name) || Name is "HTTPS" or "SSH" or "RDP" or "SMB" or "FTP");

    /// <summary>Human-readable target shown in the chip tooltip - a URL for the web
    /// protocols, "host:port"/UNC path for the rest.</summary>
    public string DisplayTarget => HttpNames.Contains(Name) ? (Port == 80 ? $"http://{Ip}" : $"http://{Ip}:{Port}")
        : Name switch
    {
        "HTTPS" => Port == 443 ? $"https://{Ip}" : $"https://{Ip}:{Port}",
        "SMB" => $@"\\{Ip}",
        "FTP" => Port == 21 ? $"ftp://{Ip}" : $"ftp://{Ip}:{Port}",
        _ => $"{Ip}:{Port}",
    };

    /// <summary>Openable chips explain what clicking them does; everything else shows the
    /// port label plus the full (untruncated) name and version banner, since the tile
    /// itself only ever shows the trimmed version.</summary>
    public string TooltipText => !IsOpenable
        ? IsProbable ? $"{PortLabel} · {DisplayName} · {(AnsweredByNetwork ? Localization.Loc.ServiceNetworkAnsweredHint : Localization.Loc.ServiceProbableHint)}"
                     : $"{PortLabel} · {DisplayName}"
        : string.Format(HttpNames.Contains(Name) || Name == "HTTPS" ? Localization.Loc.OpenInBrowser : Localization.Loc.OpenWithApp,
            DisplayTarget);
}

/// <summary>Probes a device for open TCP/UDP ports across a broad list of common
/// services, grabs a lightweight version banner where the protocol offers one
/// (no full nmap-style fingerprinting), and launches the matching client for the
/// services we know how to open.</summary>
public static partial class PortScanner
{
    // Advanced network settings - all five are mutable so MainWindow can
    // push the configured values in at startup/settings-apply (ApplyPortScanSettings),
    // same pattern as Palette.HeatMs1..4. Defaults match the previous hardcoded consts.
    internal static int ConnectTimeoutMs { get; set; } = 500;
    internal static int BannerTimeoutMs { get; set; } = 600;
    /// <summary>How hard the service identification probes (0 = listen only, 9 = every probe).</summary>
    internal static int ServiceProbeIntensity { get; set; } = ServiceProbeRunner.DefaultIntensity;
    /// <summary>False skips the service identification probes, so an open port keeps its table name.</summary>
    internal static bool ServiceDetection { get; set; } = true;
    internal static int UdpTimeoutMs { get; set; } = 800;

    /// <summary>The pacing in force: retries, scan delay, rate cap and host timeout are read from it by
    /// <see cref="PortScanEngine"/>. Starts as template 3, which is exactly the constants above.</summary>
    internal static ScanTiming Timing { get; private set; } = ScanTiming.Normal3;

    /// <summary>Makes <paramref name="timing"/> the scanner's pacing and pushes its timeouts and
    /// parallelism into the statics the probes read.</summary>
    internal static void ApplyTiming(ScanTiming timing)
    {
        Timing = timing;
        ConnectTimeoutMs = timing.InitialRttMs;
        UdpTimeoutMs = timing.UdpTimeoutMs;
        BannerTimeoutMs = timing.BannerTimeoutMs;
        ConfigureParallelism(timing.Parallelism);
    }

    private static readonly AsyncLocal<int?> ProbeTimeoutOverride = new();

    /// <summary>Gives the probes started inside the returned scope a measured timeout instead of the
    /// static one; dispose restores the previous value.</summary>
    internal static IDisposable UseAdaptiveTimeout(int ms)
    {
        var previous = ProbeTimeoutOverride.Value;
        ProbeTimeoutOverride.Value = ms;
        return new TimeoutScope(previous);
    }

    private sealed class TimeoutScope : IDisposable
    {
        private readonly int? _previous;
        public TimeoutScope(int? previous) => _previous = previous;
        public void Dispose() => ProbeTimeoutOverride.Value = _previous;
    }

    private static readonly AsyncLocal<ProbeThrottle?> ServiceThrottle = new();

    /// <summary>Makes the extra connections of service detection started inside the returned scope wait for the
    /// run's delay and rate rules like any other probe; dispose restores the previous throttle.</summary>
    internal static IDisposable UseServiceThrottle(ProbeThrottle? throttle)
    {
        var previous = ServiceThrottle.Value;
        ServiceThrottle.Value = throttle;
        return new ThrottleScope(previous);
    }

    internal static ProbeThrottle? CurrentServiceThrottle => ServiceThrottle.Value;

    private sealed class ThrottleScope : IDisposable
    {
        private readonly ProbeThrottle? _previous;
        public ThrottleScope(ProbeThrottle? previous) => _previous = previous;
        public void Dispose() => ServiceThrottle.Value = _previous;
    }

    private static readonly AsyncLocal<bool> Deep = new();

    /// <summary>True inside <see cref="UseDeepDetection"/>: service detection runs without the routine time cap.</summary>
    internal static bool DeepDetection => Deep.Value;

    /// <summary>Lets service detection started inside the returned scope use every probe and the full time (the port tool).</summary>
    internal static IDisposable UseDeepDetection()
    {
        var previous = Deep.Value;
        Deep.Value = true;
        return new DeepScope(previous);
    }

    private sealed class DeepScope : IDisposable
    {
        private readonly bool _previous;
        public DeepScope(bool previous) => _previous = previous;
        public void Dispose() => Deep.Value = _previous;
    }

    internal static int EffectiveConnectTimeoutMs => ProbeTimeoutOverride.Value ?? ConnectTimeoutMs;
    private static int EffectiveUdpTimeoutMs => ProbeTimeoutOverride.Value ?? UdpTimeoutMs;
    // 195 TCP + 6 UDP probes fired all at once look exactly like a port scan to
    // any security appliance on the network. Cap how many run concurrently, and
    // accept a token so the caller can stop launching new probes once the
    // device-detail view that asked for this scan has already closed.
    private static readonly object GateSync = new();
    private static SemaphoreSlim _globalProbeGate = new(40, 500);
    private static int _globalProbeGateSize = 40;
    private static int _configuredParallelism = 40;
    private static int _probeGateUsers;
    private static int _activeGlobalProbes;
    private static int _peakGlobalProbes;
    internal static int MaxConcurrentProbes { get; private set; } = 40;
    internal static int PeakGlobalProbes => _peakGlobalProbes;
    internal static Func<CancellationToken, Task>? ProbeGateTestHook { get; set; }

    internal static void ConfigureParallelism(int value)
    {
        value = Math.Clamp(value, 1, 500);
        lock (GateSync)
        {
            _configuredParallelism = value;
            MaxConcurrentProbes = value;
            if (_probeGateUsers == 0) ReplaceGlobalProbeGate();
        }
    }

    private static void ReplaceGlobalProbeGate()
    {
        if (_globalProbeGateSize == _configuredParallelism) return;
        _globalProbeGate = new SemaphoreSlim(_configuredParallelism, _configuredParallelism);
        _globalProbeGateSize = _configuredParallelism;
    }

    internal static void ResetProbeGateMetrics() => _peakGlobalProbes = 0;

    internal static async Task<T?> ProbeWithGatesAsync<T>(
        SemaphoreSlim deviceGate, Func<Task<T?>> probe, CancellationToken ct, bool returnOnCancel) where T : class
    {
        SemaphoreSlim globalGate;
        lock (GateSync)
        {
            _probeGateUsers++;
            globalGate = _globalProbeGate;
        }
        bool deviceEntered = false, globalEntered = false;
        try
        {
            try { await deviceGate.WaitAsync(ct); deviceEntered = true; }
            catch (OperationCanceledException) when (returnOnCancel) { return null; }
            try { await globalGate.WaitAsync(ct); globalEntered = true; }
            catch (OperationCanceledException) when (returnOnCancel) { return null; }
            int active = Interlocked.Increment(ref _activeGlobalProbes);
            InterlockedMax(ref _peakGlobalProbes, active);
            if (ct.IsCancellationRequested && returnOnCancel) return null;
            if (ProbeGateTestHook is { } hook) await hook(ct);
            return await probe();
        }
        finally
        {
            if (globalEntered)
            {
                Interlocked.Decrement(ref _activeGlobalProbes);
                globalGate.Release();
            }
            if (deviceEntered) deviceGate.Release();
            lock (GateSync)
            {
                if (--_probeGateUsers == 0) ReplaceGlobalProbeGate();
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int observed;
        do { observed = target; if (value <= observed) return; }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }
    /// <summary>SNMPv1 community string used by the sysDescr probe on port 161/UDP.
    /// "public" is the near-universal read-only default; changed here when a
    /// network uses a non-default community.</summary>
    internal static string SnmpCommunity { get; set; } = "public";

    /// <summary>Networks (CIDR or base address, like the configured subnets) the configured community
    /// may be sent to. Any other target only ever sees "public", so a private community string never
    /// leaves the user's own networks in cleartext SNMPv1.</summary>
    internal static IReadOnlyList<string> SnmpTrustedNetworks { get; set; } = Array.Empty<string>();

    /// <summary>The community to put into the probe for <paramref name="ip"/>.</summary>
    internal static string SnmpCommunityFor(string ip)
        => string.IsNullOrEmpty(SnmpCommunity) || SnmpCommunity == "public" || !IsInTrustedNetwork(ip) ? "public" : SnmpCommunity;

    private static bool IsInTrustedNetwork(string ip)
    {
        if (!Net.Ipv4.TryParseUInt(ip, out var target)) return false;
        foreach (var raw in SnmpTrustedNetworks)
        {
            var slash = raw.Split('/');
            int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;
            // Below /16 the range is too wide to count as the user's own network.
            if (cidr is < 16 or > 32 || !Net.Ipv4.TryParseUInt(slash[0].Trim(), out var baseAddr)) continue;
            uint mask = 0xFFFFFFFFu << (32 - cidr);
            if ((baseAddr & mask) == (target & mask)) return true;
        }
        return false;
    }

    /// <summary>Extra ports to probe alongside the built-in TCP list, from the
    /// Scan page's "custom port list" - never removes or overrides a
    /// built-in port, only adds ones not already known.</summary>
    internal static List<(int Port, string Name)> CustomTcpPorts { get; set; } = new();

    // Port -> display name. Multiple ports can share a name (e.g. many HTTP-ish
    // ports); the exact port number always travels alongside for the URL/PortLabel.
    // Internal (not private) so PortScannerTests can assert on the table
    // directly instead of reflecting into it.
    internal static readonly (int Port, string Name)[] TcpPorts =
    {
        (20,"FTP-Data"),(21,"FTP"),(22,"SSH"),(23,"Telnet"),(25,"SMTP"),
        (53,"DNS"),(69,"TFTP"),(80,"HTTP"),(81,"HTTP-Alt"),(88,"Kerberos"),
        (110,"POP3"),(111,"RPCbind"),(113,"Ident"),(119,"NNTP"),(135,"MS-RPC"),
        (139,"NetBIOS"),(143,"IMAP"),(161,"SNMP"),(179,"BGP"),(199,"SNMP-Trap"),
        (389,"LDAP"),(427,"SLP"),(443,"HTTPS"),(444,"SNPP"),(445,"SMB"),
        (464,"Kerberos"),(465,"SMTPS"),(514,"Syslog"),(515,"LPD"),(543,"Kerberos-Login"),
        (544,"Kshell"),(548,"AFP"),(554,"RTSP"),(587,"SMTP"),(591,"HTTP-Alt"),
        (593,"MS-RPC-HTTP"),(631,"IPP"),(636,"LDAPS"),(646,"LDP"),(873,"Rsync"),
        (990,"FTPS"),(993,"IMAPS"),(995,"POP3S"),(1025,"MS-RPC"),(1080,"SOCKS"),
        (1194,"OpenVPN"),(1433,"MSSQL"),(1434,"MSSQL-Browser"),(1521,"Oracle"),(1723,"PPTP"),
        (1883,"MQTT"),(1900,"SSDP"),(2049,"NFS"),(2082,"cPanel"),(2083,"cPanel-SSL"),
        (2086,"WHM"),(2087,"WHM-SSL"),(2181,"ZooKeeper"),(2222,"SSH"),(2375,"Docker"),
        (2376,"Docker-TLS"),(2379,"etcd"),(2380,"etcd-Peer"),(3000,"HTTP-Dev"),(3128,"Proxy"),
        (3260,"iSCSI"),(3268,"LDAP-GC"),(3269,"LDAP-GC-SSL"),(3306,"MySQL"),(3389,"RDP"),
        (3690,"SVN"),(4000,"HTTP-Dev"),(4040,"Spark-UI"),(4369,"Erlang-EPMD"),(4443,"HTTPS"),
        (5000,"HTTP-Alt"),(5001,"HTTP-Alt"),(5060,"SIP"),(5061,"SIP-TLS"),(5222,"XMPP"),
        (5269,"XMPP-Server"),(5353,"mDNS"),(5432,"PostgreSQL"),(5601,"Kibana"),(5672,"AMQP"),
        (5900,"VNC"),(5901,"VNC"),(5984,"CouchDB"),(5985,"WinRM"),(5986,"WinRM-SSL"),
        (6000,"X11"),(6379,"Redis"),(6443,"Kubernetes-API"),(6660,"IRC"),(6661,"IRC"),
        (6662,"IRC"),(6663,"IRC"),(6664,"IRC"),(6665,"IRC"),(6666,"IRC"),
        (6667,"IRC"),(6668,"IRC"),(6669,"IRC"),(7000,"HTTP-Alt"),(7001,"HTTP-Alt"),
        (7070,"RTSP"),(7077,"Spark"),(7100,"Font-Service"),(7443,"HTTPS"),(8000,"HTTP-Dev"),
        (8001,"HTTP-Alt"),(8006,"Proxmox"),(8008,"HTTP-Alt"),(8009,"Chromecast"),(8080,"HTTP-Proxy"),
        (8081,"HTTP-Alt"),(8086,"InfluxDB"),(8087,"HTTP-Alt"),(8088,"HTTP-Alt"),(8090,"HTTP-Alt"),
        (8096,"Jellyfin"),(8112,"Deluge"),(8123,"Home-Assistant"),(8181,"HTTP-Alt"),(8200,"UPnP"),
        (8222,"HTTP-Alt"),(8291,"MikroTik-API"),(8333,"Bitcoin"),(8384,"Syncthing"),(8443,"HTTPS"),
        (8500,"Consul"),(8834,"Nessus"),(8880,"HTTP-Alt"),(8883,"MQTT-TLS"),(8888,"HTTP-Alt"),
        (8920,"Emby"),(8983,"Solr"),(9000,"HTTP-Alt"),(9001,"HTTP-Alt"),(9042,"Cassandra"),
        (9080,"HTTP-Alt"),(9090,"HTTP-Admin"),(9091,"Transmission"),(9092,"Kafka"),(9100,"JetDirect"),
        (9200,"Elasticsearch"),(9300,"Elasticsearch"),(9443,"HTTPS"),(9800,"WebDAV"),(9981,"HTTP-Alt"),
        (10000,"Webmin"),(10250,"Kubelet"),(11211,"Memcached"),(15672,"RabbitMQ"),(19999,"Netdata"),
        (20000,"HTTP-Alt"),(25565,"Minecraft"),(27015,"Source-Engine"),(27017,"MongoDB"),(32400,"Plex"),
        (32469,"Plex-DLNA"),(37777,"DVR"),(49152,"UPnP"),(50000,"SAP"),
    };

    internal static readonly (int Port, string Name)[] UdpPorts =
    {
        (53,"DNS"), (123,"NTP"), (137,"NetBIOS"), (161,"SNMP"), (1900,"SSDP"), (5353,"mDNS"),
    };

    /// <summary>Unroutable documentation address (TEST-NET-3): no device can own it, so a port
    /// that answers there is answered by the network in between.</summary>
    internal const string CanaryIp = "203.0.113.1";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(NetProtocol, int), Task<bool>> CanaryCache = new();

    /// <summary>Test seam, per async flow so parallel tests cannot see each other's override:
    /// replaces the canary probe and applies it to loopback targets too.</summary>
    internal static readonly AsyncLocal<Func<NetProtocol, int, Task<bool>>?> CanaryOverride = new();

    /// <summary>The first time a port answers, the same port is probed once on the canary
    /// address (cached for the session). When the canary answers too, the network intercepts
    /// that port and every answer on it is only Probable. The cached canary probe is shared by
    /// every scan and bounded by its own timeout, so one caller's token is not handed to it (a
    /// stop would otherwise cache a false "not intercepted"); the caller just stops waiting for
    /// it. A stop returns null: the answer was not vetted, so it is not reported.</summary>
    internal static async Task<NetService?> VetAsync(string ip, NetService found, CancellationToken ct)
    {
        var probe = CanaryOverride.Value;
        if (probe is null && (IsLoopback(ip) || ip == CanaryIp || IsOnLink(ip))) return found;
        bool intercepted;
        try
        {
            var canary = probe is not null
                ? probe(found.Protocol, found.Port)
                : CanaryCache.GetOrAdd((found.Protocol, found.Port), k => CanaryAnswersAsync(k.Item1, k.Item2));
            intercepted = await canary.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Log.Debug(LogArea.Ports, "Interception check failed: " + ex.GetType().Name); intercepted = false; }
        return intercepted ? found with { Confidence = ServiceConfidence.Probable, AnsweredByNetwork = true } : found;
    }

    private static async Task<bool> CanaryAnswersAsync(NetProtocol protocol, int port)
    {
        try
        {
            var hit = protocol == NetProtocol.Tcp
                ? await ProbeTcpRaw(CanaryIp, port, "")
                : await ProbeUdpRaw(CanaryIp, port, "");
            return hit is not null;
        }
        catch (Exception ex) { Log.Debug(LogArea.Ports, "Reachability check failed: " + ex.GetType().Name); return false; }
    }

    private static bool IsLoopback(string ip) => IPAddress.TryParse(ip, out var a) && IPAddress.IsLoopback(a);

    /// <summary>A target inside one of this machine's own subnets does not travel through the
    /// gateway, so a gateway that intercepts a port says nothing about it.</summary>
    private static bool IsOnLink(string ip)
    {
        if (!IPAddress.TryParse(ip, out var target) || target.AddressFamily != AddressFamily.InterNetwork) return false;
        try
        {
            var t = target.GetAddressBytes();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var a = ua.Address.GetAddressBytes();
                    var m = ua.IPv4Mask.GetAddressBytes();
                    var same = true;
                    for (var i = 0; i < 4 && same; i++) same = (a[i] & m[i]) == (t[i] & m[i]);
                    if (same) return true;
                }
            }
        }
        catch (Exception ex) { Log.Warn(LogArea.Ports, "Topology check failed", ex); /* unknown topology: treat as off-link and let the canary decide */ }
        return false;
    }

    /// <summary>Best-effort version string from the service's own banner/headers.
    /// Only a handful of protocols volunteer one; everything else stays name-only.</summary>
    private static async Task<string?> GrabBanner(TcpClient client, string ip, int port, string name, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BannerTimeoutMs);
            if (NetService.HttpNames.Contains(name)) return await HttpServerHeader(client.GetStream(), ip, cts.Token);
            return name switch
            {
                "HTTPS" => await HttpsServerHeader(client, ip, cts.Token),
                "SSH" or "FTP" or "SMTP" or "POP3" or "IMAP" => await ReadLine(client.GetStream(), cts.Token),
                _ => null,
            };
        }
        catch (Exception ex) { Log.Debug(LogArea.Ports, "Banner grab failed: " + ex.GetType().Name); return null; }
    }

    private static async Task<string?> ReadLine(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[512];
        int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
        if (n <= 0) return null;
        var line = Encoding.ASCII.GetString(buf, 0, n).Split('\r', '\n')[0].Trim();
        // Sanitize before the emptiness check - a banner made entirely of
        // control characters must come out as "nothing", not a blank string.
        var clean = line.Length > 0 ? Truncate(line) : "";
        return clean.Length > 0 ? clean : null;
    }

    private static async Task<string?> HttpServerHeader(NetworkStream stream, string host, CancellationToken ct)
    {
        var req = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req, ct);
        return ParseServerHeader(await ReadAll(stream, ct));
    }

    private static async Task<string?> HttpsServerHeader(TcpClient client, string host, CancellationToken ct)
    {
        // LAN devices commonly present self-signed/expired certs; this is a
        // one-shot banner probe, not a trust decision, so validation is skipped.
        using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        // A device that accepts the TCP connection but never completes (or never
        // starts) the TLS handshake would hang here forever, so the handshake takes ct
        // like every other step in this probe.
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = host, EnabledSslProtocols = System.Security.Authentication.SslProtocols.None }, ct);
        var req = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await ssl.WriteAsync(req, ct);
        var buf = new byte[2048];
        int n = await ssl.ReadAsync(buf.AsMemory(0, buf.Length), ct);
        return ParseServerHeader(n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : "");
    }

    private static async Task<string> ReadAll(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[2048];
        int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
        return n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : "";
    }

    private static string? ParseServerHeader(string response)
    {
        var line = response.Split('\n')
            .FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
        var val = line?[7..].Trim();
        if (string.IsNullOrEmpty(val)) return null;
        var clean = Truncate(val);
        return clean.Length > 0 ? clean : null;
    }

    // Strips control characters as well as capping length - a banner
    // is untrusted text from the probed device.
    private static string Truncate(string s) => NetworkText.Sanitize(s);

    /// <summary>A UDP reply proves nothing by itself - a firewall/NAT device or an
    /// unrelated ICMP-driven error path can produce one too. Only a reply
    /// matching the protocol's own expected shape counts as Confirmed; NetBIOS has no
    /// cheap shape check and always stays Probable.</summary>
    internal static bool IsConfirmedUdpReply(int port, byte[] resp) => port switch
    {
        53 => resp.Length >= 12 && resp[0] == 0x12 && resp[1] == 0x34 && (resp[2] & 0x80) != 0,
        123 => resp.Length >= 48 && (resp[0] & 0x07) is 4 or 2,
        161 => resp.Length > 2 && resp[0] == 0x30 &&
               Array.IndexOf(resp, (byte)0xA2, 0, Math.Min(resp.Length, 20)) >= 0,
        1900 => resp.Length >= 12 &&
                Encoding.ASCII.GetString(resp, 0, 12).StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
        5353 => MdnsHelper.ParsePtrAnswer(resp) is not null,
        _ => false,
    };

    // Root-name DNS query (QTYPE=A) - any real DNS server answers something.
    private static byte[] BuildDnsQuery()
        => new byte[] { 0x12,0x34, 0x01,0x00, 0x00,0x01, 0x00,0x00, 0x00,0x00, 0x00,0x00,
                        0x00, 0x00,0x01, 0x00,0x01 };

    // NTP v3 client request (LI=0, VN=3, Mode=3), rest zero.
    private static readonly byte[] NtpProbe = BuildNtpProbe();
    private static byte[] BuildNtpProbe() { var b = new byte[48]; b[0] = 0x1B; return b; }

    // SNMPv1 GetRequest for OID 1.3.6.1.2.1.1.1.0 (sysDescr.0), with a configurable
    // community string. The PDU past the community
    // field never changes, so only that one field is rebuilt per call - clamped to 32
    // bytes so the outer SEQUENCE length always fits BER's single-byte length form.
    // Internal (not private) so PortScannerTests can assert on the encoding directly.
    internal static byte[] BuildSnmpGetSysDescr(string community)
    {
        var commBytes = Encoding.ASCII.GetBytes(string.IsNullOrEmpty(community) ? "public" : community);
        if (commBytes.Length > 32) commBytes = commBytes[..32];
        byte[] version = { 0x02, 0x01, 0x00 };
        byte[] communityField = { 0x04, (byte)commBytes.Length };
        byte[] pdu =
        {
            0xA0,0x1C, 0x02,0x04,0x00,0x00,0x00,0x01, 0x02,0x01,0x00, 0x02,0x01,0x00,
            0x30,0x0E, 0x30,0x0C, 0x06,0x08,0x2B,0x06,0x01,0x02,0x01,0x01,0x01,0x00, 0x05,0x00,
        };
        var content = version.Concat(communityField).Concat(commBytes).Concat(pdu).ToArray();
        byte[] header = { 0x30, (byte)content.Length };
        return header.Concat(content).ToArray();
    }

    // NetBIOS NBSTAT (node status) query for the wildcard name "*".
    internal static readonly byte[] NetBiosNbstatProbe =
    {
        0x82,0x28, 0x00,0x00, 0x00,0x01, 0x00,0x00, 0x00,0x00, 0x00,0x00,
        0x20,0x43,0x4B,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,
        0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,
        0x00, 0x00,0x21, 0x00,0x01,
    };

    // Unicast SSDP M-SEARCH - many UPnP devices answer a direct (non-multicast) probe too.
    internal static readonly byte[] SsdpProbe = Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");

    /// <summary>Open the given service on the device with the right client.</summary>
    public static void Open(string ip, NetService s)
    {
        if (!Net.AddressSyntax.IsValidAddress(ip)) return;
        if (!s.IsOpenable) return;
        try
        {
            if (NetService.HttpNames.Contains(s.Name)) Shell(UrlFor("http", ip, s.Port, 80));
            else switch (s.Name)
            {
                case "RDP": Start("mstsc", $"/v:{ip}:{s.Port}"); break;
                case "SMB": Shell($@"\\{(ip.Contains(':') ? ip.Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net" : ip)}"); break;
                // Launch ssh.exe directly (ArgumentList, no shell parsing)
                // instead of routing through cmd.exe - one less process in the
                // chain and ip/port never pass through a command-line parser.
                case "SSH": StartSsh(ip, s.Port); break;
                case "FTP": Shell(UrlFor("ftp", ip, s.Port, 21)); break;
                case "HTTPS": Shell(UrlFor("https", ip, s.Port, 443)); break;
            }
        }
        catch (Exception ex) { LogFailure(ip, s, ex); }
    }

    private static string UrlFor(string scheme, string ip, int port, int defaultPort)
        => port == defaultPort ? $"{scheme}://{HostPart(ip)}" : $"{scheme}://{HostPart(ip)}:{port}";

    private static string HostPart(string ip) => ip.Contains(':') ? "[" + ip.Replace("%", "%25") + "]" : ip;

    // Goes through Log: one path (next to the exe) and one size cap, and the
    // central redaction hides the device address down to its last octet.
    private static void LogFailure(string ip, NetService s, Exception ex)
        => Log.Warn(LogArea.Ports, $"Opening {ip}:{s.Port} {s.Name} failed", ex);

    private static void Shell(string target)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });

    private static void Start(string exe, string args)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });

    private static void StartSsh(string ip, int port)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ssh") { UseShellExecute = true };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add(ip);
        System.Diagnostics.Process.Start(psi);
    }
}
