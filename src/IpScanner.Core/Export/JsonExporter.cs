using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Export;

public static class JsonExporter
{
    public static string Write(IEnumerable<Device> devices, NetworkInfo info, string outputDir, string timestamp,
        string gatewaySlug, IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null, ReportExtras? extras = null)
        => ReportFile.WriteToFolder(ReportBuilder.Build(devices, info, timestamp, null, ports, extras), ReportFormat.Json,
            outputDir, timestamp, gatewaySlug);
}
