using System.Globalization;
using System.IO;
using System.Text;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

public static class CsvExporter
{
    private static readonly string[] Columns =
    {
        "ip","status","hostname","vendor","mac","ping_avg_ms","ping_min_ms",
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
        foreach (var d in devices)
        {
            if (!d.IsOnline && !d.FromDb && !d.Seen) continue;
            string Num(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
            var target = d.TargetPings == ScanConfig.InfinitePingCount ? "∞" : d.TargetPings.ToString();
            sb.AppendLine(string.Join(',', new[]
            {
                d.Ip,
                d.IsOnline ? "ONLINE" : "OFFLINE",
                d.Hostname == "Unknown" ? "" : d.Hostname ?? "",
                MacVendorLookup.Instance.Lookup(d.Mac) ?? "",
                d.Mac == "Unknown" ? "" : d.Mac ?? "",
                Num(d.AvgMs), Num(d.MinMs), Num(d.MaxMs), Num(d.LastMs),
                d.CurrentPings.ToString(), target, d.FromDb ? "1" : "0",
            }));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
