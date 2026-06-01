using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>Resolves MAC addresses via the system ARP table.</summary>
public static class ArpHelper
{
    private static readonly Regex MacRegex =
        new(@"([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    public static string? ParseMac(string arpOutput)
    {
        var m = MacRegex.Match(arpOutput);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    public static string? Resolve(string ip)
    {
        try
        {
            var output = RunCapture("arp", $"-a {ip}", 2000);
            return ParseMac(output);
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
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return output;
    }
}
