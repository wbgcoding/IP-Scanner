using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Export;

/// <summary>A standalone HTML report, styled in the app's own (dark,
/// Catppuccin Mocha) color scheme - independent of the live UI theme, since this
/// runs off the UI thread and the file is meant to look the same every time.</summary>
public static class HtmlExporter
{
    private const string Bg = "#1E1E2E", Surface = "#313244", Text = "#CDD6F4",
        Subtext = "#A6ADC8", Blue = "#89B4FA", Green = "#A6E3A1", Red = "#F38BA8";

    public static string Write(IEnumerable<Device> devices, NetworkInfo info,
                               string outputDir, string timestamp, string gatewaySlug,
                               string? displayTime = null,
                               IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null)
    {
        outputDir = IpScanner.Core.Models.ScanConfig.ResolvePath(outputDir);
        // Restrict the reports folder to this Windows account before the
        // first file lands in it, same as the config/database folders.
        var ownedDir = IpScanner.Core.Data.FileProtection.PrepareDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.html");
        var unknown = Loc.ReportUnknown;
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Ms(double? v) => v is null ? "-" : v.Value.ToString("F2", CultureInfo.InvariantCulture) + "ms";

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{E(Loc.ReportTitle)}</title><style>");
        sb.AppendLine($"body{{background:{Bg};color:{Text};font:14px/1.5 Segoe UI,Arial,sans-serif;margin:24px}}");
        sb.AppendLine($"h1{{color:{Blue};font-size:20px}} h2{{color:{Blue};font-size:15px;margin-top:28px}}");
        sb.AppendLine($"table{{border-collapse:collapse;width:100%}} th,td{{padding:5px 10px;text-align:left;border-bottom:1px solid {Surface}}}");
        sb.AppendLine($"th{{color:{Subtext};font-weight:600}} .on{{color:{Green}}} .off{{color:{Red}}} .note{{white-space:pre-wrap}}");
        sb.AppendLine($".meta{{color:{Subtext};font-size:12px}}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>{E(Loc.ReportTitle)}</h1>");
        sb.AppendLine($"<div class=\"meta\">{E(Loc.ReportTimestamp)}: {E(displayTime ?? timestamp)}</div>");

        sb.AppendLine($"<h2>{E(Loc.ReportNetworkInfo)}</h2><table>");
        sb.AppendLine($"<tr><th>{E(Loc.Interface)}</th><td>{E(info.Interface.Length > 0 ? info.Interface : unknown)}</td></tr>");
        sb.AppendLine($"<tr><th>{E(Loc.IpAddress)}</th><td>{E(info.Ip ?? unknown)}</td></tr>");
        if (info.Gateway is not null) sb.AppendLine($"<tr><th>{E(Loc.Gateway)}</th><td>{E(info.Gateway)}</td></tr>");
        if (info.SubnetMask is not null) sb.AppendLine($"<tr><th>{E(Loc.Mask)}</th><td>{E(info.SubnetMask)}</td></tr>");
        sb.AppendLine($"<tr><th>{E(Loc.Dns)}</th><td>{E(info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : unknown)}</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine($"<h2>{E(Loc.ReportTitle)}</h2><table><tr>" +
            $"<th>{E(Loc.IpAddress)}</th><th>{E(Loc.Status)}</th><th>{E(Loc.Hostname)}</th>" +
            $"<th>{E(Loc.Avg)}</th><th>{E(Loc.Min)}</th><th>{E(Loc.Max)}</th><th>{E(Loc.Last)}</th>" +
            $"<th>{E(Loc.Mac)}</th><th>{E(Loc.FoundBy)}</th><th>{E(Loc.OsColumnHeader)}</th><th>{E(Loc.DeviceNote)}</th></tr>");
        var online = devices.Where(d => d.IsOnline || d.FromDb || d.Seen)
                             .OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer).ToList();
        foreach (var d in online)
        {
            var host = d.Hostname is null or Device.Unknown ? "-" : d.Hostname;
            var mac = d.Mac is null or Device.Unknown ? "-" : d.Mac;
            var statusClass = d.IsOnline ? "on" : "off";
            var statusText = d.IsListed ? Loc.StatusListed : d.IsOnline ? Loc.StatusOnline : Loc.StatusOffline;
            sb.AppendLine($"<tr><td>{E(d.Ip)}</td><td class=\"{statusClass}\">{E(statusText)}</td>" +
                $"<td>{E(host)}</td><td>{E(Ms(d.AvgMs))}</td><td>{E(Ms(d.MinMs))}</td>" +
                $"<td>{E(Ms(d.MaxMs))}</td><td>{E(d.LastFailed ? "-" : Ms(d.LastMs))}</td><td>{E(mac)}</td>" +
                $"<td>{E(IpScanner.Core.Scanner.Discovery.DiscoveryModes.Display(d.DiscoveryMethod))}</td><td>{E(IpScanner.Core.Scanner.Os.OsDisplay.ForExport(d, ports))}</td><td class=\"note\">{E(d.Note)}</td></tr>");
        }
        sb.AppendLine("</table>");

        if (ports is { Count: > 0 })
        {
            sb.AppendLine($"<h2>{E(Loc.OpenPortsHeader)}</h2>");
            foreach (var d in online.Where(d => d.IsOnline))
            {
                if (!ports.TryGetValue(d.Ip, out var found) || found.Count == 0) continue;
                sb.AppendLine($"<h3 style=\"font-size:13px;color:{Text}\">{E(d.Ip)}</h3><table>");
                foreach (var s in found.OrderBy(s => s.Port))
                    sb.AppendLine($"<tr><td>{E(s.PortLabel)}</td><td>{E(s.DisplayName)}</td></tr>");
                sb.AppendLine("</table>");
            }
        }

        sb.AppendLine("</body></html>");
        IpScanner.Core.Data.FileProtection.WriteReport(path, sb.ToString(), Encoding.UTF8, ownedDir);
        return path;
    }
}
