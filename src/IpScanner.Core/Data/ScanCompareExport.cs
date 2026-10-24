using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Text.Json;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;

namespace IpScanner.Core.Data;

/// <summary>Text, JSON, XML and grepable renderings of a scan comparison, built on the same helpers and
/// format list as the scan report writers; JSON, XML and the grepable lines use fixed English identifiers,
/// the text follows the UI language.</summary>
public static class ScanCompareExport
{
    public static string TypeId(ScanChangeType type) => type switch
    {
        ScanChangeType.New => "new",
        ScanChangeType.Gone => "gone",
        ScanChangeType.IpChanged => "ipChanged",
        ScanChangeType.MacChanged => "macChanged",
        ScanChangeType.PortsOpened => "portsOpened",
        ScanChangeType.PortsClosed => "portsClosed",
        _ => "latencyChanged"
    };

    public static string TypeLabel(ScanChangeType type) => type switch
    {
        ScanChangeType.New => Loc.CompareTypeNew,
        ScanChangeType.Gone => Loc.CompareTypeGone,
        ScanChangeType.IpChanged => Loc.CompareTypeIpChanged,
        ScanChangeType.MacChanged => Loc.CompareTypeMacChanged,
        ScanChangeType.PortsOpened => Loc.CompareTypePortsOpened,
        ScanChangeType.PortsClosed => Loc.CompareTypePortsClosed,
        _ => Loc.CompareTypeLatencyChanged
    };

    public static string Render(ScanCompareResult result, ReportFormat format) => format switch
    {
        ReportFormat.Text => ToText(result),
        ReportFormat.Xml => ToXml(result),
        ReportFormat.Json => ToJson(result),
        _ => ToGrepable(result)
    };

    public static string ToJson(ScanCompareResult result)
    {
        var summary = new Dictionary<string, int>();
        foreach (ScanChangeType t in Enum.GetValues<ScanChangeType>())
            summary[TypeId(t)] = result.Changes.Count(c => c.Type == t);
        var doc = new
        {
            runA = Run(result.A),
            runB = Run(result.B),
            options = new { latencyPercent = result.Options.LatencyPercent, latencyMinMs = result.Options.LatencyMinMs },
            portsCompared = result.PortsCompared,
            summary,
            changes = result.Changes.Select(c => new
            {
                type = TypeId(c.Type), ip = c.Ip, mac = c.Mac, hostname = c.Hostname,
                before = c.Before, after = c.After
            })
        };
        return JsonSerializer.Serialize(doc, ReportCodec.Json);
    }

    /// <summary>Own schema (documented in docs/DESIGN.md): attributes carry the values, a missing value is
    /// an absent attribute.</summary>
    public static string ToXml(ScanCompareResult result)
    {
        return ReportCodec.Xml(w =>
        {
            w.WriteStartElement("scanCompare");
            w.WriteAttributeString("version", "1");
            WriteRun(w, "runA", result.A);
            WriteRun(w, "runB", result.B);
            w.WriteStartElement("options");
            w.WriteAttributeString("latencyPercent", result.Options.LatencyPercent.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("latencyMinMs", result.Options.LatencyMinMs.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteElementString("portsCompared", result.PortsCompared ? "true" : "false");
            w.WriteStartElement("summary");
            foreach (ScanChangeType t in Enum.GetValues<ScanChangeType>())
            {
                w.WriteStartElement("count");
                w.WriteAttributeString("type", TypeId(t));
                w.WriteAttributeString("value", result.Changes.Count(c => c.Type == t).ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("changes");
            foreach (var c in result.Changes)
            {
                w.WriteStartElement("change");
                w.WriteAttributeString("type", TypeId(c.Type));
                w.WriteAttributeString("ip", ReportCodec.XmlSafe(c.Ip));
                if (c.Mac is not null) w.WriteAttributeString("mac", ReportCodec.XmlSafe(c.Mac));
                if (c.Hostname is not null) w.WriteAttributeString("hostname", ReportCodec.XmlSafe(c.Hostname));
                if (c.Before is not null) w.WriteAttributeString("before", ReportCodec.XmlSafe(c.Before));
                if (c.After is not null) w.WriteAttributeString("after", ReportCodec.XmlSafe(c.After));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        });
    }

    /// <summary>One line per change, tab-separated "Key: value" fields after a few "#" header lines, so
    /// grep, findstr or awk can pick a type or a host without parsing.</summary>
    public static string ToGrepable(ScanCompareResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# scanCompare runA=").Append(result.A.Id).Append(' ')
          .Append(result.A.StartedAt.ToString("o", CultureInfo.InvariantCulture)).Append(" runB=").Append(result.B.Id).Append(' ')
          .Append(result.B.StartedAt.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var c in result.Changes)
        {
            sb.Append("Host: ").Append(ReportCodec.Field(c.Ip));
            sb.Append("\tChange: ").Append(TypeId(c.Type));
            sb.Append("\tMac: ").Append(ReportCodec.Field(c.Mac));
            sb.Append("\tName: ").Append(ReportCodec.Field(c.Hostname));
            sb.Append("\tBefore: ").Append(ReportCodec.Field(c.Before));
            sb.Append("\tAfter: ").Append(ReportCodec.Field(c.After));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void WriteRun(XmlWriter w, string name, ArchivedScan s)
    {
        w.WriteStartElement(name);
        w.WriteAttributeString("id", s.Id.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("startedAt", s.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        w.WriteAttributeString("network", ReportCodec.XmlSafe(s.Network));
        w.WriteEndElement();
    }

    public static string ToText(ScanCompareResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Loc.CompareRunA}: {Describe(result.A)}");
        sb.AppendLine($"{Loc.CompareRunB}: {Describe(result.B)}");
        sb.AppendLine(string.Format(CultureInfo.CurrentCulture, Loc.CompareSummaryFmt, result.Changes.Count));
        foreach (var group in result.Changes.GroupBy(c => c.Type))
        {
            sb.AppendLine();
            sb.AppendLine($"{TypeLabel(group.Key)} ({group.Count()})");
            sb.AppendLine(new string('-', 30));
            foreach (var c in group)
            {
                var line = new StringBuilder("  ").Append(c.Ip);
                if (!string.IsNullOrEmpty(c.Hostname)) line.Append("  ").Append(c.Hostname);
                if (c.Before is not null || c.After is not null)
                    line.Append("  ").Append(c.Before ?? "-").Append(" -> ").Append(c.After ?? "-");
                sb.AppendLine(line.ToString());
            }
        }
        return sb.ToString();
    }

    private static object Run(ArchivedScan s) => new
    {
        id = s.Id, startedAt = s.StartedAt.ToString("o", CultureInfo.InvariantCulture), network = s.Network
    };

    private static string Describe(ArchivedScan s) =>
        $"{s.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)} ({s.Network})";
}
