using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>One rule of a probe: when the response matches, the service is named and the
/// capture groups ($1..$9 in the templates) fill product, version and extra info.</summary>
internal sealed record ProbeMatch(Regex Pattern, string Service, string? Product, string? Version, string? Info);

/// <summary>What a probe response told about the service. A soft match narrows the protocol
/// (it speaks TLS, it looks like IMAP) without naming a product or version.</summary>
internal sealed record ServiceMatch(string Service, string? Product, string? Version, string? Info, bool Soft)
{
    /// <summary>Product, version and extra info as one short line, or null when there is none.</summary>
    public string? Label
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Product)) sb.Append(Product);
            if (!string.IsNullOrEmpty(Version)) sb.Append(sb.Length > 0 ? " " : "").Append(Version);
            if (!string.IsNullOrEmpty(Info))
            {
                if (sb.Length > 0) sb.Append(" (").Append(Info).Append(')');
                else sb.Append(Info);
            }
            return sb.Length == 0 ? null : NetworkText.Sanitize(sb.ToString(), 100);
        }
    }
}

/// <summary>Session details seen while talking TLS to a port: the same facts the certificate tool shows.</summary>
internal sealed record TlsInfo(string Protocol, string? Subject, string? Issuer, DateTime? NotAfter);

/// <summary>Outcome of the probe run for one open port.</summary>
internal sealed record ServiceDetection(ServiceMatch? Match, TlsInfo? Tls, bool OverTls);

internal sealed record ServiceProbe(string Name, int Rarity, int[] Ports, bool OverTls, byte[] Payload,
    IReadOnlyList<ProbeMatch> Matches, IReadOnlyList<ProbeMatch> Soft)
{
    public ServiceMatch? Match(string text)
    {
        foreach (var rule in Matches)
        {
            var m = TryMatch(rule, text);
            if (m is not null) return Build(rule, m, soft: false);
        }
        return null;
    }

    public ServiceMatch? MatchSoft(string text)
    {
        foreach (var rule in Soft)
        {
            var m = TryMatch(rule, text);
            if (m is not null) return Build(rule, m, soft: true);
        }
        return null;
    }

    private static System.Text.RegularExpressions.Match? TryMatch(ProbeMatch rule, string text)
    {
        try
        {
            var m = rule.Pattern.Match(text);
            return m.Success ? m : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static ServiceMatch Build(ProbeMatch rule, System.Text.RegularExpressions.Match m, bool soft)
        => new(rule.Service, Fill(rule.Product, m), Fill(rule.Version, m), Fill(rule.Info, m), soft);

    // Banner text is the remote device's own words: sanitized before it goes anywhere.
    private static string? Fill(string? template, System.Text.RegularExpressions.Match m)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var s = Regex.Replace(template, @"\$(\d)", d =>
        {
            int i = d.Groups[1].Value[0] - '0';
            return i < m.Groups.Count ? m.Groups[i].Value : "";
        });
        s = NetworkText.Sanitize(s);
        return s.Length == 0 ? null : s;
    }
}

/// <summary>The probe collection, loaded from the embedded <c>service-probes.json</c>.</summary>
internal sealed class ServiceProbeDb
{
    private static readonly Lazy<ServiceProbeDb> DefaultDb = new(LoadEmbedded);

    public IReadOnlyList<ServiceProbe> Probes { get; }

    public ServiceProbeDb(IReadOnlyList<ServiceProbe> probes) => Probes = probes;

    public static ServiceProbeDb Default => DefaultDb.Value;

    /// <summary>The probes for one port at the given intensity: the listen-only probe first, then those
    /// that name this port, then the generic ones, each group by rarity. A probe runs when its rarity
    /// does not exceed the intensity.</summary>
    public IReadOnlyList<ServiceProbe> Plan(int port, int intensity, bool overTls = false)
        => Probes
            .Where(p => p.Rarity <= intensity && (p.Ports.Length == 0 || p.Ports.Contains(port)) && (!overTls || p.OverTls)
                        && MaySend(p, port))
            .OrderBy(p => p.Rarity == 0 ? 0 : p.Ports.Length > 0 ? 1 : 2)
            .ThenBy(p => p.Rarity)
            .ToList();

    /// <summary>Raw-print ports hand every received byte to the printer, so a probe payload there prints
    /// pages of junk. They get the listen-only probe; the IPP port also takes the plain HTTP request.</summary>
    internal static bool IsRawPrintPort(int port) => port is >= 9100 and <= 9107 or 515 or 631;

    private static bool MaySend(ServiceProbe probe, int port)
    {
        if (probe.Payload.Length == 0 || !IsRawPrintPort(port)) return true;
        return port == 631 && probe.Name == "GetRequest";
    }

    public static ServiceProbeDb Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<DbDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new DbDto();
        var probes = new List<ServiceProbe>();
        foreach (var p in doc.Probes ?? new List<ProbeDto>())
        {
            if (!string.Equals(p.Protocol ?? "tcp", "tcp", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] payload = !string.IsNullOrEmpty(p.PayloadHex) ? Convert.FromHexString(p.PayloadHex)
                : Encoding.Latin1.GetBytes(p.Payload ?? "");
            probes.Add(new ServiceProbe(p.Name ?? "", p.Rarity, p.Ports ?? Array.Empty<int>(), p.OverTls ?? true, payload,
                Rules(p.Matches), Rules(p.Soft)));
        }
        return new ServiceProbeDb(probes);
    }

    private static List<ProbeMatch> Rules(List<MatchDto>? list)
    {
        var rules = new List<ProbeMatch>();
        foreach (var m in list ?? new List<MatchDto>())
        {
            if (string.IsNullOrEmpty(m.Pattern) || string.IsNullOrEmpty(m.Service)) continue;
            try
            {
                // Singleline so binary responses (a dot over a newline byte) match; the timeout keeps a
                // crafted response from pinning a core in a backtracking pattern.
                var rx = new Regex(m.Pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                rules.Add(new ProbeMatch(rx, m.Service, m.Product, m.Version, m.Info));
            }
            catch (ArgumentException ex) { Log.Warn(LogArea.Ports, "Bad service probe pattern skipped", ex); }
        }
        return rules;
    }

    private static ServiceProbeDb LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("service-probes.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return new ServiceProbeDb(Array.Empty<ServiceProbe>());
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Ports, "Service probe database could not be loaded", ex);
            return new ServiceProbeDb(Array.Empty<ServiceProbe>());
        }
    }

    private sealed class DbDto { public List<ProbeDto>? Probes { get; set; } }

    private sealed class ProbeDto
    {
        public string? Name { get; set; }
        public string? Protocol { get; set; }
        public int[]? Ports { get; set; }
        public int Rarity { get; set; }
        public bool? OverTls { get; set; }
        public string? Payload { get; set; }
        public string? PayloadHex { get; set; }
        public List<MatchDto>? Matches { get; set; }
        public List<MatchDto>? Soft { get; set; }
    }

    private sealed class MatchDto
    {
        public string? Pattern { get; set; }
        public string? Service { get; set; }
        public string? Product { get; set; }
        public string? Version { get; set; }
        public string? Info { get; set; }
    }
}

/// <summary>One connection to the port under test, plain or wrapped in TLS.</summary>
internal interface IProbeSession : IAsyncDisposable
{
    TlsInfo? Tls { get; }

    Task WriteAsync(byte[] data, CancellationToken ct);

    /// <summary>The response bytes, capped at <see cref="ServiceProbeRunner.MaxResponseBytes"/>; empty when
    /// the service stayed silent for the timeout or closed the connection.</summary>
    Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct);
}

/// <summary>Runs the probe collection against one open port: listen first, then the probes by rarity and
/// port, and finally the same set inside TLS when the plain attempts point to it.</summary>
internal static class ServiceProbeRunner
{
    public const int MaxResponseBytes = 4096;
    public const int MaxProbesPerPort = 8;
    public const int DefaultIntensity = 7;
    /// <summary>Time a routine scan spends on one port, in banner timeouts: the listen-only probe plus one more.</summary>
    public const int RoutineBudgetFactor = 2;

    internal delegate Task<IProbeSession?> SessionOpener(bool tls, CancellationToken ct);

    /// <summary>Null when the caller stopped; otherwise what was learned (possibly nothing).</summary>
    public static async Task<ServiceDetection?> RunAsync(SessionOpener open, ServiceProbeDb db, int port, int intensity,
        int readTimeoutMs, CancellationToken ct, int budgetMs = 0)
    {
        intensity = Math.Clamp(intensity, 0, 9);
        var watch = Stopwatch.StartNew();
        bool OutOfTime() => budgetMs > 0 && watch.ElapsedMilliseconds >= budgetMs;
        try
        {
            ServiceMatch? soft = null;
            bool anyData = false, tlsHint = false, first = true;
            int attempts = 0; // one budget for the plain and the TLS pass
            foreach (var probe in db.Plan(port, intensity))
            {
                if (attempts++ >= MaxProbesPerPort || OutOfTime()) break;
                var ex = await ExchangeAsync(open, false, probe, readTimeoutMs, ct);
                if (ex is null)
                {
                    if (first) return new ServiceDetection(null, null, false); // nothing to talk to
                    continue;
                }
                first = false;
                if (ex.Data.Length == 0) continue;
                anyData = true;
                var text = Encoding.Latin1.GetString(ex.Data, 0, Math.Min(ex.Data.Length, MaxResponseBytes));
                if (probe.Match(text) is { } hard) return new ServiceDetection(hard, null, false);
                soft ??= probe.MatchSoft(text);
                if (soft is { Service: "TLS" }) tlsHint = true;
                if (probe.Rarity == 0) break; // the service spoke first, so it is not waiting for a request
            }

            // A TLS hello is payload too: a raw-print port would print it.
            if (intensity >= 2 && (tlsHint || !anyData) && (port == 631 || !ServiceProbeDb.IsRawPrintPort(port)))
            {
                TlsInfo? tls = null;
                bool up = false;
                foreach (var probe in db.Plan(port, intensity, overTls: true))
                {
                    if (attempts++ >= MaxProbesPerPort || OutOfTime()) break;
                    var ex = await ExchangeAsync(open, true, probe, readTimeoutMs, ct);
                    if (ex is null)
                    {
                        if (!up) break; // the handshake itself failed: not a TLS service
                        continue;
                    }
                    up = true;
                    tls ??= ex.Tls;
                    if (ex.Data.Length == 0) continue;
                    var text = Encoding.Latin1.GetString(ex.Data, 0, Math.Min(ex.Data.Length, MaxResponseBytes));
                    if (probe.Match(text) is { } hard) return new ServiceDetection(hard, tls, true);
                    if (probe.Rarity == 0) break;
                }
                if (up) return new ServiceDetection(new ServiceMatch("TLS", null, null, null, true), tls, true);
            }
            return new ServiceDetection(soft, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
    }

    private sealed record Exchange(byte[] Data, TlsInfo? Tls);

    private static async Task<Exchange?> ExchangeAsync(SessionOpener open, bool tls, ServiceProbe probe, int readTimeoutMs,
        CancellationToken ct)
    {
        IProbeSession? session = null;
        try
        {
            session = await open(tls, ct);
            if (session is null) return null;
            if (probe.Payload.Length > 0) await session.WriteAsync(probe.Payload, ct);
            var data = await session.ReadAsync(readTimeoutMs, ct);
            return new Exchange(data.Length > MaxResponseBytes ? data[..MaxResponseBytes] : data, session.Tls);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug(LogArea.Ports, "Service probe failed: " + ex.GetType().Name);
            return session is null ? null : new Exchange(Array.Empty<byte>(), session.Tls);
        }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    /// <summary>The result laid over what the port table already knew. A named match renames an unknown
    /// port and corrects a wrong name; a soft match only names a port that had no name, and is then
    /// Probable. The TLS facts ride along either way.</summary>
    public static NetService Apply(NetService svc, ServiceDetection d)
    {
        var result = d.Tls is null ? svc : svc with { Tls = d.Tls };
        if (d.Match is not { } m) return result;
        bool httpName = NetService.HttpNames.Contains(svc.Name) || svc.Name == "HTTPS";
        if (m.Soft)
        {
            return string.IsNullOrEmpty(svc.Name)
                ? result with { Name = m.Service, Confidence = ServiceConfidence.Probable }
                : result;
        }
        string name = m.Service == "HTTP"
            ? (httpName ? svc.Name : d.OverTls ? "HTTPS" : "HTTP")
            : m.Service;
        var label = m.Label;
        if (d.OverTls && m.Service != "HTTP")
            label = label is null ? "TLS" : label + " (TLS)";
        return result with { Name = name, Version = label ?? svc.Version, Confidence = ServiceConfidence.Confirmed };
    }

    /// <summary>The real thing: probes a TCP port of the host through fresh connections.</summary>
    public static Task<ServiceDetection?> IdentifyAsync(string ip, int port, int intensity, CancellationToken ct)
        => RunAsync(Throttled((tls, token) => TcpProbeSession.OpenAsync(ip, port, tls, token), PortScanner.CurrentServiceThrottle),
            ServiceProbeDb.Default, port, intensity, PortScanner.BannerTimeoutMs, ct, BudgetFor(intensity, PortScanner.BannerTimeoutMs));

    /// <summary>The per-port time cap: none for the port tool or a raised intensity, else two banner timeouts.</summary>
    internal static int BudgetFor(int intensity, int bannerTimeoutMs)
        => PortScanner.DeepDetection || intensity > DefaultIntensity ? 0 : RoutineBudgetFactor * bannerTimeoutMs;

    /// <summary>Every connection beyond the scan's own probe waits for the run's delay and rate rules.</summary>
    internal static SessionOpener Throttled(SessionOpener open, ProbeThrottle? throttle)
    {
        if (throttle is null) return open;
        return async (tls, ct) =>
        {
            await throttle.WaitAsync(ct);
            return await open(tls, ct);
        };
    }
}

internal sealed class TcpProbeSession : IProbeSession
{
    private readonly TcpClient _client;
    private readonly Stream _stream;

    private TcpProbeSession(TcpClient client, Stream stream, TlsInfo? tls)
    {
        _client = client;
        _stream = stream;
        Tls = tls;
    }

    public TlsInfo? Tls { get; }

    /// <summary>The TLS handshake wait: never shorter than the banner wait, so a slow stack is not reported as plain.</summary>
    internal static int HandshakeTimeoutMs => Math.Max(PortScanner.EffectiveConnectTimeoutMs, PortScanner.BannerTimeoutMs);

    /// <summary>Null when the connect or the TLS handshake fails or times out; the caller's stop propagates.</summary>
    public static async Task<IProbeSession?> OpenAsync(string ip, int port, bool tls, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            PortScanner.ReadRefusalPromptly(client.Client);
            if (NetworkDetector.SourceFor(ip) is { } srcAddr)
                client.Client.Bind(new IPEndPoint(srcAddr, 0));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PortScanner.EffectiveConnectTimeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);
            if (!tls) return new TcpProbeSession(client, client.GetStream(), null);

            X509Certificate2? seen = null;
            try
            {
                var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
                {
                    if (cert is not null) seen = new X509Certificate2(cert);
                    return true; // a probe, not a trust decision: LAN devices ship self-signed certificates
                });
                // Slow embedded TLS stacks need longer than a connect: the handshake has its own timer.
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(HandshakeTimeoutMs);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = ip, EnabledSslProtocols = System.Security.Authentication.SslProtocols.None }, tlsCts.Token);
                var info = new TlsInfo(ssl.SslProtocol.ToString(),
                    seen is null ? null : NetworkText.Sanitize(seen.Subject, 120),
                    seen is null ? null : NetworkText.Sanitize(seen.Issuer, 120),
                    seen?.NotAfter.ToUniversalTime());
                return new TcpProbeSession(client, ssl, info);
            }
            finally { seen?.Dispose(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { client.Dispose(); throw; }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PortScanner.EffectiveConnectTimeoutMs);
        await _stream.WriteAsync(data, cts.Token);
    }

    public async Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct)
    {
        var buf = new byte[ServiceProbeRunner.MaxResponseBytes];
        int total = 0;
        try
        {
            while (total < buf.Length)
            {
                // The first byte may take the full banner wait; once a response started, only a short
                // pause ends it, so a chatty service cannot hold the scan.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(total == 0 ? timeoutMs : 60);
                int n;
                try { n = await _stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (n <= 0) break;
                total += n;
            }
        }
        catch (IOException) { /* reset by the peer: what arrived so far still counts */ }
        catch (SocketException) { }
        return buf[..total];
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* already gone */ }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
