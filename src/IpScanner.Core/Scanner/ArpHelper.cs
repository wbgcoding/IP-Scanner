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

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(byte[]? table, ref int size, bool order);

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
        // Fallback: the whole system ARP table - also lists L2-reachable hosts in
        // other local ranges (multihomed / flat networks) that the per-IP query
        // misses. Truly routed hosts aren't here; their MAC lives on the router,
        // and only NetBIOS can still reach it.
        // Resolving that MAC would need SNMP to the gateway - out of scope here.
        return ArpTable().GetValueOrDefault(ip);
    }

    /// <summary>Async twin of <see cref="Resolve"/>: the blocking SendARP call runs on the pool and the
    /// `arp -a` fallback is awaited, so no thread sits waiting on a child process.</summary>
    public static async Task<string?> ResolveAsync(string ip, CancellationToken ct = default)
    {
        var viaApi = await Task.Run(() => SendArp(ip), ct).ConfigureAwait(false);
        if (viaApi is not null) return viaApi;
        var table = await ArpTableAsync(ct).ConfigureAwait(false);
        return table.GetValueOrDefault(ip);
    }

    private static readonly SemaphoreSlim TableGate = new(1, 1);

    private static async Task<Dictionary<string, string>> ArpTableAsync(CancellationToken ct)
    {
        var cached = _table;
        if (cached is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return cached;
        await TableGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_table is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return _table;
            try { _table = IpNetTable() ?? ParseTable(await RunCaptureAsync("arp", "-a", 3000, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn(LogArea.Scan, "Reading the ARP table failed", ex); _table ??= new(); }
            _tableTick = Environment.TickCount64;
            return _table;
        }
        finally { TableGate.Release(); }
    }

    /// <summary>Cached snapshot of the system ARP table (one `arp -a` per few seconds).</summary>
    private static Dictionary<string, string> ArpTable()
    {
        var cached = _table;
        if (cached is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return cached;
        lock (CacheLock)
        {
            if (_table is not null && Environment.TickCount64 - _tableTick < ArpCacheMs) return _table;
            // Read the ARP table via the IP Helper API (no process spawn); fall
            // back to `arp -a` only if the call fails.
            try { _table = IpNetTable() ?? ParseTable(RunCapture("arp", "-a", 3000)); }
            catch (Exception ex) { Log.Warn(LogArea.Scan, "Reading the ARP table failed", ex); _table ??= new(); }
            _tableTick = Environment.TickCount64;
            return _table;
        }
    }

    /// <summary>System ARP table (IP -> MAC) via GetIpNetTable; null on failure.</summary>
    private static Dictionary<string, string>? IpNetTable()
    {
        int size = 0;
        GetIpNetTable(null, ref size, false);   // first call reports the needed size
        if (size == 0) return null;
        var buf = new byte[size];
        if (GetIpNetTable(buf, ref size, false) != 0) return null;

        int count = BitConverter.ToInt32(buf, 0);
        const int rowSize = 24;   // MIB_IPNETROW: index+physLen+phys[8]+addr+type
        var map = new Dictionary<string, string>();
        for (int i = 0; i < count; i++)
        {
            int b = 4 + i * rowSize;
            if (b + rowSize > buf.Length) break;
            int physLen = BitConverter.ToInt32(buf, b + 4);
            int type = BitConverter.ToInt32(buf, b + 20);
            if (physLen != 6 || type < 3) continue;   // keep dynamic(3)/static(4) only
            var mac = $"{buf[b + 8]:X2}-{buf[b + 9]:X2}-{buf[b + 10]:X2}-{buf[b + 11]:X2}-{buf[b + 12]:X2}-{buf[b + 13]:X2}";
            uint a = BitConverter.ToUInt32(buf, b + 16);
            map[$"{a & 0xFF}.{(a >> 8) & 0xFF}.{(a >> 16) & 0xFF}.{(a >> 24) & 0xFF}"] = mac;
        }
        return map;
    }

    /// <summary>Live ARP request through the OS, never answered from the cached table.</summary>
    internal static string? SendArpOnly(string ip) => SendArp(ip);

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
        catch (Exception ex) { Log.Debug(LogArea.Scan, "ARP lookup failed: " + ex.GetType().Name); return null; }
    }

    private const int OutputDrainMs = 500;

    /// <summary>Blocking wrapper for the synchronous lookup helpers; the work itself runs on the
    /// pool, so a caller on a context-bound thread cannot deadlock on it.</summary>
    internal static string RunCapture(string exe, string args, int timeoutMs)
        => Task.Run(() => RunCaptureAsync(exe, args, timeoutMs)).GetAwaiter().GetResult();

    /// <summary>Runs a console tool and returns what it printed. The process is killed after
    /// <paramref name="timeoutMs"/>, and reading its output is given at most 500 ms more, so a
    /// hung child cannot block the caller; whatever could not be read yields an empty string.</summary>
    internal static async Task<string> RunCaptureAsync(string exe, string args, int timeoutMs,
        CancellationToken ct = default)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        p.Start();
        var readTask = p.StandardOutput.ReadToEndAsync(ct);
        using (var exitLimit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            exitLimit.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(exitLimit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        using var drainLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainLimit.CancelAfter(OutputDrainMs);
        try { return await readTask.WaitAsync(drainLimit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return ""; }
    }
}
