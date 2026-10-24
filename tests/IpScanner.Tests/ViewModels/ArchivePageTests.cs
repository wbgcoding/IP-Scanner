using System;
using System.Globalization;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests.ViewModels;

[Collection("Localization")]
public class ArchivePageTests
{
    private static ArchivedScan ScanWith(string network, string[] subnets)
    {
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        return new ArchivedScan(1, at, at.AddMinutes(1), network, subnets, "{}", 5, 3, 10, 2, 12);
    }

    [Fact]
    public void RunRow_NetworkText_ListsEverySubnetOfTheScan()
    {
        var row = new ArchivePage.RunRow(ScanWith("192.168.1.0/24", ["192.168.1.0/24", "10.0.0.0/24"]));

        Assert.Equal("192.168.1.0/24 +1", row.NetworkText);
        Assert.Equal("192.168.1.0/24, 10.0.0.0/24", row.NetworkFull);
        Assert.Equal("192.168.1.0/24", row.Network);   // stays the grouping key
    }

    [Fact]
    public void RunRow_NetworkText_FallsBackToTheNetworkWithoutSubnets()
        => Assert.Equal("192.168.1.0/24", new ArchivePage.RunRow(ScanWith("192.168.1.0/24", [])).NetworkText);

    [Fact]
    public void DateRange_IncludesBothLocalCalendarDays()
    {
        var (from, through) = ArchivePage.DateRange(new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));

        Assert.Equal(new DateTime(2026, 9, 1), from!.Value.LocalDateTime.Date);
        Assert.Equal(new DateTime(2026, 9, 3, 23, 59, 59, 999).Date, through!.Value.LocalDateTime.Date);
        Assert.Equal(TimeSpan.TicksPerDay - 1, through.Value.LocalDateTime.TimeOfDay.Ticks);
        Assert.ThrowsAny<ArgumentException>(() => ArchivePage.DateRange(new DateTime(2026, 9, 4), new DateTime(2026, 9, 3)));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("36501", false)]
    [InlineData("text", false)]
    [InlineData("1", true)]
    [InlineData("36500", true)]
    public void RetentionDays_RefusesZeroAndOutOfRangeValues(string text, bool valid)
    {
        Assert.Equal(valid, ArchivePage.TryRetentionDays(text, CultureInfo.InvariantCulture, out _));
    }

    [Theory]
    [InlineData(30, 365, true)]
    [InlineData(365, 365, false)]
    [InlineData(400, 365, false)]
    [InlineData(400, 0, true)]
    public void CompactionDays_MustStayBelowAFiniteRetention(int days, int retention, bool allowed)
    {
        Assert.Equal(allowed, ArchivePage.CompactionDaysAllowed(days, retention));
    }

    [Fact]
    public void ArchiveImportFilter_IncludesPreviousGenerationRecoveryFiles()
    {
        Assert.Contains("*.json.bak", Loc.ArchiveFileFilter);
    }

    [Fact]
    public void BackupIfAny_WithoutRuns_WritesNoFile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var db = new KnownDevicesDb(Path.Combine(dir, "scanner.db"));
        var backups = Path.Combine(dir, "backups");

        Assert.Null(ArchivePage.BackupIfAny(db, backups));
        Assert.False(Directory.Exists(backups) && Directory.GetFiles(backups).Length > 0);
    }

    [Fact]
    public void BackupIfAny_WithARun_WritesTheBackup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var db = new KnownDevicesDb(Path.Combine(dir, "scanner.db"));
        var start = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        db.SaveScanArchive(start, start.AddMinutes(1), "network", new[] { "192.0.2.0/24" },
            "{}", new[] { new Device("192.0.2.8") { Mac = "DD:EE:FF:00:11:22" } }, Array.Empty<IpScanner.Core.Scanner.ConflictFinding>());

        var created = ArchivePage.BackupIfAny(db, Path.Combine(dir, "backups"));

        Assert.NotNull(created);
        Assert.True(File.Exists(created));
    }

    [Fact]
    public void BackupIfAny_WithoutUsableKey_IsAKeyFailureNotAnEmptyArchive()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, KnownDevicesDb.KeyFileName), "not a protected key");
        var db = new KnownDevicesDb(Path.Combine(dir, "scanner.db"));

        Assert.False(db.IsUsable);
        Assert.Throws<ArchivePage.ArchiveKeyException>(() => ArchivePage.BackupIfAny(db, Path.Combine(dir, "backups")));
    }
}
