using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>The log is one static sink, so every test that redirects its file or flips the verbose
/// switch runs in this collection (one at a time) and restores the defaults afterwards.</summary>
[CollectionDefinition("LogFile", DisableParallelization = true)]
public class LogFileCollection { }

[Collection("LogFile")]
public class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipscanner-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _origPath = Log.FilePath;
    private readonly long _origMax = Log.MaxFileBytes;
    private readonly Func<bool> _origVerbose = Log.VerboseProvider;

    public LogTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FilePath = Path.Combine(_dir, "error.log");
        Log.MaxFileBytes = Log.DefaultMaxFileBytes;
        Log.VerboseProvider = () => false;
    }

    public void Dispose()
    {
        Log.FilePath = _origPath;
        Log.MaxFileBytes = _origMax;
        Log.VerboseProvider = _origVerbose;
        try { Directory.Delete(_dir, true); } catch { /* temp folder, best effort */ }
    }

    private static string Marker() => "m-" + Guid.NewGuid().ToString("N");

    private static LogEntry[] Rows(string marker) =>
        Log.Snapshot().Where(e => e.Message.Contains(marker)).ToArray();

    [Fact]
    public void Warn_IsBufferedAndWrittenToTheFile()
    {
        var m = Marker();
        Log.Warn(LogArea.Dns, m, new InvalidOperationException("boom"));

        var row = Assert.Single(Rows(m));
        Assert.Equal(LogLevel.Warn, row.Level);
        Assert.Equal(LogArea.Dns, row.Area);
        Assert.Contains("boom", row.Detail);
        var text = File.ReadAllText(Log.FilePath);
        Assert.Contains(m, text);
        Assert.Contains("WARN Dns", text);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]", text);
    }

    [Fact]
    public void VerboseOff_KeepsInfoAndDebugOutOfBufferAndFile()
    {
        var m = Marker();
        Log.Info(LogArea.Scan, "info " + m);
        Log.Debug(LogArea.Scan, "debug " + m);
        Log.Warn(LogArea.Scan, "warn " + m);
        Log.Error(LogArea.Scan, "error " + m);

        Assert.Equal(new[] { LogLevel.Warn, LogLevel.Error }, Rows(m).Select(r => r.Level).ToArray());
        var text = File.ReadAllText(Log.FilePath);
        Assert.DoesNotContain("INFO", text);
        Assert.DoesNotContain("DEBUG", text);
        Assert.Contains("WARN", text);
        Assert.Contains("ERROR", text);
    }

    [Fact]
    public void VerboseOn_KeepsInfoAndDebugToo()
    {
        Log.VerboseProvider = () => true;
        var m = Marker();
        Log.Info(LogArea.Tools, "info " + m);
        Log.Debug(LogArea.Tools, "debug " + m);

        Assert.Equal(new[] { LogLevel.Info, LogLevel.Debug }, Rows(m).Select(r => r.Level).ToArray());
        var text = File.ReadAllText(Log.FilePath);
        Assert.Contains("INFO Tools", text);
        Assert.Contains("DEBUG Tools", text);
    }

    [Fact]
    public void VerboseProvider_ThatThrows_CountsAsOff()
    {
        Log.VerboseProvider = () => throw new InvalidOperationException("no config yet");
        var m = Marker();
        Log.Info(LogArea.App, m);
        Assert.False(Log.IsVerbose);
        Assert.Empty(Rows(m));
    }

    [Fact]
    public void RingBuffer_HoldsTheLast2000_OldestFirst()
    {
        var m = Marker();
        for (int i = 0; i < Log.BufferCapacity + 100; i++) Log.Warn(LogArea.App, $"{m} #{i}");

        var all = Log.Snapshot();
        Assert.Equal(Log.BufferCapacity, all.Count);
        var ours = Rows(m);
        Assert.DoesNotContain(ours, e => e.Message.EndsWith(" #0"));
        Assert.Contains(ours, e => e.Message.EndsWith($" #{Log.BufferCapacity + 99}"));
        var idx = ours.Select(e => int.Parse(e.Message[(e.Message.LastIndexOf('#') + 1)..])).ToArray();
        Assert.Equal(idx.OrderBy(x => x).ToArray(), idx);
    }

    [Fact]
    public void File_RotatesOverTheCap_AndKeepsOneOlderGeneration()
    {
        Log.MaxFileBytes = 2000;
        var m = Marker();
        for (int i = 0; i < 40; i++) Log.Warn(LogArea.App, $"{m} row {i} " + new string('x', 80));

        var rotated = Log.FilePath + ".1";
        Assert.True(File.Exists(rotated));
        Assert.True(new FileInfo(Log.FilePath).Length < new FileInfo(rotated).Length + 2000);
        Assert.True(new FileInfo(Log.FilePath).Length <= 2000 + 400, "the live file stays near the cap");
        Assert.Contains(m, File.ReadAllText(rotated));
        Assert.Contains($"{m} row 39", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public async Task ConcurrentWrites_AreAllInTheFile_NoneTorn()
    {
        var m = Marker();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) Log.Warn(LogArea.Scan, $"{m} t{t} i{i}");
        })));

        var lines = File.ReadAllLines(Log.FilePath).Where(l => l.Contains(m)).ToArray();
        Assert.Equal(800, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("[", l));
        Assert.Equal(800, lines.Distinct().Count());
    }

    [Fact]
    public void Added_FiresForStoredEntries_NotForDroppedOnes()
    {
        var m = Marker();
        var seen = new System.Collections.Concurrent.ConcurrentBag<LogEntry>();
        void Handler(LogEntry e) { if (e.Message.Contains(m)) seen.Add(e); }
        Log.Added += Handler;
        try
        {
            Log.Info(LogArea.Ui, m);
            Log.Warn(LogArea.Ui, m);
        }
        finally { Log.Added -= Handler; }
        Assert.Equal(LogLevel.Warn, Assert.Single(seen).Level);
    }

    [Fact]
    public void Format_PutsTheExceptionTextBelowTheHeadline()
    {
        var e = new LogEntry(new DateTime(2026, 3, 4, 5, 6, 7), LogLevel.Error, LogArea.Db, "headline", "stack line");
        Assert.Equal("[2026-03-04 05:06:07] ERROR Db headline\nstack line", Log.Format(e));
        Assert.Equal("[2026-03-04 05:06:07] ERROR Db headline", Log.Format(e with { Detail = null }));
    }

    // A second connection holds the write lock; the first one gives up after one second, which
    // is what the archive write does when another process has the database open.
    [Fact]
    public void LockedDatabase_YieldsExactlyOneWarningRow_WithReadableTitleAndFullTextOnExpand()
    {
        var db = Path.Combine(_dir, "locked.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = db, DefaultTimeout = 1, Pooling = false }.ConnectionString;
        using var holder = new SqliteConnection(cs);
        holder.Open();
        Exec(holder, "CREATE TABLE t (x INTEGER)");
        Exec(holder, "BEGIN EXCLUSIVE");

        var m = Marker();
        try
        {
            using var other = new SqliteConnection(cs);
            other.Open();
            Exec(other, "INSERT INTO t VALUES (1)");
            Assert.Fail("the insert should have hit the lock");
        }
        catch (SqliteException ex)
        {
            Log.Warn(LogArea.Db, "Scan archive write failed " + m, ex);
        }

        var row = Assert.Single(Rows(m));
        Assert.Equal(LogLevel.Warn, row.Level);
        Assert.Equal(Loc.LogTitleDbLocked, LogTitles.For(row));
        Assert.Contains("locked", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SqliteException", row.Detail);   // the expanded view shows this full text
        Assert.Contains("Scan archive write failed", Log.Format(row));
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task OfflineUpdateCheck_IsInfoNotWarn_ButARefusedRedirectStaysWarn()
    {
        Log.VerboseProvider = () => true;
        Log.ClearBuffer();
        var offline = await UpdateService.CheckAsync(new ThrowingHandler(new System.Net.Http.HttpRequestException("No such host is known")));
        Assert.Equal(UpdateService.CheckStatus.Failed, offline.Status);
        var entry = Assert.Single(Log.Snapshot().Where(e => e.Area == LogArea.Update));
        Assert.Equal(LogLevel.Info, entry.Level);

        Log.ClearBuffer();
        await UpdateService.CheckAsync(new ThrowingHandler(new System.Net.Http.HttpRequestException("redirect refused")));
        Assert.Equal(LogLevel.Warn, Assert.Single(Log.Snapshot().Where(e => e.Area == LogArea.Update)).Level);
    }

    private sealed class ThrowingHandler(Exception ex) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct) => Task.FromException<System.Net.Http.HttpResponseMessage>(ex);
    }

    [Fact]
    public void LoadHistory_FillsAnEmptyBufferFromTheFileOfAnEarlierSession()
    {
        var m = Marker();
        Log.Warn(LogArea.Db, "first " + m, new InvalidOperationException("boom\nsecond line"));
        Log.Error(LogArea.Scan, "second " + m);
        Log.ClearBuffer();   // a new process starts with an empty buffer; the file stays
        Assert.Empty(Rows(m));

        Assert.Equal(2, Log.LoadHistory());

        var rows = Rows(m);
        Assert.Equal(new[] { LogLevel.Warn, LogLevel.Error }, rows.Select(r => r.Level).ToArray());
        Assert.Equal(new[] { LogArea.Db, LogArea.Scan }, rows.Select(r => r.Area).ToArray());
        Assert.Contains("second line", rows[0].Detail);
        Assert.Null(rows[1].Detail);
    }

    [Fact]
    public void LoadHistory_DoesNotDuplicateEntriesTheBufferHolds_AndCanRepeat()
    {
        var m = Marker();
        Log.Warn(LogArea.Dns, "same " + m);
        Log.Warn(LogArea.Dns, "same " + m);

        Assert.Equal(0, Log.LoadHistory());
        Assert.Equal(2, Rows(m).Length);
        Log.ClearBuffer();
        Assert.Equal(2, Log.LoadHistory());
        Assert.Equal(0, Log.LoadHistory());
        Assert.Equal(2, Rows(m).Length);
    }

    [Fact]
    public void LoadHistory_MultiLineMessage_RoundTripsWithoutADuplicate()
    {
        var m = Marker();
        Log.Error(LogArea.App, "crash " + m + "\nat Foo.Bar()\nat Baz()");

        var buffered = Rows(m);
        Assert.Single(buffered);
        Assert.Equal("crash " + m, buffered[0].Message);
        Assert.Equal("at Foo.Bar()\nat Baz()", buffered[0].Detail);

        Assert.Equal(0, Log.LoadHistory());
        Assert.Single(Rows(m));

        Log.ClearBuffer();
        Assert.Equal(1, Log.LoadHistory());
        var reloaded = Rows(m);
        Assert.Single(reloaded);
        Assert.Equal(buffered[0].Message, reloaded[0].Message);
        Assert.Equal(buffered[0].Detail, reloaded[0].Detail);
    }

    [Fact]
    public void LoadHistory_MultiLineMessageWithException_KeepsBothInTheDetail()
    {
        var m = Marker();
        Log.Error(LogArea.App, "crash " + m + "\r\nextra", new InvalidOperationException("boom"));

        var row = Assert.Single(Rows(m));
        Assert.Equal("crash " + m, row.Message);
        Assert.StartsWith("extra\n", row.Detail);
        Assert.Contains("boom", row.Detail);
        Assert.Equal(0, Log.LoadHistory());
        Assert.Single(Rows(m));
    }

    [Fact]
    public void LoadHistory_ReadsTheRotatedGenerationFirst_AndKeepsNewSessionEntriesLast()
    {
        var m = Marker();
        File.WriteAllText(Log.FilePath + ".1", "[2020-01-01 10:00:00] ERROR App old " + m + "\n\n");
        File.WriteAllText(Log.FilePath, "[2020-01-02 10:00:00] WARN Ui newer " + m + "\n\n");
        Log.Warn(LogArea.App, "live " + m);

        Log.LoadHistory();

        Assert.Equal(new[] { "old " + m, "newer " + m, "live " + m }, Rows(m).Select(r => r.Message).ToArray());
    }

    [Fact]
    public void LoadHistory_WithoutAFile_AddsNothing()
    {
        Assert.False(File.Exists(Log.FilePath));
        Assert.Equal(0, Log.LoadHistory());
    }

    [Fact]
    public void FileParser_RoundTripsFormat_AndIgnoresTextBeforeTheFirstEntry()
    {
        var entry = new LogEntry(new DateTime(2026, 3, 4, 5, 6, 7), LogLevel.Error, LogArea.Update, "Download failed", "System.IO.IOException: x\n   at A.B()");
        var parsed = LogFileParser.Parse("leftover text\n" + Log.Format(entry) + "\n\n" + Log.Format(entry with { Detail = null }) + "\n\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal(entry, parsed[0]);
        Assert.Null(parsed[1].Detail);
        Assert.Empty(LogFileParser.Parse(""));
    }

    [Fact]
    public void VerboseLog_RoundTripsThroughTheConfigFile_AndDefaultsToOff()
    {
        Assert.False(new ScanConfig().VerboseLog);
        var path = Path.Combine(_dir, "t.conf");
        ConfigManager.Save(path, new ScanConfig { VerboseLog = true });
        Assert.True(ConfigManager.Load(path).VerboseLog);
        ConfigManager.Save(path, new ScanConfig { VerboseLog = false });
        Assert.False(ConfigManager.Load(path).VerboseLog);
    }
}
