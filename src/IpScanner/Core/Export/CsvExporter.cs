using System.Globalization;
using System.IO;
using System.Text;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

public static class CsvExporter
{
    private static readonly string[] Columns =
    {
        "ip","status","hostname","group","vendor","mac","ping_avg_ms","ping_min_ms",
        "ping_max_ms","last_ping_ms","pings_done","pings_target","from_db"
    };

    public static string Write(IEnumerable<Device> devices, string outputDir,
                               string timestamp, string gatewaySlug)
    {
        Directory.CreateDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.csv");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns));
        foreach (var d in devices.OrderBy(d => Net.Ipv4.SortKey(d.Ip)))
        {
            if (!d.IsOnline && !d.FromDb && !d.Seen) continue;
            string Num(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
            var target = d.TargetPings == ScanConfig.InfinitePingCount ? "∞" : d.TargetPings.ToString();
            sb.AppendLine(string.Join(',', new[]
            {
                d.Ip,
                d.IsOnline ? "ONLINE" : "OFFLINE",
                Esc(d.Hostname == Device.Unknown ? "" : d.Hostname ?? ""),
                ExportGroup(d.GroupId),
                Esc(MacVendorLookup.Instance.Lookup(d.Mac) ?? ""),
                d.Mac == Device.Unknown ? "" : d.Mac ?? "",
                Num(d.AvgMs), Num(d.MinMs), Num(d.MaxMs), Num(d.LastMs),
                d.CurrentPings.ToString(), target, d.FromDb ? "1" : "0",
            }));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    /// <summary>Group numbering in exports starts at 0 = ungrouped, 1 = gateway, ...</summary>
    internal static string ExportGroup(int groupId) => groupId <= 1 ? "0" : (groupId - 1).ToString();

    /// <summary>RFC 4180: quote fields containing delimiter, quote or newline.</summary>
    internal static string Esc(string field) =>
        field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"") + "\"";
}
