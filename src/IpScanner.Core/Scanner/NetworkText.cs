using System.Text;

namespace IpScanner.Core.Scanner;

/// <summary>Hostnames and banner/version text come straight from the
/// remote device's own advertisement (DNS/mDNS/NetBIOS reply, HTTP/telnet/SSH
/// banner) - a hostile device can put anything in them, including control
/// characters that would corrupt a CSV/TXT report or a database row. Strip
/// those and cap the length before the text goes anywhere near storage.</summary>
internal static class NetworkText
{
    public static string Sanitize(string s, int maxLen = 60, bool allowLineBreaks = false)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var c in s)
            if (allowLineBreaks && c == '\n') clean.Append(c);
            else if (!char.IsControl(c)) clean.Append(c);
        var result = clean.ToString().Trim();
        return result.Length > maxLen ? result[..maxLen] : result;
    }
}
