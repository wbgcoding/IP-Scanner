using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>NetBIOS hostname lookup via nbtstat -A (works across subnets).</summary>
public static class NetBiosHelper
{
    public static string? ParseName(string nbtstatOutput)
    {
        foreach (var code in new[] { "<20>", "<00>" })
        {
            foreach (var line in nbtstatOutput.Split('\n'))
            {
                var m = Regex.Match(line, @"\s*([^\s<].*?)\s+" + Regex.Escape(code) + @"\s");
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    if (name.Length > 0 && name != "Unknown") return name;
                }
            }
        }
        return null;
    }

    public static (string? name, string? mac) Lookup(string ip)
    {
        try
        {
            var output = ArpHelper.RunCapture("nbtstat", $"-A {ip}", 2500);
            var name = ParseName(output);
            var mac = ArpHelper.ParseMac(output);
            return (name, mac == "00-00-00-00-00-00" ? null : mac);
        }
        catch { return (null, null); }
    }
}
