using System.Net;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner.Checks;

/// <summary>Shared plumbing of the built-in checks: identity, the matching rule and finding helpers.</summary>
internal abstract class CheckBase : ICheck
{
    protected const int FindingMax = 120;
    protected const int MaxListed = 20;

    protected CheckBase(string id, CheckCategory category, bool perHost = false)
    {
        Id = id;
        Category = category;
        PerHost = perHost;
    }

    public string Id { get; }
    public CheckCategory Category { get; }
    public bool PerHost { get; }

    public abstract bool Matches(NetService service);
    public abstract Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct);

    protected CheckResult Done(NetService service, List<CheckFinding> findings) => CheckResult.Found(this, service, findings);
    protected CheckResult Silent(NetService service) => CheckResult.Silent(this, service);

    protected static CheckFinding F(string key, string value, int max = FindingMax) => new(key, NetworkText.Sanitize(value, max));

    protected static bool IsTcp(NetService s) => s.Protocol == NetProtocol.Tcp;
    protected static bool IsUdp(NetService s, int port) => s.Protocol == NetProtocol.Udp && s.Port == port;

    protected static bool IsWeb(NetService s)
        => IsTcp(s) && (NetService.HttpNames.Contains(s.Name) || s.Name == "HTTPS");

    protected static bool UsesTls(NetService s) => s.Name == "HTTPS" || s.Tls != null;
}

/// <summary>Page title and server software of a web service.</summary>
internal sealed class HttpTitleCheck : CheckBase
{
    private static readonly Regex TitleRx = new(@"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    private readonly ICheckIo _io;
    public HttpTitleCheck(ICheckIo io) : base("http-title", CheckCategory.Safe) => _io = io;

    public override bool Matches(NetService service) => IsWeb(service);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var reply = await _io.HttpGetAsync(target, service.Port, UsesTls(service), "/", ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var findings = new List<CheckFinding>();
        if (ExtractTitle(reply.Body) is { Length: > 0 } title) findings.Add(F("title", title));
        if (reply.Headers.TryGetValue("Server", out var server) && server.Length > 0) findings.Add(F("server", server));
        if (reply.Headers.TryGetValue("X-Powered-By", out var powered) && powered.Length > 0) findings.Add(F("poweredBy", powered));
        return Done(service, findings);
    }

    internal static string? ExtractTitle(string html)
    {
        try
        {
            var m = TitleRx.Match(html);
            if (!m.Success) return null;
            var text = SpaceRx.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), " ").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}

/// <summary>Which of the common protective response headers a web service leaves out.</summary>
internal sealed class HttpSecurityHeadersCheck : CheckBase
{
    private readonly ICheckIo _io;
    public HttpSecurityHeadersCheck(ICheckIo io) : base("http-headers", CheckCategory.Safe) => _io = io;

    public override bool Matches(NetService service) => IsWeb(service);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        bool tls = UsesTls(service);
        var reply = await _io.HttpGetAsync(target, service.Port, tls, "/", ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var missing = MissingHeaders(reply.Headers, tls);
        var findings = missing.Count == 0
            ? new List<CheckFinding> { new("allHeadersSet", "") }
            : missing.Select(h => new CheckFinding("missingHeader", h)).ToList();
        return Done(service, findings);
    }

    internal static List<string> MissingHeaders(IReadOnlyDictionary<string, string> headers, bool tls)
    {
        var missing = new List<string>();
        if (tls && !headers.ContainsKey("Strict-Transport-Security")) missing.Add("Strict-Transport-Security");
        if (!headers.ContainsKey("Content-Security-Policy")) missing.Add("Content-Security-Policy");
        if (!headers.ContainsKey("X-Content-Type-Options")) missing.Add("X-Content-Type-Options");
        // frame-ancestors in the CSP replaces X-Frame-Options.
        bool framed = headers.ContainsKey("X-Frame-Options")
            || (headers.TryGetValue("Content-Security-Policy", out var csp) && csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase));
        if (!framed) missing.Add("X-Frame-Options");
        if (!headers.ContainsKey("Referrer-Policy")) missing.Add("Referrer-Policy");
        return missing;
    }
}

/// <summary>Protocol version and certificate of a TLS service, read from its own handshake.</summary>
internal sealed class TlsCertificateCheck : CheckBase
{
    private static readonly HashSet<string> TlsNames = new(StringComparer.Ordinal)
    { "HTTPS", "SMTPS", "IMAPS", "POP3S", "LDAPS", "FTPS" };

    private readonly ICheckIo _io;
    public TlsCertificateCheck(ICheckIo io) : base("tls-cert", CheckCategory.Safe) => _io = io;

    public override bool Matches(NetService service) => IsTcp(service) && (service.Tls != null || TlsNames.Contains(service.Name));

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        await using var session = await _io.OpenTcpAsync(target, service.Port, true, ct).ConfigureAwait(false);
        if (session?.Tls is not { } tls) return Silent(service);
        var findings = new List<CheckFinding> { F("protocol", tls.Protocol) };
        if (tls.Subject is { Length: > 0 } subject) findings.Add(F("subject", subject));
        if (tls.Issuer is { Length: > 0 } issuer) findings.Add(F("issuer", issuer));
        if (tls.NotAfter is { } until)
        {
            findings.Add(new CheckFinding("validTo", until.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            if (until.ToUniversalTime() < DateTime.UtcNow) findings.Add(new CheckFinding("certExpired", ""));
        }
        return Done(service, findings);
    }
}
