using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner.Discovery;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Core.Export;

/// <summary>Renders a <see cref="ScanReport"/> as text, XML, JSON or grepable lines. Every host (GUI, CLI,
/// archive) goes through these, so the same scan gives the same bytes everywhere.</summary>
public static class ReportWriters
{
    public static string Render(ScanReport report, ReportFormat format, bool showReason = false) => format switch
    {
        ReportFormat.Text => Text(report, showReason),
        ReportFormat.Xml => Xml(report),
        ReportFormat.Json => Json(report),
        _ => Grepable(report)
    };

    // ---------------------------------------------------------------- JSON

    public static string Json(ScanReport report) => JsonSerializer.Serialize(report, ReportCodec.Json);

    /// <summary>Reads a report written by <see cref="Json"/>; the text-only display time is not part of the file.</summary>
    public static ScanReport? ParseJson(string json) => JsonSerializer.Deserialize<ScanReport>(json, ReportCodec.Json);

    // ---------------------------------------------------------------- XML

    public static string Xml(ScanReport report) => ReportCodec.Xml(w =>
    {
        var m = report.Meta;
        w.WriteStartDocument();
        w.WriteStartElement("scanReport");
        w.WriteAttributeString("schemaVersion", ScanReport.SchemaVersion.ToString(CultureInfo.InvariantCulture));

        w.WriteStartElement("meta");
        w.WriteAttributeString("tool", m.Tool);
        w.WriteAttributeString("version", m.ToolVersion);
        if (m.StartedAt is { } at) w.WriteAttributeString("startedAt", ReportCodec.Iso(at));
        w.WriteAttributeString("partial", Bool(m.Partial));
        Attr(w, "technique", m.Technique);
        Element(w, "command", m.Command);
        Element(w, "targets", m.Targets);
        w.WriteStartElement("network");
        w.WriteAttributeString("interface", ReportCodec.XmlSafe(m.Interface));
        Attr(w, "ip", m.LocalIp);
        Attr(w, "gateway", m.Gateway);
        Attr(w, "subnetMask", m.SubnetMask);
        foreach (var dns in m.Dns) w.WriteElementString("dns", ReportCodec.XmlSafe(dns));
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("hosts");
        foreach (var h in report.Hosts) WriteHost(w, h);
        w.WriteEndElement();

        if (report.Findings.Count > 0)
        {
            w.WriteStartElement("findings");
            foreach (var f in report.Findings)
            {
                w.WriteStartElement("finding");
                w.WriteAttributeString("ip", f.Ip);
                Attr(w, "mac", f.Mac);
                w.WriteAttributeString("kind", f.Kind);
                Attr(w, "detail", f.Detail);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndDocument();
    });

    private static void WriteHost(XmlWriter w, ReportHost h)
    {
        w.WriteStartElement("host");
        w.WriteAttributeString("ip", h.Ip);
        w.WriteAttributeString("addrtype", h.Family);
        w.WriteAttributeString("status", h.Status);
        Attr(w, "hostname", h.Hostname);
        Attr(w, "mac", h.Mac);
        Attr(w, "vendor", h.Vendor);
        w.WriteAttributeString("group", h.Group);
        Attr(w, "discovery", h.Discovery);
        Num(w, "avgMs", h.AvgMs);
        Num(w, "minMs", h.MinMs);
        Num(w, "maxMs", h.MaxMs);
        Num(w, "lastMs", h.LastMs);
        w.WriteAttributeString("pingsDone", h.PingsDone.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("pingsTarget", h.PingsTarget);
        w.WriteAttributeString("fromDb", Bool(h.FromDb));
        Element(w, "note", h.Note);
        if (h.Os is { } os)
        {
            w.WriteStartElement("os");
            w.WriteAttributeString("family", OsFamilyId(os.Family));
            w.WriteAttributeString("percent", os.Percent.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fromFingerprint", Bool(os.FromFingerprint));
            Attr(w, "match", os.Match);
            if (os.MatchPercent is { } fit) w.WriteAttributeString("matchPercent", fit.ToString(CultureInfo.InvariantCulture));
            if (os.Hops is { } hops) w.WriteAttributeString("hops", hops.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
        if (h.Ports.Count > 0)
        {
            w.WriteStartElement("ports");
            foreach (var p in h.Ports)
            {
                w.WriteStartElement("port");
                w.WriteAttributeString("number", p.Port.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("protocol", p.Protocol);
                w.WriteAttributeString("state", p.State);
                Attr(w, "reason", p.Reason);
                Attr(w, "service", p.Service);
                Attr(w, "version", p.Version);
                Attr(w, "confidence", p.Confidence);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        if (h.Checks.Count > 0)
        {
            w.WriteStartElement("checks");
            foreach (var c in h.Checks)
            {
                w.WriteStartElement("check");
                w.WriteAttributeString("id", c.Id);
                w.WriteAttributeString("category", c.Category);
                Attr(w, "service", c.Service);
                w.WriteAttributeString("outcome", c.Outcome);
                foreach (var f in c.Findings)
                {
                    w.WriteStartElement("finding");
                    w.WriteAttributeString("key", ReportCodec.XmlSafe(f.Key));
                    w.WriteAttributeString("value", ReportCodec.XmlSafe(f.Value));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>The embedded schema file the XML output validates against.</summary>
    public static string XmlSchema()
    {
        var asm = typeof(ReportWriters).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("scan-report.xsd", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static string OsFamilyId(OsFamily f) => f switch
    {
        OsFamily.Windows => "windows",
        OsFamily.UnixLike => "unixLike",
        OsFamily.NetworkGear => "networkGear",
        OsFamily.Printer => "printer",
        OsFamily.Media => "media",
        _ => "unknown"
    };

    private static string Bool(bool b) => b ? "true" : "false";

    private static void Attr(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteAttributeString(name, ReportCodec.XmlSafe(value));
    }

    private static void Num(XmlWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteAttributeString(name, ReportCodec.Num(v));
    }

    private static void Element(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteElementString(name, ReportCodec.XmlSafe(value));
    }

    // ---------------------------------------------------------------- grepable

    /// <summary>Comment lines start with '#'. Every host is exactly one line starting with "Host: ", fields
    /// separated by tabs; ports are "number/state/protocol//service//version/" entries separated by ", ".</summary>
    public static string Grepable(ScanReport report)
    {
        var m = report.Meta;
        var sb = new StringBuilder();
        sb.Append("# ").Append(m.Tool).Append(' ').Append(m.ToolVersion).Append(" scan report");
        if (m.StartedAt is { } at) sb.Append(" started ").Append(ReportCodec.Iso(at));
        sb.Append('\n');
        if (!string.IsNullOrEmpty(m.Command)) sb.Append("# Command: ").Append(ReportCodec.Field(m.Command)).Append('\n');
        if (!string.IsNullOrEmpty(m.Technique)) sb.Append("# Scan type: ").Append(ReportCodec.Field(m.Technique)).Append('\n');
        if (m.Partial) sb.Append("# Incomplete: the scan was cancelled\n");
        foreach (var h in report.Hosts)
        {
            sb.Append("Host: ").Append(h.Ip).Append(" (").Append(h.Hostname is null ? "" : ReportCodec.Field(h.Hostname).Replace(')', ' ')).Append(')');
            sb.Append("\tStatus: ").Append(h.Status switch
            {
                ReportBuilder.Online => "Up",
                ReportBuilder.Offline => "Down",
                _ => "Listed"
            });
            sb.Append("\tMAC: ").Append(ReportCodec.Field(h.Mac));
            sb.Append("\tVendor: ").Append(ReportCodec.Field(h.Vendor));
            sb.Append("\tPorts: ").Append(h.Ports.Count == 0 ? "-" : string.Join(", ", h.Ports.Select(GrepPort)));
            sb.Append("\tOS: ").Append(h.Os is null ? "-" : GrepOs(h.Os));
            if (h.Checks.Count > 0)
                sb.Append("\tChecks: ").Append(string.Join(", ", h.Checks.Select(c => $"{GrepToken(c.Id)}/{GrepToken(c.Outcome)}")));
            if (!string.IsNullOrEmpty(h.Note)) sb.Append("\tNote: ").Append(ReportCodec.Field(h.Note));
            sb.Append('\n');
        }
        foreach (var f in report.Findings)
            sb.Append("# Finding: ").Append(GrepToken(f.Kind)).Append(' ').Append(GrepToken(f.Ip)).Append(' ')
              .Append(ReportCodec.Field(f.Mac)).Append(' ').Append(ReportCodec.Field(f.Detail)).Append('\n');
        sb.Append("# Done: ").Append(report.Hosts.Count.ToString(CultureInfo.InvariantCulture)).Append(" hosts\n");
        return sb.ToString();
    }

    private static string GrepToken(string? s) => ReportCodec.Field(s).Replace('/', ' ').Replace(',', ' ');

    private static string GrepPort(ReportPort p) =>
        $"{p.Port}/{p.State}/{p.Protocol}//{(p.Service is null ? "" : GrepToken(p.Service))}//{(p.Version is null ? "" : GrepToken(p.Version))}/";

    private static string GrepOs(ReportOs os) =>
        ReportCodec.Field(OsDisplay.Text(new OsEstimate(os.Family, os.Percent, os.FromFingerprint, os.Match, os.MatchPercent, os.Hops)));

    // ---------------------------------------------------------------- text

    private const int MaxListedPerState = 25;

    /// <summary>The readable report. Labels follow the UI language; <paramref name="showReason"/> adds why each port got its state.</summary>
    public static string Text(ScanReport report, bool showReason = false)
    {
        var m = report.Meta;
        var unknown = Loc.ReportUnknown;
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine(Loc.ReportTitle);
        sb.AppendLine($"{Loc.ReportTimestamp}: {m.DisplayTime ?? (m.StartedAt is { } at ? ReportCodec.Iso(at) : "")}");
        if (!string.IsNullOrEmpty(m.Command)) sb.AppendLine($"{Loc.LogAreaCli}: {ReportCodec.Line(m.Command)}");
        if (m.Partial) sb.AppendLine(Loc.ReportPartialNote);
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();
        sb.AppendLine($"{Loc.ReportNetworkInfo}:");
        sb.AppendLine(new string('-', 30));
        // Padded to the widest label so a longer translation does not misalign the value column.
        int labelWidth = new[] { Loc.Interface, Loc.IpAddress, Loc.Gateway, Loc.Mask, Loc.Dns }.Max(l => l.Length) + 2;
        string Kv(string label, string value) => $"{(label + ":").PadRight(labelWidth)}{value}";
        sb.AppendLine(Kv(Loc.Interface, m.Interface.Length > 0 ? m.Interface : unknown));
        sb.AppendLine(Kv(Loc.IpAddress, m.LocalIp ?? unknown));
        if (m.Gateway is not null) sb.AppendLine(Kv(Loc.Gateway, m.Gateway));
        if (m.SubnetMask is not null) sb.AppendLine(Kv(Loc.Mask, m.SubnetMask));
        sb.AppendLine(Kv(Loc.Dns, m.Dns.Count > 0 ? string.Join(", ", m.Dns) : unknown));
        sb.AppendLine();

        // The status column holds a translated word; 14 fits the longest one with margin.
        const int statusWidth = 14;
        // The address column grows with the longest address so IPv6 rows keep the columns aligned.
        int ipWidth = Math.Max(16, report.Hosts.Select(h => h.Ip.Length).DefaultIfEmpty(0).Max() + 2);
        sb.AppendLine($"{Loc.IpAddress.PadRight(ipWidth)}{Loc.Status,-statusWidth}{Loc.Hostname,-24}{Loc.Group,-7}{Loc.Avg,-10}{Loc.Min,-10}{Loc.Max,-10}{Loc.Last,-10}{Loc.Pings,-12}{Loc.Mac,-18}{Loc.FoundBy,-14}{Loc.OsColumnHeader}");
        sb.AppendLine(new string('-', 126 + statusWidth - 9 + 14 + 34 + (ipWidth - 16)));
        static string Ms(double? v) => v is null ? "-" : v.Value.ToString("F2", CultureInfo.InvariantCulture) + "ms";
        foreach (var h in report.Hosts)
        {
            var target = h.PingsTarget == "inf" ? "∞" : h.PingsTarget;
            var status = h.Status switch
            {
                ReportBuilder.Listed => Loc.StatusListed,
                ReportBuilder.Online => Loc.StatusOnline,
                _ => Loc.StatusOffline
            };
            var os = h.Os is null ? "" : ReportCodec.Line(OsDisplay.Text(new OsEstimate(h.Os.Family, h.Os.Percent, h.Os.FromFingerprint, h.Os.Match, h.Os.MatchPercent, h.Os.Hops)));
            sb.AppendLine(
                $"{h.Ip.PadRight(ipWidth)}{status,-statusWidth}" +
                $"{(h.Hostname is null ? "-" : ReportCodec.Line(h.Hostname)),-24}{h.Group,-7}" +
                $"{Ms(h.AvgMs),-10}{Ms(h.MinMs),-10}{Ms(h.MaxMs),-10}{Ms(h.LastMs),-10}" +
                $"{$"{h.PingsDone}/{target}",-12}{(h.Mac is null ? "-" : ReportCodec.Line(h.Mac)),-18}{DiscoveryModes.Display(h.Discovery),-14}{os}");
            if (!string.IsNullOrEmpty(h.Note))
                sb.AppendLine($"  {Loc.DeviceNote}: {ReportCodec.Block(h.Note).Replace("\n", "\n    ")}");
        }

        if (report.Hosts.Any(h => h.Ports.Count > 0))
        {
            sb.AppendLine();
            bool allStates = m.ListsAllStates;
            sb.AppendLine($"{(allStates ? Loc.ScanToolPortsLabel : Loc.OpenPortsHeader)}:");
            sb.AppendLine(new string('-', 30));
            foreach (var h in report.Hosts.Where(h => h.IsOnline || h.Status == ReportBuilder.Listed))
            {
                var shown = h.Ports.Where(p => allStates || p.IsOpen || p.State == "open|filtered").ToList();
                // Like nmap, a state that holds more than 25 ports is summarised instead of listed; open ports always stay.
                string? summary = null;
                if (allStates && shown.Where(p => !p.IsOpen).GroupBy(p => p.State).OrderByDescending(g => g.Count()).FirstOrDefault() is { } common
                    && common.Count() > MaxListedPerState)
                {
                    summary = string.Format(Loc.ReportNotShownFmt, common.Count(), common.Key);
                    shown.RemoveAll(p => p.State == common.Key);
                }
                if (shown.Count == 0 && summary is null) continue;
                sb.AppendLine($"{h.Ip}:");
                foreach (var p in shown)
                {
                    var name = ReportCodec.Line(string.IsNullOrEmpty(p.Version) ? p.Service ?? "" : $"{p.Service} {p.Version}");
                    var line = p.IsOpen ? $"  {p.Label,-10}{name}".TrimEnd() : $"  {p.Label,-10}{p.State}".TrimEnd();
                    if (showReason && !string.IsNullOrEmpty(p.Reason)) line += $"  ({p.Reason})";
                    sb.AppendLine(line);
                }
                if (summary is not null) sb.AppendLine("  " + summary);
            }
        }

        var withChecks = report.Hosts.Where(h => h.Checks.Count > 0).ToList();
        if (withChecks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{Loc.ReportChecksHeader}:");
            sb.AppendLine(new string('-', 30));
            foreach (var h in withChecks)
            {
                sb.AppendLine($"{h.Ip}:");
                foreach (var c in h.Checks)
                {
                    sb.AppendLine($"  {ReportCodec.Line(c.Id)} ({(c.Service is null ? "-" : ReportCodec.Line(c.Service))}): {ReportCodec.Line(c.Outcome)}");
                    foreach (var f in c.Findings) sb.AppendLine($"    {ReportCodec.Line(f.Key)}: {ReportCodec.Line(f.Value)}");
                }
            }
        }

        if (report.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{Loc.ReportConflictsHeader}:");
            sb.AppendLine(new string('-', 30));
            foreach (var f in report.Findings)
                sb.AppendLine($"  {ReportCodec.Line(f.Ip)}  {(f.Mac is null ? "-" : ReportCodec.Line(f.Mac))}  {ReportCodec.Line(f.Kind)}  {ReportCodec.Line(f.Detail)}".TrimEnd());
        }
        return sb.ToString();
    }
}
