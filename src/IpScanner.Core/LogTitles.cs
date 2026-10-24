using System.Text.RegularExpressions;
using IpScanner.Core.Localization;

namespace IpScanner.Core;

/// <summary>Maps the raw text of a log entry (message plus exception) to a short readable title, so the
/// viewer can show "Database is locked by another process" instead of a provider error code. The
/// first matching rule wins; no match means the viewer shows the entry's own message.</summary>
public static class LogTitles
{
    private sealed record Rule(Regex Pattern, Func<string> Title);

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Order matters: the specific database codes come before the generic file and access rules.
    private static readonly Rule[] Rules =
    [
        new(new Regex(@"database (table )?is locked|SQLite Error (5|6)\b|SQLITE_(BUSY|LOCKED)", Opts), () => Loc.LogTitleDbLocked),
        new(new Regex(@"readonly database|attempt to write a readonly|SQLite Error (8|23)\b|SQLITE_READONLY", Opts), () => Loc.LogTitleDbReadOnly),
        new(new Regex(@"disk I/O error|SQLite Error 10\b|SQLITE_IOERR", Opts), () => Loc.LogTitleDiskIo),
        new(new Regex(@"not enough space on the disk|disk is full|database or disk is full|SQLite Error 13\b|ERROR_DISK_FULL", Opts), () => Loc.LogTitleDiskFull),
        new(new Regex(@"CLIPBRD_E_CANT_OPEN|0x800401D0|clipboard", Opts), () => Loc.LogTitleClipboardBusy),
        new(new Regex(@"being used by another process|sharing violation", Opts), () => Loc.LogTitleFileInUse),
        new(new Regex(@"No route to host|HostUnreachable|NetworkUnreachable|\b10065\b|\b10051\b", Opts), () => Loc.LogTitleNoRoute),
        new(new Regex(@"No such host is known|HostNotFound|TryAgain|\b1100[14]\b|name or service not known", Opts), () => Loc.LogTitleNameNotFound),
        new(new Regex(@"actively refused|ConnectionRefused|\b10061\b", Opts), () => Loc.LogTitleRefused),
        new(new Regex(@"timed out|TimedOut|TimeoutException|\b10060\b", Opts), () => Loc.LogTitleTimedOut),
        new(new Regex(@"Access (to the path )?'?.*is denied|UnauthorizedAccess|AccessDenied|\b10013\b|administrator", Opts), () => Loc.LogTitleAccessDenied),
        // Named steps come last: a recognised cause above is the better title, the step is the fallback.
        new(new Regex(@"^Port collection failed", Opts), () => Loc.LogTitlePortCollection),
        new(new Regex(@"^Update signature check failed", Opts), () => Loc.LogTitleUpdateSignature),
        new(new Regex(@"^Startup scan failed", Opts), () => Loc.LogTitleStartupScan),
    ];

    /// <summary>The readable title for the given raw text, or null when no rule matches.</summary>
    public static string? For(string? message, string? detail)
    {
        var text = string.IsNullOrEmpty(detail) ? message : message + "\n" + detail;
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var rule in Rules)
            if (rule.Pattern.IsMatch(text)) return rule.Title();
        return null;
    }

    public static string? For(LogEntry entry) => For(entry.Message, entry.Detail);
}
