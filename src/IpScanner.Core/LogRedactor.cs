using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IpScanner.Core;

/// <summary>Strips personal details from text before it goes into the error log: the user profile
/// path (long and 8.3 short form), the Windows user name, the machine name, IPv4 addresses (only the
/// last octet survives, loopback is left alone), IPv6 addresses, MAC addresses (last byte only) and
/// device host names: a name under a home or office suffix (nas.fritz.box, printer.lan) keeps only
/// its suffix, and the "(host:port)" a failed connection reports loses the host.</summary>
public static class LogRedactor
{
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d])", RegexOptions.Compiled);
    private static readonly Regex Mac = new(@"(?<![0-9A-Fa-f:\-])(?:[0-9A-Fa-f]{2}[:\-]){5}([0-9A-Fa-f]{2})(?![0-9A-Fa-f:\-])", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(
        @"(?<![\w:.])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{0,4}::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{0,4})(?![\w:])",
        RegexOptions.Compiled);

    // Only suffixes that name a private network: a dotted type name or file name (System.Net.Sockets, IP-Scanner.exe)
    // never ends in one, and public hosts the app talks to (github.com) are not device names.
    private static readonly Regex PrivateHost = new(
        @"(?<![\w.\-])(?:[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?\.)+(?<tld>home\.arpa|localdomain|local|box|lan|home|internal|intranet|corp|private)(?![\w\-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "(nas-home:443)": the target a failed connect reports. A bare host name, not a number or a dotted address.
    private static readonly Regex HostPort = new(@"\((?<host>[A-Za-z][A-Za-z0-9\-]*(?:\.[A-Za-z0-9\-]+)*):(?<port>\d{1,5})\)", RegexOptions.Compiled);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    private static readonly Lazy<string?> ShortProfile = new(() =>
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return null;
            var sb = new StringBuilder(520);
            var len = GetShortPathName(profile, sb, (uint)sb.Capacity);
            return len > 0 && len < sb.Capacity ? sb.ToString() : null;
        }
        catch { return null; }
    });

    public static string Redact(string text) => Redact(text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName,
        ShortProfile.Value, Environment.MachineName);

    internal static string Redact(string text, string? profilePath, string? userName,
        string? shortProfilePath = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        foreach (var path in new[] { profilePath, shortProfilePath })
            if (!string.IsNullOrWhiteSpace(path))
                result = result.Replace(path.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userName))
            result = ReplaceWord(result, userName, "<user>");
        if (machineName is { Length: >= 3 } && !string.Equals(machineName, userName, StringComparison.OrdinalIgnoreCase))
            result = ReplaceWord(result, machineName, "<host>");
        result = PrivateHost.Replace(result, m => "<host>." + m.Groups["tld"].Value);
        result = HostPort.Replace(result, m => "(<host>:" + m.Groups["port"].Value + ")");
        result = Mac.Replace(result, m => "xx:xx:xx:xx:xx:" + m.Groups[1].Value);
        result = Ipv6.Replace(result, m => m.Value is "::" or "::1" ? m.Value : "<ipv6>");
        return Ipv4.Replace(result, m =>
        {
            for (int i = 1; i <= 4; i++)
                if (!int.TryParse(m.Groups[i].Value, out var octet) || octet > 255) return m.Value;   // not an address (version number)
            return m.Groups[1].Value == "127" ? m.Value : "x.x.x." + m.Groups[4].Value;
        });
    }

    private static string ReplaceWord(string text, string word, string replacement)
        => Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_])",
            replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
