using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

public static class TxtExporter
{
    public static string Write(IEnumerable<Device> devices, NetworkInfo info,
                               string outputDir, string timestamp, string gatewaySlug,
                               string? displayTime = null)
    {
        Directory.CreateDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.txt");

        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine("NETWORK SCAN REPORT");
        sb.AppendLine($"Timestamp: {displayTime ?? timestamp}");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();
        sb.AppendLine("Network Information:");
        sb.AppendLine(new string('-', 30));
        sb.AppendLine($"Interface:   {(info.Interface.Length > 0 ? info.Interface : "Unknown")}");
        sb.AppendLine($"IP Address:  {info.Ip ?? "Unknown"}");
        if (info.Gateway is not null) sb.AppendLine($"Gateway:     {info.Gateway}");
        if (info.SubnetMask is not null) sb.AppendLine($"Subnet Mask: {info.SubnetMask}");
        sb.AppendLine($"DNS Servers: {(info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "Unknown")}");
        sb.AppendLine();

        sb.AppendLine($"{"IP",-16}{"Status",-9}{"Hostname",-24}{"Group",-7}{"Avg",-10}{"Min",-10}{"Max",-10}{"Last",-10}{"Pings",-12}{"MAC",-18}");
        sb.AppendLine(new string('-', 126));
        string Ms(double? v) => v is null ? "-" : v.Value.ToString("F2", CultureInfo.InvariantCulture) + "ms";
        foreach (var d in devices.Where(d => d.IsOnline || d.FromDb || d.Seen)
                                 .OrderBy(d => Net.Ipv4.SortKey(d.Ip)))
        {
            var target = d.TargetPings == ScanConfig.InfinitePingCount ? "∞" : d.TargetPings.ToString();
            sb.AppendLine(
                $"{d.Ip,-16}{(d.IsOnline ? "ONLINE" : "OFFLINE"),-9}" +
                $"{(d.Hostname ?? "-"),-24}{CsvExporter.ExportGroup(d.GroupId),-7}" +
                $"{Ms(d.AvgMs),-10}{Ms(d.MinMs),-10}{Ms(d.MaxMs),-10}{Ms(d.LastMs),-10}" +
                $"{$"{d.CurrentPings}/{target}",-12}{d.Mac ?? "-",-18}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
