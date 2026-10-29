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
            else if (!char.IsControl(c) && !IsBidiControl(c)) clean.Append(c);
        var result = clean.ToString().Trim();
        return result.Length > maxLen ? result[..maxLen] : result;
    }

    /// <summary>Direction marks and overrides (U+200E/F, U+202A-E, U+2066-9) that reorder the text around
    /// them: a device name must not be able to flip how the words next to it read.</summary>
    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
}
