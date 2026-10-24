using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Export;

public static class TxtExporter
{
    /// <summary>One line per open port ("port/proto  name version"), header line
    /// "ip  hostname" first - the same shape a device row's "copy open ports" has
    /// always used, now shared with the port-check tool so both outputs
    /// read identically.</summary>
    public static string FormatPortsText(string ip, string? hostname, IEnumerable<NetService> services)
    {
        var list = services.OrderBy(s => s.Port).ToList();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrEmpty(hostname) ? ip : $"{ip}  {hostname}");
        foreach (var s in list) sb.AppendLine($"{s.PortLabel,-10}{s.DisplayName}");
        return sb.ToString();
    }

    /// <summary>One selected device's values for <see cref="FormatRowsAsText"/>.</summary>
    public readonly record struct RowFields(string Ip, string Status, string Hostname, string Avg, string Min,
        string Max, string Last, string Loss, string Jitter, string Progress, string Mac, string Vendor,
        string Note = "");

    private static string RowHeader() => string.Join("\t", Loc.IpAddress, Loc.Status, Loc.Hostname, Loc.Avg,
        Loc.Min, Loc.Max, Loc.Last, Loc.Loss, Loc.Jitter, Loc.Progress, Loc.Mac, Loc.Vendor, Loc.DeviceNote);

    private static string RowValues(RowFields r) => string.Join("\t", r.Ip, r.Status, r.Hostname, r.Avg, r.Min,
        r.Max, r.Last, r.Loss, r.Jitter, r.Progress, r.Mac, r.Vendor,
        r.Note.Replace("\r", "").Replace("\n", " / "));

    /// <summary>Tab-separated header + value line matching the device
    /// table's visible columns, minus Group (a color swatch only, no text
    /// form) - pastes as aligned columns in a spreadsheet or plain text
    /// editor.</summary>
    public static string FormatRowAsText(string ip, string status, string hostname, string avg, string min,
        string max, string last, string loss, string jitter, string progress, string mac, string vendor,
        string note = "")
        => FormatRowsAsText(new[] { new RowFields(ip, status, hostname, avg, min, max, last, loss, jitter, progress, mac, vendor, note) });

    /// <summary>Formats selected rows as one header and one tab-separated line per device.</summary>
    public static string FormatRowsAsText(IEnumerable<RowFields> rows)
        => RowHeader() + "\r\n" + string.Join("\r\n", rows.Select(RowValues));

    public static string FormatRowsAsMarkdown(IEnumerable<RowFields> rows)
    {
        static string Cell(string? value) => (value ?? "").Replace("\\", "\\\\")
            .Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        static string Line(IEnumerable<string> values) => "| " + string.Join(" | ", values.Select(Cell)) + " |";
        var headers = RowHeader().Split('\t');
        var lines = new List<string> { Line(headers), Line(headers.Select(_ => "---")) };
        lines.AddRange(rows.Select(r => Line(new[] { r.Ip, r.Status, r.Hostname, r.Avg, r.Min,
            r.Max, r.Last, r.Loss, r.Jitter, r.Progress, r.Mac, r.Vendor, r.Note })));
        return string.Join("\r\n", lines);
    }

    public static string Write(IEnumerable<Device> devices, NetworkInfo info,
                               string outputDir, string timestamp, string gatewaySlug,
                               string? displayTime = null,
                               IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null,
                               ReportExtras? extras = null)
        => ReportFile.WriteToFolder(ReportBuilder.Build(devices, info, timestamp, displayTime, ports, extras),
            ReportFormat.Text, outputDir, timestamp, gatewaySlug);
}
