using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>Resolves MAC addresses (SendARP first, ARP-table fallback).</summary>
public static class ArpHelper
{
    private static readonly Regex MacRegex =
        new(@"([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    // One "ip ... mac" row of `arp -a` (Windows prints the MAC with '-').
    private static readonly Regex ArpRow = new(
        @"(\d{1,3}(?:\.\d{1,3}){3})\s+(([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2})", RegexOptions.Compiled);

    private const int ArpCacheMs = 3000;
    private static readonly object CacheLock = new();
    private static Dictionary<string, string>? _table;
    private static long _tableTick;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    public static string? ParseMac(string arpOutput)
    {
        var m = MacRegex.Match(arpOutput);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    /// <summary>Parse a full `arp -a` dump into an IP -> MAC map across all interfaces.</summary>
    public static Dictionary<string, string> ParseTable(string arpOutput)
    {
        var map = new Dictionary<string, string>();
        foreach (Match m in ArpRow.Matches(arpOutput))
            map[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();
        return map;
    }

    public static string? Resolve(string ip)
    {
        // SendARP forces L2 resolution for same-subnet hosts (fails for routed ones).
        var viaApi = SendArp(ip);
        if (viaApi is not null) return viaApi;
        // Fallback: the whole system ARP table — also lists L2-reachable hosts in
        // other local ranges (multihomed / flat networks) that the per-IP query
        // misses. Truly routed hosts aren't here; their MAC lives on the router,
        // and only NetBIOS can still reach it.
        // ponytail: per-router MAC would need SNMP to the gateway — out of scope.
        return ArpTable().GetValueOrDefault(ip);
    }

    /// <summary>Cached snapshot of the system ARP table (one `arp -a` per few seconds).</summary>
    private static Dictionary<string, string> ArpTable()
    {
        var cached = _table;
        if (cached is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return cached;
        lock (CacheLock)
        {
            if (_table is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return _table;
            try { _table = ParseTable(RunCapture("arp", "-a", 3000)); }
            catch { _table ??= new(); }
            _tableTick = Environment.TickCount64;
            return _table;
        }
    }

    private static string? SendArp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        try
        {
            uint dest = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
            var mac = new byte[6];
            uint len = 6;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
            if (mac.All(b => b == 0)) return null;
            return string.Join("-", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }

    internal static string RunCapture(string exe, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        p.Start();
        // Read async so a hung child can't block this thread forever; enforce the timeout.
        var readTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        return readTask.Wait(500) ? readTask.Result : "";
    }
}
