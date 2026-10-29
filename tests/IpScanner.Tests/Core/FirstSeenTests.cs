using System;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IpScanner.Tests.Core;

public class FirstSeenTests
{
    private const string Network = "AA:BB:CC:11:22:33";
    private const string Mac = "DD:EE:FF:00:11:22";

    private static string DbPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");

    private static Device Online(string ip = "192.0.2.7")
    {
        var d = new Device(ip) { Mac = Mac, Hostname = "test-host" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        return d;
    }

    private static Device Offline() => new("192.0.2.7") { Mac = Mac, Hostname = "test-host" };

    private static DateTimeOffset At(int y, int mo, int d, int h) => new(new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Local));

    [Fact]
    public void Save_SetsFirstSeenOnTheFirstInsertOnly_AndLastSeenOnEveryAnswer()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        db.Save(Network, [Online()], "20260101_100000");
        db.Save(Network, [Online()], "20260301_110000");

        var d = Assert.Single(new KnownDevicesDb(path).Load(Network));
        Assert.Equal(At(2026, 1, 1, 10), d.FirstSeen);
        Assert.Equal(At(2026, 3, 1, 11), d.LastSeen);
    }

    [Fact]
    public void Save_AnOfflineDeviceKeepsItsLastSeenTime()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        db.Save(Network, [Online()], "20260101_100000");
        db.Save(Network, [Offline()], "20260301_110000");

        var d = Assert.Single(new KnownDevicesDb(path).Load(Network));
        Assert.Equal(At(2026, 1, 1, 10), d.FirstSeen);
        Assert.Equal(At(2026, 1, 1, 10), d.LastSeen);
        Assert.Equal("test-host", d.Hostname);
    }

    [Fact]
    public void Open_AddsFirstAndLastSeenToRowsFromBeforeTheyExisted()
    {
        var path = DbPath();
        new KnownDevicesDb(path).Save(Network, [Online()], "20250505_080000");
        SqliteConnection.ClearAllPools();

        // Put the row back into the old shape: a payload of four fields, no first/last seen.
        var crypto = LocalCrypto.TryLoad(Path.Combine(Path.GetDirectoryName(path)!, KnownDevicesDb.KeyFileName))!;
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE known_devices_v2 SET payload=$p";
            cmd.Parameters.AddWithValue("$p", crypto.Seal($"{Network}\t{Mac}\t192.0.2.7\ttest-host"));
            cmd.ExecuteNonQuery();
        }
        KnownDevicesDb.ForgetInitializedFiles();

        var d = Assert.Single(new KnownDevicesDb(path).Load(Network));
        Assert.Equal(At(2025, 5, 5, 8), d.FirstSeen);
        Assert.Equal(At(2025, 5, 5, 8), d.LastSeen);
        Assert.Equal("test-host", d.Hostname);

        // The stamps were written into the sealed payload, not only filled in on read.
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT payload FROM known_devices_v2";
        var fields = crypto.Open((byte[])read.ExecuteScalar()!)!.Split('\t');
        Assert.Equal(6, fields.Length);
        Assert.Equal("20250505_080000", fields[4]);
        Assert.Equal("20250505_080000", fields[5]);
    }

    [Fact]
    public void Stamps_StayUnreadableInTheFile()
    {
        var path = DbPath();
        new KnownDevicesDb(path).Save(Network, [Online()], "20260101_100000");
        SqliteConnection.ClearAllPools();
        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("first_seen", System.Text.Encoding.ASCII.GetString(raw));
    }

    [Fact]
    public void ExportAndImportCsv_KeepsTheEarlierFirstSeenOfTheTarget()
    {
        var source = DbPath();
        new KnownDevicesDb(source).Save(Network, [Online()], "20260301_100000");
        var csv = Path.Combine(Directory.CreateTempSubdirectory().FullName, "devices.csv");
        new KnownDevicesDb(source).ExportCsv(csv);

        var target = DbPath();
        new KnownDevicesDb(target).Save(Network, [Online()], "20260101_100000");
        new KnownDevicesDb(target).ImportCsv(csv);

        var d = Assert.Single(new KnownDevicesDb(target).Load(Network));
        Assert.Equal(At(2026, 1, 1, 10), d.FirstSeen);
        Assert.Equal(At(2026, 3, 1, 10), d.LastSeen);
    }

    [Fact]
    public void Viewmodel_ShowsLocalShortDateTime_AndSortsByTime()
    {
        var older = new Device("192.0.2.1") { FirstSeen = At(2026, 1, 1, 10), LastSeen = At(2026, 2, 1, 10) };
        var newer = new Device("192.0.2.2") { FirstSeen = At(2026, 1, 5, 10), LastSeen = At(2026, 3, 1, 10) };
        var none = new Device("192.0.2.3");
        var a = new DeviceViewModel(older);
        var b = new DeviceViewModel(newer);
        var c = new DeviceViewModel(none);

        Assert.Equal(At(2026, 1, 1, 10).LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture), a.FirstSeenText);
        Assert.Equal("–", c.FirstSeenText);
        Assert.True(c.LastSeenSortKey < a.LastSeenSortKey && a.LastSeenSortKey < b.LastSeenSortKey);
        Assert.True(c.FirstSeenSortKey < a.FirstSeenSortKey && a.FirstSeenSortKey < b.FirstSeenSortKey);
    }

    [Fact]
    public void Viewmodel_AnOnlineDeviceIsLastSeenNow_AndFirstSeenNowWhenUnknown()
    {
        var vm = new DeviceViewModel(Online());
        vm.Refresh();
        Assert.NotEqual("–", vm.LastSeenText);
        Assert.NotEqual("–", vm.FirstSeenText);
        Assert.True(Math.Abs((DateTimeOffset.Now - new DateTimeOffset(vm.LastSeenSortKey, TimeSpan.Zero)).TotalMinutes) < 2);
    }

    [Fact]
    public void ConfigDefault_HidesTheTwoSeenColumns_AndAnOldConfigStaysAsItWas()
    {
        Assert.Contains("first_seen", new ScanConfig().HiddenColumns);
        Assert.Contains("last_seen", new ScanConfig().HiddenColumns);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        try
        {
            File.WriteAllText(path, "hidden_columns = jitter,os\n");
            var old = ConfigManager.Load(path);
            Assert.Equal(new[] { "jitter", "os", "first_seen", "last_seen" }, old.HiddenColumns);

            // Once saved, the user's choice sticks: showing the column keeps it shown.
            old.HiddenColumns = new() { "jitter" };
            ConfigManager.Save(path, old);
            Assert.Equal(new[] { "jitter" }, ConfigManager.Load(path).HiddenColumns);
        }
        finally { File.Delete(path); }
    }
}
