using System.Globalization;
using System.Text;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Models;

/// <summary>How the main table shows a device's open TCP ports: a short ascending list for the cell,
/// the full list with service names for the tooltip, and a sort key (count first, then lowest port).</summary>
public static class PortsSummary
{
    /// <summary>Ports shown in the cell before the list is cut with an ellipsis.</summary>
    public const int CellMax = 6;
    private const int TipMax = 40;

    private static List<NetService> OpenTcp(IEnumerable<NetService> services) =>
        services.Where(s => s.Protocol == NetProtocol.Tcp)
                .GroupBy(s => s.Port).Select(g => g.First()).OrderBy(s => s.Port).ToList();

    /// <summary>Comma list of open TCP ports ascending, cut after <paramref name="max"/> with an ellipsis; a dash when none is known.</summary>
    public static string Text(IEnumerable<NetService> services, int max = CellMax)
    {
        var tcp = OpenTcp(services);
        if (tcp.Count == 0) return "–";
        var shown = string.Join(", ", tcp.Take(max).Select(s => s.Port.ToString(CultureInfo.InvariantCulture)));
        return tcp.Count > max ? shown + ", …" : shown;
    }

    /// <summary>One "port name" line per open TCP port (capped), or null when there is none to show.</summary>
    public static string? Tip(IEnumerable<NetService> services)
    {
        var tcp = OpenTcp(services);
        if (tcp.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var s in tcp.Take(TipMax))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s.Port.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(s.Name)) sb.Append("  ").Append(s.Name);
        }
        if (tcp.Count > TipMax) sb.Append("\n…");
        return sb.ToString();
    }

    /// <summary>Sorts by number of open TCP ports, then by the lowest port; devices without any sort first ascending.</summary>
    public static long SortKey(IEnumerable<NetService> services)
    {
        var tcp = OpenTcp(services);
        return tcp.Count == 0 ? 0 : tcp.Count * 100_000L + tcp[0].Port;
    }
}
