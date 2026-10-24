using System.IO;
using System.Text;
using IpScanner.Core.Data;

namespace IpScanner.Core;

public enum LogLevel { Error, Warn, Info, Debug }

public enum LogArea { App, Scan, Ports, Dns, Db, Config, Update, Ui, Tools, Cli }

/// <summary>One log row as the viewer shows it. Message and Detail are already redacted.</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, LogArea Area, string Message, string? Detail);

/// <summary>The one log sink: a thread-safe rotating file next to the exe plus an in-memory ring
/// buffer of the latest entries for the viewer. Error and Warn are always kept; Info and Debug
/// (verbose) only while the setting is on. Text is redacted before it is stored anywhere, so
/// neither the file nor the viewer ever holds a user name, profile path or full device address.</summary>
public static class Log
{
    public const int BufferCapacity = 2000;
    public const long DefaultMaxFileBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly LogEntry?[] Ring = new LogEntry?[BufferCapacity];
    private static int _next;
    private static int _count;
    private static bool _aclChecked;

    /// <summary>Where the rotating file lives; next to the exe, never the process's current directory.</summary>
    internal static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "error.log");
    internal static long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>Asked on every call whether verbose levels are wanted; wired to the setting by the window.</summary>
    public static Func<bool> VerboseProvider { get; set; } = () => false;

    /// <summary>Raised after an entry was stored (any thread).</summary>
    public static event Action<LogEntry>? Added;

    public static bool IsVerbose
    {
        get { try { return VerboseProvider(); } catch { return false; } }
    }

    public static void Error(LogArea area, string message, Exception? ex = null) => Write(LogLevel.Error, area, message, ex);
    public static void Warn(LogArea area, string message, Exception? ex = null) => Write(LogLevel.Warn, area, message, ex);
    public static void Info(LogArea area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Debug(LogArea area, string message) => Write(LogLevel.Debug, area, message, null);

    internal static void Write(LogLevel level, LogArea area, string message, Exception? ex)
    {
        try
        {
            if (level >= LogLevel.Info && !IsVerbose) return;
            // The file holds one headline per entry and everything below it is detail, so a multi-line message
            // (a crash handler logging Exception.ToString()) is stored as headline + detail up front; the
            // buffered entry then equals what LogFileParser reads back and the history merge cannot double it.
            var text = LogRedactor.Redact(message ?? "");
            string? detail = ex is null ? null : LogRedactor.Redact(ex.ToString());
            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0)
            {
                var rest = text[(firstBreak + 1)..].TrimEnd('\r', '\n', ' ');
                text = text[..firstBreak].TrimEnd('\r');
                if (rest.Length > 0) detail = detail is null ? rest : rest + "\n" + detail;
            }
            var entry = new LogEntry(DateTime.Now, level, area, text, detail);
            lock (Gate)
            {
                Ring[_next] = entry;
                _next = (_next + 1) % BufferCapacity;
                if (_count < BufferCapacity) _count++;
                AppendToFile(entry);
            }
            Added?.Invoke(entry);
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>The buffered entries, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            var list = new List<LogEntry>(_count);
            var start = _count < BufferCapacity ? 0 : _next;
            for (int i = 0; i < _count; i++) list.Add(Ring[(start + i) % BufferCapacity]!);
            return list;
        }
    }

    /// <summary>Merges the entries of the log file (and its rotated older generation) into the buffer, so the
    /// viewer shows what earlier sessions wrote and not only what happened since this start. Entries the buffer
    /// already holds are not added twice; the call can be repeated. Returns how many entries were added.</summary>
    public static int LoadHistory()
    {
        List<LogEntry> fromFile;
        try
        {
            fromFile = [];
            foreach (var path in new[] { FilePath + ".1", FilePath })
                if (File.Exists(path))
                    fromFile.AddRange(LogFileParser.Parse(ReadSharedText(path)));
        }
        catch { return 0; }

        lock (Gate)
        {
            var current = new List<LogEntry>(_count);
            var start = _count < BufferCapacity ? 0 : _next;
            for (int i = 0; i < _count; i++) current.Add(Ring[(start + i) % BufferCapacity]!);

            // A file entry only keeps second precision, so it is matched against the buffer by that key; one
            // buffered entry cancels exactly one file entry (two identical lines in a second stay two).
            var held = new Dictionary<(DateTime, LogLevel, LogArea, string), int>();
            foreach (var e in current)
            {
                var key = HistoryKey(e);
                held[key] = held.GetValueOrDefault(key) + 1;
            }
            var added = new List<LogEntry>();
            foreach (var e in fromFile)
            {
                var key = HistoryKey(e);
                if (held.TryGetValue(key, out var n) && n > 0) held[key] = n - 1;
                else added.Add(e);
            }
            if (added.Count == 0) return 0;

            var merged = added.Concat(current).OrderBy(e => e.Time).ToList();
            if (merged.Count > BufferCapacity) merged.RemoveRange(0, merged.Count - BufferCapacity);
            Array.Clear(Ring);
            for (int i = 0; i < merged.Count; i++) Ring[i] = merged[i];
            _count = merged.Count;
            _next = merged.Count % BufferCapacity;
            return added.Count;
        }
    }

    private static (DateTime, LogLevel, LogArea, string) HistoryKey(LogEntry e)
        => (new DateTime(e.Time.Ticks - e.Time.Ticks % TimeSpan.TicksPerSecond), e.Level, e.Area, e.Message);

    // The file is open for appending while the app runs; read it without asking for exclusive access.
    private static string ReadSharedText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    public static void ClearBuffer()
    {
        lock (Gate)
        {
            Array.Clear(Ring);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>The text one entry takes in the file and in the viewer's copy button.</summary>
    public static string Format(LogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
          .Append(e.Level.ToString().ToUpperInvariant()).Append(' ').Append(e.Area).Append(' ').Append(e.Message);
        if (!string.IsNullOrEmpty(e.Detail)) sb.Append('\n').Append(e.Detail);
        return sb.ToString();
    }

    // Caller holds Gate.
    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                File.Move(path, path + ".1", overwrite: true);   // rotate: keep one older generation
            var isNew = !File.Exists(path);
            if (isNew)
            {
                // The log starts out private, so nothing is appended to a file with an inherited or foreign ACL.
                try { FileProtection.CreatePrivateFile(path).Dispose(); }
                catch { /* best-effort: RestrictFile below still runs */ }
            }
            File.AppendAllText(path, Format(entry) + "\n\n", new UTF8Encoding(false));
            if (isNew) FileProtection.RestrictFile(path);   // current user only
            else if (!_aclChecked)
            {
                // A log from an earlier version still has an inherited ACL.
                _aclChecked = true;
                FileProtection.EnsureFilePrivate(path);
            }
        }
        catch { /* the file is best-effort; the buffer still has the entry */ }
    }
}
