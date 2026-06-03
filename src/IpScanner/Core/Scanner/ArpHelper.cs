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

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    public static string? ParseMac(string arpOutput)
    {
        var m = MacRegex.Match(arpOutput);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    public static string? Resolve(string ip)
    {
        // SendARP forces L2 resolution immediately for same-subnet hosts.
        var viaApi = SendArp(ip);
        if (viaApi is not null) return viaApi;
        // Fallback: read the ARP table (routed hosts / cached entries).
        try { return ParseMac(RunCapture("arp", $"-a {ip}", 2000)); }
        catch { return null; }
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
