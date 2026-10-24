using System.Globalization;
using System.IO;
using System.Text;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Export;

public static class CsvExporter
{
    private static readonly string[] Columns =
    {
        "ip","status","hostname","group","vendor","mac","ping_avg_ms","ping_min_ms",
        "ping_max_ms","last_ping_ms","pings_done","pings_target","from_db","open_ports","discovery_method","os_estimate","note"
    };

    public static string Write(IEnumerable<Device> devices, string outputDir,
                               string timestamp, string gatewaySlug,
                               IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null)
    {
        outputDir = IpScanner.Core.Models.ScanConfig.ResolvePath(outputDir);
        // Restrict the reports folder to this Windows account before the
        // first file lands in it, same as the config/database folders.
        var ownedDir = IpScanner.Core.Data.FileProtection.PrepareDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.csv");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns));
        foreach (var d in devices.OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer))
        {
            if (!d.IsOnline && !d.FromDb && !d.Seen) continue;
            string Num(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
            var target = d.TargetPings == ScanConfig.InfinitePingCount ? "∞" : d.TargetPings.ToString();
            // "80/TCP HTTP; 443/TCP HTTPS" - "; " never collides with the
            // CSV delimiter, so the whole field stays a single unquoted-safe cell.
            var openPorts = ports is not null && ports.TryGetValue(d.Ip, out var found) && found.Count > 0
                ? string.Join("; ", found.OrderBy(s => s.Port).Select(s => $"{s.PortLabel} {s.DisplayName}"))
                : "";
            sb.AppendLine(string.Join(',', new[]
            {
                Esc(d.Ip),
                d.IsListed ? "LISTED" : d.IsOnline ? "ONLINE" : "OFFLINE",
                Esc(DefuseFormula(d.Hostname == Device.Unknown ? "" : d.Hostname ?? "")),
                ExportGroup(d.GroupId),
                Esc(MacVendorLookup.Instance.Lookup(d.Mac) ?? ""),
                Esc(d.Mac == Device.Unknown ? "" : d.Mac ?? ""),
                Num(d.AvgMs), Num(d.MinMs), Num(d.MaxMs), Num(d.LastMs),
                d.CurrentPings.ToString(), target, d.FromDb ? "1" : "0",
                Esc(DefuseFormula(openPorts)),
                Esc(d.DiscoveryMethod ?? ""),
                Esc(Scanner.Os.OsDisplay.ForExport(d, ports)),
                Esc(DefuseFormula(d.Note ?? "")),
            }));
        }
        IpScanner.Core.Data.FileProtection.WriteReport(path, sb.ToString(), Encoding.UTF8, ownedDir);
        return path;
    }

    /// <summary>Group numbering in exports starts at 0 = ungrouped, 1 = gateway, ...</summary>
    internal static string ExportGroup(int groupId) => groupId <= 1 ? "0" : (groupId - 1).ToString();

    /// <summary>RFC 4180: quote fields containing delimiter, quote or newline.</summary>
    internal static string Esc(string field) =>
        field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"") + "\"";

    /// <summary>Defuses CSV formula injection (OWASP): a leading =, +, -, @, tab or CR
    /// makes Excel/Sheets execute the cell as a formula when the file is opened.
    /// Hostnames come from other devices on the network (DNS/mDNS/NetBIOS) and are
    /// not trusted input, so this runs before RFC 4180 escaping.</summary>
    internal static string DefuseFormula(string field) =>
        field.Length > 0 && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + field
            : field;
}
