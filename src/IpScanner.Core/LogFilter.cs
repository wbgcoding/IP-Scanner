using IpScanner.Core.Localization;

namespace IpScanner.Core;

/// <summary>The viewer's selection logic, kept apart from the page so it can be tested without a window:
/// readable names for areas and levels, and the area / level / free-text filter over the buffer.</summary>
public static class LogFilter
{
    public static string AreaName(LogArea area) => area switch
    {
        LogArea.App => Loc.LogAreaApp,
        LogArea.Scan => Loc.NavScan,
        LogArea.Ports => Loc.LogAreaPorts,
        LogArea.Dns => Loc.Dns,
        LogArea.Db => Loc.TabDatabase,
        LogArea.Config => Loc.Settings,
        LogArea.Update => Loc.LogAreaUpdate,
        LogArea.Ui => Loc.LogAreaUi,
        LogArea.Tools => Loc.Tools,
        LogArea.Cli => Loc.LogAreaCli,
        _ => area.ToString(),
    };

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Error => Loc.LogLevelError,
        LogLevel.Warn => Loc.LogLevelWarn,
        LogLevel.Info => Loc.LogLevelInfo,
        LogLevel.Debug => Loc.LogLevelDebug,
        _ => level.ToString(),
    };

    /// <summary>The entries that pass the filter, newest first. A null area or level means all; the
    /// search text must appear (case-insensitive) in the message, the exception text, the readable
    /// title, or the area and level names.</summary>
    public static IReadOnlyList<LogEntry> Apply(IReadOnlyList<LogEntry> all, LogArea? area, LogLevel? level, string? search)
    {
        var text = search?.Trim();
        var result = new List<LogEntry>();
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var e = all[i];
            if (area is { } a && e.Area != a) continue;
            if (level is { } l && e.Level != l) continue;
            if (!string.IsNullOrEmpty(text) && !Matches(e, text)) continue;
            result.Add(e);
        }
        return result;
    }

    private static bool Matches(LogEntry e, string text)
    {
        const StringComparison cmp = StringComparison.CurrentCultureIgnoreCase;
        return e.Message.Contains(text, cmp)
            || (e.Detail?.Contains(text, cmp) ?? false)
            || (LogTitles.For(e)?.Contains(text, cmp) ?? false)
            || AreaName(e.Area).Contains(text, cmp)
            || LevelName(e.Level).Contains(text, cmp);
    }
}
