using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Discovery;

namespace IpScanner.Core.Cli;

/// <summary>The scan settings a command line and the settings page have in common. Both routes are mapped
/// into this one record, so the same flags always mean the same scan.</summary>
/// <param name="Timing">Timing template 0 to 5.</param>
/// <param name="SkipDiscovery">Treat every target as up.</param>
/// <param name="PingOnly">Find the live hosts and scan no ports.</param>
/// <param name="Ports">"" for the standard list, "fast", "top:N" or a port list as typed after -p.</param>
/// <param name="ServiceDetection">Identify the service behind each open port.</param>
/// <param name="OsDetection">Estimate the operating system.</param>
public sealed record ScanPlan(int Timing, bool SkipDiscovery, bool PingOnly, string Ports, bool ServiceDetection, bool OsDetection)
{
    public const string PortsStandard = "";
    public const string PortsFast = "fast";
    private const string TopPrefix = "top:";
    private const int StandardCount = 1000;
    private const int FastCount = 100;

    public static ScanPlan FromOptions(CliOptions o)
    {
        bool ping = o.Mode == CliMode.Ping;
        string ports = o.Ports ?? (o.TopPorts is { } n ? TopPrefix + n.ToString(CultureInfo.InvariantCulture)
            : o.Fast ? PortsFast : PortsStandard);
        return new ScanPlan(o.Timing ?? ScanTiming.Normal, o.SkipDiscovery, ping, ping ? PortsStandard : ports,
            o.ServiceDetection, o.OsDetection);
    }

    /// <summary>Reads the plan out of the settings the page edits; the port scan is on when the ports are exported.</summary>
    public static ScanPlan FromConfig(ScanConfig c)
    {
        bool skip = DiscoveryModes.Parse(c.DiscoveryModes).Contains(DiscoveryMode.SkipDiscovery);
        return new ScanPlan(Math.Clamp(c.PortScanTiming, 0, ScanTiming.TemplateCount - 1), skip, !c.ExportPorts,
            c.ScanPortRange ?? PortsStandard, c.PortServiceDetection, c.OsFingerprint);
    }

    /// <summary>Writes the plan into the settings, leaving every unrelated setting alone.</summary>
    public void ApplyTo(ScanConfig c)
    {
        c.PortScanTiming = Timing;
        c.DiscoveryModes = WithSkip(c.DiscoveryModes, SkipDiscovery);
        c.ExportPorts = !PingOnly;
        c.ScanPortRange = Ports;
        c.PortServiceDetection = ServiceDetection;
        c.OsFingerprint = OsDetection;
    }

    /// <summary>The port text when a plan can run it, else the standard list.</summary>
    public static string NormalizePorts(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > 200) return PortsStandard;
        if (t.Length == 0 || t == PortsFast) return t;
        if (t.StartsWith(TopPrefix, StringComparison.Ordinal))
            return int.TryParse(t.AsSpan(TopPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? t : PortsStandard;
        return PortSpec.TryParse(t, out _) ? t : PortsStandard;
    }

    /// <summary>The discovery mode keys the plan runs with when the settings carry no choice of their own.</summary>
    public string DiscoveryModeKeys => SkipDiscovery ? "skip" : DiscoveryModes.DefaultModes;

    /// <summary>The ports a scan of this plan probes (both protocols; callers narrow with <see cref="PortSpec.Only"/>).</summary>
    public PortSpec ResolvePorts()
    {
        if (Ports.Length == 0) return PortSpec.Top(StandardCount);
        if (Ports == PortsFast) return PortSpec.Top(FastCount);
        if (Ports.StartsWith(TopPrefix, StringComparison.Ordinal)
            && int.TryParse(Ports.AsSpan(TopPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return PortSpec.Top(n);
        return PortSpec.TryParse(Ports, out var spec) ? spec : PortSpec.Top(StandardCount);
    }

    /// <summary>The plan with the parts a ping-only run cannot honor dropped; what a command line can say.</summary>
    public ScanPlan Normalized() => PingOnly ? this with { SkipDiscovery = false, Ports = PortsStandard, ServiceDetection = false, OsDetection = false } : this;

    /// <summary>The flags that spell the plan. With <paramref name="runnable"/> the default timing is left out and the
    /// safety gate a full port scan needs is added, so the line runs as it stands.</summary>
    public string Flags(bool runnable)
    {
        var p = Normalized();
        var tokens = new List<string>();
        if (!runnable || p.Timing != ScanTiming.Normal)
            tokens.AddRange(CliFlags.ById("T").Tokens(p.Timing.ToString(CultureInfo.InvariantCulture)));
        if (p.SkipDiscovery) tokens.AddRange(CliFlags.ById("Pn").Tokens());
        if (p.PingOnly) tokens.AddRange(CliFlags.ById("sn").Tokens());
        else
        {
            if (p.Ports == PortsFast) tokens.AddRange(CliFlags.ById("F").Tokens());
            else if (p.Ports.StartsWith(TopPrefix, StringComparison.Ordinal))
                tokens.AddRange(CliFlags.ById("top-ports").Tokens(p.Ports[TopPrefix.Length..]));
            else if (p.Ports.Length > 0) tokens.AddRange(CliFlags.ById("p").Tokens(p.Ports));
            if (p.ServiceDetection) tokens.AddRange(CliFlags.ById("sV").Tokens());
            if (p.OsDetection) tokens.AddRange(CliFlags.ById("O").Tokens());
            if (runnable && NeedsFullScanGate(p)) tokens.AddRange(CliFlags.ById("allow-full-scan").Tokens());
        }
        return string.Join(' ', tokens);
    }

    private static bool NeedsFullScanGate(ScanPlan p)
    {
        // Same rule as the command line: only a chosen list or top-N count is gated, never the standard or fast set.
        if (p.Ports == PortsStandard || p.Ports == PortsFast) return false;
        var spec = p.ResolvePorts();
        return PortScanGate.NeedsConfirmation(spec.Tcp.Count + spec.Udp.Count);
    }

    private static string WithSkip(string current, bool skip)
    {
        if (skip) return "skip";
        var rest = DiscoveryModes.Parse(current).Where(m => m != DiscoveryMode.SkipDiscovery).ToList();
        return rest.Count == 0 ? DiscoveryModes.DefaultModes : DiscoveryModes.Format(rest);
    }
}
