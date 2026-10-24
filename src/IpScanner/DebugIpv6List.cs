#if DEBUG
using System.IO;
using System.Linq;
using System.Text;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Ipv6;

namespace IpScanner;

/// <summary>Debug-only live check: <c>IP-Scanner.exe --ipv6-list [file]</c> prints the own IPv6 addresses, the
/// neighbour cache and what link discovery finds, into the file (default ipv6-list.txt next to the exe).</summary>
internal static class DebugIpv6List
{
    public static int Run(string[] args)
    {
        var path = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "ipv6-list.txt");
        var sb = new StringBuilder();
        var own = NetworkDetector.ListOwnIpv6();
        sb.AppendLine("own addresses: " + own.Count);
        foreach (var o in own) sb.AppendLine($"  {o.Address.ToScopedString()}/{o.PrefixLength} if={o.InterfaceIndex} {o.Interface}");

        var table = new WindowsNeighborTable();
        var before = table.Read();
        sb.AppendLine("neighbour cache before: " + before.Count);
        foreach (var n in before) sb.AppendLine($"  {n.Address.ToScopedString()} mac={n.Mac} if={n.InterfaceIndex} {n.State}{(n.IsRouter ? " router" : "")}");

        var disc = new Ipv6Discovery(table, new SystemIpv6Net(), nameOf: ip => MdnsHelper.Resolve(ip, 800));
        var hosts = disc.DiscoverLinkAsync(own).GetAwaiter().GetResult();
        sb.AppendLine("discovered: " + hosts.Count);
        foreach (var h in hosts)
            sb.AppendLine($"  {h.Address.ToScopedString()} mac={h.Mac} if={h.InterfaceIndex} {h.Method}{(h.IsRouter ? " router" : "")}"
                + (h.RttMs is { } r ? $" rtt={r:0.0}ms" : "") + (h.Name is null ? "" : " name=" + h.Name));
        var net = new SystemIpv6Net();
        foreach (var h in hosts.Take(3))
        {
            var rep = net.EchoAsync(h.Address, 1500, default).GetAwaiter().GetResult();
            sb.AppendLine($"unicast echo {h.Address.ToScopedString()}: {rep.Count} reply" + (rep.Count > 0 ? $" rtt={rep[0].RttMs:0.0}ms" : ""));
        }
        var all = net.EchoAsync(Ipv6Address.AllNodes.WithZone(own.FirstOrDefault()?.InterfaceIndex.ToString() ?? "0"), 1500, default).GetAwaiter().GetResult();
        sb.AppendLine("multicast echo replies: " + all.Count);
        File.WriteAllText(path, sb.ToString());
        Console.Out.Write(sb.ToString());
        return 0;
    }
}
#endif
