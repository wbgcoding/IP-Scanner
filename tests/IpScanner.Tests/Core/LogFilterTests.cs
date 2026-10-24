using System;
using System.Collections.Generic;
using IpScanner.Core;
using Xunit;

namespace IpScanner.Tests.Core;

public class LogFilterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static LogEntry E(int n, LogLevel level, LogArea area, string message, string? detail = null) =>
        new(T0.AddSeconds(n), level, area, message, detail);

    private static readonly IReadOnlyList<LogEntry> Sample =
    [
        E(0, LogLevel.Info, LogArea.App, "Session started"),
        E(1, LogLevel.Warn, LogArea.Db, "Scan archive write failed", "SqliteException: SQLite Error 5: 'database is locked'."),
        E(2, LogLevel.Error, LogArea.Scan, "Starting the scan failed", "InvalidOperationException: bad subnet"),
        E(3, LogLevel.Debug, LogArea.Ports, "TCP probe failed: SocketException"),
        E(4, LogLevel.Warn, LogArea.Ui, "Opening the browser failed"),
    ];

    [Fact]
    public void NoFilter_ReturnsEverything_NewestFirst()
    {
        var rows = LogFilter.Apply(Sample, null, null, null);
        Assert.Equal(Sample.Count, rows.Count);
        Assert.Equal(Sample[4], rows[0]);
        Assert.Equal(Sample[0], rows[^1]);
    }

    [Fact]
    public void AreaFilter_KeepsOnlyThatArea()
    {
        var rows = LogFilter.Apply(Sample, LogArea.Db, null, null);
        Assert.Single(rows);
        Assert.Equal(LogArea.Db, rows[0].Area);
    }

    [Fact]
    public void LevelFilter_KeepsOnlyThatLevel()
    {
        var rows = LogFilter.Apply(Sample, null, LogLevel.Warn, null);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(LogLevel.Warn, r.Level));
    }

    [Fact]
    public void AreaAndLevel_Combine()
    {
        Assert.Single(LogFilter.Apply(Sample, LogArea.Ui, LogLevel.Warn, null));
        Assert.Empty(LogFilter.Apply(Sample, LogArea.Ui, LogLevel.Error, null));
    }

    [Theory]
    [InlineData("SESSION", 1)]          // case-insensitive, in the message
    [InlineData("bad subnet", 1)]       // in the exception text
    [InlineData("probe", 1)]
    [InlineData("  failed  ", 4)]       // trimmed
    [InlineData("no such text anywhere", 0)]
    public void Search_MatchesMessageAndDetail(string text, int expected) =>
        Assert.Equal(expected, LogFilter.Apply(Sample, null, null, text).Count);

    [Fact]
    public void Search_MatchesTheReadableTitle()
    {
        // The locked-database row is found by its title although the title is not in the stored text.
        var title = LogTitles.For(Sample[1])!;
        var rows = LogFilter.Apply(Sample, null, null, title);
        Assert.Contains(Sample[1], rows);
    }

    [Fact]
    public void Search_MatchesAreaAndLevelNames()
    {
        Assert.Contains(Sample[2], LogFilter.Apply(Sample, null, null, LogFilter.AreaName(LogArea.Scan)));
        Assert.Contains(Sample[2], LogFilter.Apply(Sample, null, null, LogFilter.LevelName(LogLevel.Error)));
    }

    [Fact]
    public void BlankSearch_IsNoFilter() =>
        Assert.Equal(Sample.Count, LogFilter.Apply(Sample, null, null, "   ").Count);

    [Fact]
    public void EveryAreaAndLevel_HasAReadableName()
    {
        foreach (var a in Enum.GetValues<LogArea>())
        {
            Assert.False(string.IsNullOrWhiteSpace(LogFilter.AreaName(a)));
        }
        foreach (var l in Enum.GetValues<LogLevel>())
            Assert.False(string.IsNullOrWhiteSpace(LogFilter.LevelName(l)));
    }

    [Fact]
    public void EmptyBuffer_GivesNoRows() =>
        Assert.Empty(LogFilter.Apply([], null, null, "x"));
}
