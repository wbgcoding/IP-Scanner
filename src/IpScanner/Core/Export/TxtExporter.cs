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
                               string outputDir, string timestamp, string gatewaySlug)
    {
        Directory.CreateDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.txt");

        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine("NETWORK SCAN REPORT");
        sb.AppendLine($"Timestamp: {timestamp}");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();
        sb.AppendLine("Network Information:");
        sb.AppendLine(new string('-', 30));
        sb.AppendLine($"Interface:   {info.Interface}");
        sb.AppendLine($"IP Address:  {info.Ip ?? "Unknown"}");
        if (info.Gateway is not null) sb.AppendLine($"Gateway:     {info.Gateway}");
        if (info.SubnetMask is not null) sb.AppendLine($"Subnet Mask: {info.SubnetMask}");
        sb.AppendLine($"DNS Servers: {(info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "Unknown")}");
        sb.AppendLine();

        sb.AppendLine($"{"IP",-16}{"Status",-9}{"Hostname",-24}{"Avg",-10}{"MAC",-18}");
        sb.AppendLine(new string('-', 80));
        foreach (var d in devices.Where(d => d.IsOnline || d.FromDb || d.Seen))
        {
            string avg = d.AvgMs is null ? "-" : d.AvgMs.Value.ToString("F2", CultureInfo.InvariantCulture) + "ms";
            sb.AppendLine(
                $"{d.Ip,-16}{(d.IsOnline ? "ONLINE" : "OFFLINE"),-9}" +
                $"{(d.Hostname ?? "-"),-24}{avg,-10}" +
                $"{d.Mac ?? "-",-18}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
