using System.Globalization;
using System.Text.RegularExpressions;

namespace IpScanner.Core;

/// <summary>Reads the log file back into entries: the inverse of <see cref="Log.Format"/>. A line that
/// starts with the timestamp, level and area opens an entry; every following line up to the next such
/// line belongs to it as detail.</summary>
public static class LogFileParser
{
    private static readonly Regex Head = new(
        @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (?<l>ERROR|WARN|INFO|DEBUG) (?<a>[A-Za-z]+)(?: (?<m>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The entries of the text, oldest first. Text before the first entry line is ignored.</summary>
    public static IReadOnlyList<LogEntry> Parse(string text)
    {
        var result = new List<LogEntry>();
        if (string.IsNullOrEmpty(text)) return result;

        DateTime time = default;
        LogLevel level = default;
        LogArea area = default;
        string message = "";
        List<string>? detail = null;
        var open = false;

        void Flush()
        {
            if (!open) return;
            var d = detail is null ? null : string.Join('\n', detail).TrimEnd('\n', '\r', ' ');
            result.Add(new LogEntry(time, level, area, message, string.IsNullOrEmpty(d) ? null : d));
            open = false;
            detail = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Head.Match(line);
            if (m.Success
                && Enum.TryParse<LogLevel>(m.Groups["l"].Value, ignoreCase: true, out var l)
                && Enum.TryParse<LogArea>(m.Groups["a"].Value, ignoreCase: false, out var a)
                && DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t))
            {
                Flush();
                time = t; level = l; area = a; message = m.Groups["m"].Value;
                open = true;
            }
            else if (open)
            {
                (detail ??= []).Add(line);
            }
        }
        Flush();
        return result;
    }
}
