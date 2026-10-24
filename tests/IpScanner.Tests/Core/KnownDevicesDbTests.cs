using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class KnownDevicesDbTests
{
    // Scanner.key sits next to the .db file, so each test needs its OWN
    // directory - sharing the flat %TEMP% root would make unrelated tests (and test
    // classes, which xUnit can run in parallel) race on the very same key file.
    private static string DbPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");

    [Fact]
    public void PortScan_OldSealedDatabaseMigratesWithoutLosingDevices()
    {
        var dbPath = DbPath();
        const string network = "AA:BB:CC:11:22:33";
        var original = new KnownDevicesDb(dbPath);
        original.Save(network, new[] { new Device("192.0.2.7") { Mac = "DD:EE:FF:00:11:22", Hostname = "test-host" } },
            "20260919_120000");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE device_ports";
            drop.ExecuteNonQuery();
        }
        KnownDevicesDb.ForgetInitializedFiles();

        var reopened = new KnownDevicesDb(dbPath);
        var seenAt = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        reopened.SavePortScan(network, "DD:EE:FF:00:11:22", "192.0.2.7",
            new[] { new NetService("HTTPS", 443, NetProtocol.Tcp, "test-banner") }, seenAt, false);

        var stored = Assert.IsType<KnownDevicesDb.StoredPortScan>(
            new KnownDevicesDb(dbPath).LoadPortScan(network, "DD:EE:FF:00:11:22", "192.0.2.7"));
        Assert.Equal(seenAt, stored.SeenAt);
        Assert.False(stored.Partial);
        Assert.Equal("test-banner", Assert.Single(stored.Services).Version);
        Assert.Equal("test-host", Assert.Single(reopened.Load(network)).Hostname);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var raw = File.ReadAllText(dbPath, System.Text.Encoding.Latin1);
        Assert.DoesNotContain("192.0.2.7", raw);
        Assert.DoesNotContain("DD:EE:FF:00:11:22", raw);
        Assert.DoesNotContain("test-banner", raw);
    }

    [Fact]
    public void Archive_V210DatabaseMigratesAndPreservesDevicesThroughArchiveWriteRead()
    {
        var dbPath = DbPath();
        const string network = "AA:BB:CC:11:22:33";
        const string mac = "DD:EE:FF:00:11:22";
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            conn.CreateCommand("""
                CREATE TABLE known_devices (
                  network_mac TEXT NOT NULL, mac TEXT NOT NULL, ip TEXT,
                  hostname TEXT, last_seen TEXT, PRIMARY KEY (network_mac, mac))
                """).ExecuteNonQuery();
            using var insert = conn.CreateCommand("INSERT INTO known_devices VALUES ($n,$m,$i,$h,$t)");
            insert.Parameters.AddWithValue("$n", network);
            insert.Parameters.AddWithValue("$m", mac);
            insert.Parameters.AddWithValue("$i", "192.0.2.19");
            insert.Parameters.AddWithValue("$h", "legacy-nas");
            insert.Parameters.AddWithValue("$t", "20260919_120000");
            insert.ExecuteNonQuery();
        }

        var db = new KnownDevicesDb(dbPath);
        var legacyDevice = Assert.Single(db.Load(network));
        var at = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var archiveId = db.SaveScanArchive(at, at.AddMinutes(1), network, ["192.0.2.0/24"], "{}",
            [legacyDevice], []);

        var archived = Assert.IsType<ScanArchiveDetail>(db.LoadScanArchive(archiveId));
        var archivedDevice = Assert.Single(archived.Devices);
        Assert.Equal("legacy-nas", archivedDevice.Hostname);
        Assert.Equal("192.0.2.19", archivedDevice.Ip);
        Assert.Equal(mac, archivedDevice.Mac);
        Assert.Equal("legacy-nas", Assert.Single(new KnownDevicesDb(dbPath).Load(network)).Hostname);
    }

    [Fact]
    public void PortScan_CancelledResultReplacesPreviousAndSurvivesEmptyResult()
    {
        var db = new KnownDevicesDb(DbPath());
        var seenAt = DateTimeOffset.UtcNow;
        db.SavePortScan("network", "device", "192.0.2.8",
            new[] { new NetService("HTTP", 80, NetProtocol.Tcp) }, seenAt.AddMinutes(-1), false);
        db.SavePortScan("network", "device", "192.0.2.8", Array.Empty<NetService>(), seenAt, true);

        var stored = Assert.IsType<KnownDevicesDb.StoredPortScan>(db.LoadPortScan("network", "device", "192.0.2.8"));
        Assert.True(stored.Partial);
        Assert.Empty(stored.Services);
        Assert.Equal(seenAt, stored.SeenAt);
    }

    [Fact]
    public void PortScan_OldResultFollowsDatabaseRetention()
    {
        var db = new KnownDevicesDb(DbPath());
        db.SavePortScan("network", "device", "192.0.2.8", Array.Empty<NetService>(),
            DateTimeOffset.UtcNow.AddDays(-400), false);

        Assert.True(db.PurgeOlderThan(365) > 0);
        Assert.Null(db.LoadPortScan("network", "device", "192.0.2.8"));
    }

    [Fact]
    public void Save_ThenLoad_ReturnsDevices()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        var devices = new List<Device>
        {
            new("192.168.1.1") { Mac = netMac, Hostname = "router" },
            new("192.168.1.5") { Mac = "DD:EE:FF:00:11:22", Hostname = "nas" },
        };
        db.Save(netMac, devices, "2026-06-01 12:00:00");

        var loaded = db.Load(netMac);
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, d => Assert.False(d.IsOnline));
        Assert.All(loaded, d => Assert.True(d.FromDb));
    }

    [Fact]
    public void Save_StoresNoReadablePlainText_InTheRawDatabaseFile()
    {
        // Opening scanner.db as raw bytes must
        // never reveal an IP, MAC or hostname - only tokens and a sealed blob.
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        db.Save("AA:BB:CC:11:22:33",
            new List<Device> { new("192.168.1.42") { Mac = "AA:BB:CC:11:22:33", Hostname = "my-secret-nas" } },
            "20260910_120000");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var raw = File.ReadAllText(dbPath, System.Text.Encoding.Latin1);
        Assert.DoesNotContain("192.168.1.42", raw);
        Assert.DoesNotContain("AA:BB:CC:11:22:33", raw);
        Assert.DoesNotContain("my-secret-nas", raw);
    }

    [Fact]
    public void MergeFrom_SourceKeyUnusable_ThrowsForeignDatabaseException()
    {
        // A v2 database whose sibling scanner.key belongs to a different
        // Windows account must be refused, never silently produce garbage/empty rows.
        string PathFor() => DbPath();
        var mainPath = PathFor();
        var otherPath = PathFor();
        var main = new KnownDevicesDb(mainPath);
        var other = new KnownDevicesDb(otherPath);
        other.Save("AA:BB:CC:11:22:33",
            new List<Device> { new("192.168.1.5") { Mac = "DD:EE:FF:00:11:22" } }, "20260601_000000");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var otherKeyPath = Path.Combine(Path.GetDirectoryName(otherPath)!, "scanner.key");
        File.WriteAllBytes(otherKeyPath, System.Text.Encoding.UTF8.GetBytes("not a real DPAPI blob"));

        Assert.Throws<ForeignDatabaseException>(() => main.MergeFrom(otherPath));
    }

    [Fact]
    public void ExportCsv_ThenImportCsv_RoundTripsIntoAnotherDatabase()
    {
        // The CSV export/import is the only way to move device history
        // between two different Windows accounts (or off a foreign-key database).
        var dbPath = DbPath();
        var csvPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        var source = new KnownDevicesDb(dbPath);
        source.Save("AA:BB:CC:11:22:33",
            new List<Device> { new("192.168.1.9") { Mac = "DD:EE:FF:00:11:22", Hostname = "cam" } },
            "20260601_120000");

        Assert.Equal(1, source.ExportCsv(csvPath));
        Assert.Contains("cam", File.ReadAllText(csvPath));   // plain, portable on purpose

        var otherDbPath = DbPath();
        var target = new KnownDevicesDb(otherDbPath);
        Assert.Equal((1, 0), target.ImportCsv(csvPath));
        var loaded = Assert.Single(target.Load("AA:BB:CC:11:22:33"));
        Assert.Equal("192.168.1.9", loaded.Ip);
        Assert.Equal("cam", loaded.Hostname);
    }

    private const string CsvHeader = "network_mac,mac,ip,hostname,last_seen";

    private static string WriteCsv(params string[] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        File.WriteAllLines(path, new[] { CsvHeader }.Concat(rows));
        return path;
    }

    [Fact]
    public void ExportCsv_DefusesFormulaHostname_AndImportRestoresItExactly()
    {
        const string net = "AA:BB:CC:11:22:33";
        const string host = "=HYPERLINK(\"x\")";
        var source = new KnownDevicesDb(DbPath());
        source.Save(net, new List<Device> { new("192.168.1.9") { Mac = "DD:EE:FF:00:11:22", Hostname = host } },
            "20260601_120000");
        var csvPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");

        Assert.Equal(1, source.ExportCsv(csvPath));
        var text = File.ReadAllText(csvPath);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        Assert.DoesNotContain(",=HYPERLINK", text);

        var target = new KnownDevicesDb(DbPath());
        Assert.Equal((1, 0), target.ImportCsv(csvPath));
        Assert.Equal(host, Assert.Single(target.Load(net)).Hostname);
    }

    [Fact]
    public void ImportCsv_OlderRowDoesNotOverwriteNewer_NewerRowDoes()
    {
        const string net = "AA:BB:CC:11:22:33";
        var db = new KnownDevicesDb(DbPath());
        db.Save(net, new List<Device> { new("192.168.1.9") { Mac = "DD:EE:FF:00:11:22", Hostname = "kept" } },
            "20260601_120000");

        var older = WriteCsv($"{net},DD:EE:FF:00:11:22,192.168.1.50,stale,20260101_000000");
        Assert.Equal((0, 1), db.ImportCsv(older));
        var loaded = Assert.Single(db.Load(net));
        Assert.Equal("192.168.1.9", loaded.Ip);
        Assert.Equal("kept", loaded.Hostname);

        var newer = WriteCsv($"{net},DD:EE:FF:00:11:22,192.168.1.50,fresh,20260701_000000");
        Assert.Equal((1, 0), db.ImportCsv(newer));
        loaded = Assert.Single(db.Load(net));
        Assert.Equal("192.168.1.50", loaded.Ip);
        Assert.Equal("fresh", loaded.Hostname);
    }

    [Fact]
    public void ImportCsv_SkipsInvalidIpMacAndFutureDate_AndCountsThem()
    {
        const string net = "AA:BB:CC:11:22:33";
        var db = new KnownDevicesDb(DbPath());
        var csv = WriteCsv(
            $"{net},DD:EE:FF:00:11:22,999.1.1.1,bad-ip,20260601_120000",
            $"{net},not-a-mac,192.168.1.2,bad-mac,20260601_120000",
            $"bad-net,DD:EE:FF:00:11:23,192.168.1.3,bad-net,20260601_120000",
            $"{net},DD:EE:FF:00:11:24,192.168.1.4,future,20990101_000000",
            $"{net},DD:EE:FF:00:11:25,192.168.1.5,garbled,yesterday",
            $"{net},DD:EE:FF:00:11:26,192.168.1.6,ok,20260601_120000");

        Assert.Equal((1, 5), db.ImportCsv(csv));
        var loaded = Assert.Single(db.Load(net));
        Assert.Equal("192.168.1.6", loaded.Ip);
    }

    [Fact]
    public void LegacyDatabase_MigrationIsIdempotentThroughReadWriteAndExport()
    {
        var dbPath = DbPath();
        const string netMac = "AA:BB:CC:11:22:33";
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
        {
            conn.Open();
            conn.CreateCommand("""
                CREATE TABLE known_devices (
                  network_mac TEXT NOT NULL, mac TEXT NOT NULL, ip TEXT,
                  hostname TEXT, last_seen TEXT, PRIMARY KEY (network_mac, mac))
                """).ExecuteNonQuery();
            using var insert = conn.CreateCommand(
                "INSERT INTO known_devices VALUES ($n,$m,$i,$h,$t)");
            insert.Parameters.AddWithValue("$n", netMac);
            insert.Parameters.AddWithValue("$m", "DD:EE:FF:00:11:22");
            insert.Parameters.AddWithValue("$i", "192.168.1.19");
            insert.Parameters.AddWithValue("$h", "legacy-nas");
            insert.Parameters.AddWithValue("$t", "20260101_000000");
            insert.ExecuteNonQuery();
        }

        var migrated = new KnownDevicesDb(dbPath);
        var device = Assert.Single(migrated.Load(netMac));
        migrated.Save(netMac, new[] { device }, "20260601_000000");
        var reopened = new KnownDevicesDb(dbPath);
        var csvPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "known.csv");

        Assert.Equal("legacy-nas", Assert.Single(reopened.Load(netMac)).Hostname);
        Assert.Equal(1, reopened.ExportCsv(csvPath));
        Assert.Contains("legacy-nas", File.ReadAllText(csvPath));
    }

    [Fact]
    public void MergeFrom_CombinesDatabases_NewerWins()
    {
        string PathFor() => DbPath();
        var mainPath = PathFor();
        var otherPath = PathFor();
        const string netMac = "AA:BB:CC:11:22:33";

        var main = new KnownDevicesDb(mainPath);
        main.Save(netMac, new List<Device> { new("192.168.1.5") { Mac = "DD:EE:FF:00:11:22", Hostname = "old-name" } }, "20260101_000000");

        var other = new KnownDevicesDb(otherPath);
        other.Save(netMac, new List<Device>
        {
            new("192.168.1.9") { Mac = "DD:EE:FF:00:11:22", Hostname = "new-name" },   // same device, newer
            new("192.168.1.7") { Mac = "11:22:33:44:55:66", Hostname = "printer" },     // only in other
        }, "20260601_000000");

        int merged = main.MergeFrom(otherPath);

        Assert.Equal(2, merged);
        var loaded = main.Load(netMac);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, d => d.Hostname == "new-name" && d.Ip == "192.168.1.9");
        Assert.Contains(loaded, d => d.Hostname == "printer");
    }

    [Fact]
    public void MergeFrom_CancelledAfterProgress_RollsBackEveryInsertedRow()
    {
        var mainPath = DbPath();
        var otherPath = DbPath();
        const string netMac = "AA:BB:CC:11:22:33";
        var main = new KnownDevicesDb(mainPath);
        main.Save(netMac, new[] { new Device("192.168.1.1") { Mac = netMac } }, "20260901_000000");
        var other = new KnownDevicesDb(otherPath);
        other.Save(netMac, Enumerable.Range(1, 200).Select(i =>
            new Device($"10.0.0.{i}") { Mac = $"00:00:00:00:00:{i:X2}" }), "20260902_000000");

        using var cancellation = new CancellationTokenSource();
        var reports = new List<(int done, int total)>();
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            if (value.done >= 64) cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            main.MergeFrom(otherPath, progress, cancellation.Token));
        Assert.Equal((0, 200), reports[0]);
        Assert.Contains(reports, p => p.done >= 64);
        Assert.Single(main.Load(netMac));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void MergeFrom_FiftyThousandRows_ReportsProgressAndCompletes()
    {
        var mainPath = DbPath();
        var sourcePath = DbPath();
        const string netMac = "AA:BB:CC:11:22:33";
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sourcePath }.ConnectionString))
        {
            conn.Open();
            conn.CreateCommand("CREATE TABLE known_devices (network_mac TEXT, mac TEXT, ip TEXT, hostname TEXT, last_seen TEXT)")
                .ExecuteNonQuery();
            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand(
                "INSERT INTO known_devices VALUES ($n,$m,$i,$h,$t)");
            insert.Transaction = tx;
            insert.Parameters.AddWithValue("$n", netMac);
            var mac = insert.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Text);
            var ip = insert.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
            insert.Parameters.AddWithValue("$h", "host");
            insert.Parameters.AddWithValue("$t", "20260901_000000");
            for (int i = 0; i < 50_000; i++)
            {
                mac.Value = string.Join(":", Enumerable.Range(0, 6).Select(k => ((i >> (8 * (5 - k))) & 0xFF).ToString("X2")));
                ip.Value = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}";
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var main = new KnownDevicesDb(mainPath);
        var reports = new List<(int done, int total)>();
        int merged = main.MergeFrom(sourcePath, new InlineProgress(reports.Add));

        Assert.Equal(50_000, merged);
        Assert.Equal((0, 50_000), reports[0]);
        Assert.Contains(reports, p => p.done > 0 && p.done < 50_000);
        Assert.Equal((50_000, 50_000), reports[^1]);
        Assert.Equal(50_000, main.Load(netMac).Count);
    }

    private sealed class InlineProgress(Action<(int done, int total)> onReport) : IProgress<(int done, int total)>
    {
        public void Report((int done, int total) value) => onReport(value);
    }

    [Fact]
    public void GetNetworkMac_FindsGatewayMac()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        db.Save(netMac, new List<Device> { new("192.168.1.1") { Mac = netMac } }, "t");
        Assert.Equal(netMac, db.GetNetworkMac("192.168.1.1"));
    }

    // Latency history over days.
    [Fact]
    public void SaveLatencyHistory_ThenLoad_RoundTrips()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        const string mac = "DD:EE:FF:00:11:22";
        var online = new Device("192.168.1.5") { Mac = mac };
        online.RecordPing(new PingResult(true, 12.5, 64));
        online.RecordPing(new PingResult(true, 15.5, 64));   // avg 14, min 12.5, max 15.5
        var offline = new Device("192.168.1.6") { Mac = "11:22:33:44:55:66" };
        offline.RecordPing(new PingResult(false, null, null));   // no AvgMs - must not be saved

        db.SaveLatencyHistory(new[] { online, offline }, DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        var rows = db.LoadLatencyHistory(mac, DateTime.Now.AddDays(-1));
        var row = Assert.Single(rows);
        Assert.Equal(14, row.Avg);
        Assert.Equal(12.5, row.Min);
        Assert.Equal(15.5, row.Max);
        Assert.Empty(db.LoadLatencyHistory("11:22:33:44:55:66", DateTime.Now.AddDays(-1)));
    }

    [Fact]
    public void LoadLatencyHistory_ExcludesRowsBeforeSince()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        const string mac = "DD:EE:FF:00:11:22";
        var d = new Device("192.168.1.5") { Mac = mac };
        d.RecordPing(new PingResult(true, 10, 64));
        db.SaveLatencyHistory(new[] { d }, "20200101_000000");   // long past cutoff

        Assert.Empty(db.LoadLatencyHistory(mac, DateTime.Now.AddDays(-1)));
    }

    [Fact]
    public void PurgeOlderThan_AlsoPurgesLatencyHistory()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        const string mac = "DD:EE:FF:00:11:22";
        var d = new Device("192.168.1.5") { Mac = mac };
        d.RecordPing(new PingResult(true, 10, 64));
        db.SaveLatencyHistory(new[] { d }, "20200101_000000");

        db.PurgeOlderThan(30);

        Assert.Empty(db.LoadLatencyHistory(mac, DateTime.Now.AddYears(-30)));
    }

    private static List<Device> StatsDevices(int count)
    {
        var devices = new List<Device>();
        for (int i = 0; i < count; i++)
        {
            var d = new Device($"10.0.0.{i + 1}") { Mac = $"DD:EE:FF:00:00:{i + 1:X2}" };
            d.RecordPing(new PingResult(true, 5 + i, 64));
            devices.Add(d);
        }
        return devices;
    }

    [Fact]
    public void ScanArchiveStatistics_SkipCancelledRunsUnlessAskedFor()
    {
        const string network = "AA:BB:CC:11:22:33";
        var at = new DateTimeOffset(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var db = new KnownDevicesDb(DbPath());
        db.SaveScanArchive(at, at.AddMinutes(1), network, new[] { "10.0.0.0/24" }, "{}", StatsDevices(3), Array.Empty<ConflictFinding>());
        db.SaveScanArchive(at.AddHours(1), at.AddHours(1).AddMinutes(1), network, new[] { "10.0.0.0/24" }, "{}",
            StatsDevices(5), Array.Empty<ConflictFinding>(), cancelled: true);

        var normal = db.GetScanArchiveStatistics();
        var all = db.GetScanArchiveStatistics(includeCancelled: true);

        Assert.Equal(1, normal.RunCount);
        Assert.Equal(3, normal.Devices.Count);
        Assert.Equal(2, all.RunCount);
        Assert.Equal(5, all.Devices.Count);
    }

    [Fact]
    public void ScanArchive_ThreeRuns_RoundTripStatisticsAndCompactionPreserveSamples()
    {
        var db = new KnownDevicesDb(DbPath());
        var baseline = new DateTimeOffset(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        const string network = "AA:BB:CC:11:22:33";
        const string deviceMac = "DD:EE:FF:00:11:22";
        var first = SaveArchive(db, baseline.AddDays(-9).AddHours(-3), network, deviceMac, true, 10);
        SaveArchive(db, baseline.AddDays(-9).AddHours(-2), network, deviceMac, false);
        SaveArchive(db, baseline.AddDays(-8), network, deviceMac, true, 30);

        var before = db.GetScanArchiveStatistics();
        var device = Assert.IsType<ArchivedDeviceStatistics>(db.GetArchivedDeviceStatistics(deviceMac));
        Assert.Equal(3, before.RunCount);
        Assert.Equal(3, before.SampleCount);
        Assert.Equal(3, device.ObservedCount);
        Assert.Equal(2, device.OnlineCount);
        Assert.Equal(66.667, device.AvailabilityPercent, 3);
        Assert.Equal(20, device.AvgMs);
        Assert.NotNull(db.LoadScanArchive(first));
        var dayThrough = baseline.AddDays(-8).AddTicks(-1);
        var firstDayBefore = Assert.IsType<ArchivedDeviceStatistics>(
            db.GetArchivedDeviceStatistics(deviceMac, through: dayThrough));

        Assert.Equal(1, db.CompactScanArchiveOlderThan(1, baseline));
        var after = db.GetScanArchiveStatistics();
        var compacted = Assert.IsType<ArchivedDeviceStatistics>(db.GetArchivedDeviceStatistics(deviceMac));
        Assert.Equal(2, after.RunCount);
        Assert.Equal(3, after.SampleCount);
        Assert.Equal(device.ObservedCount, compacted.ObservedCount);
        Assert.Equal(device.OnlineCount, compacted.OnlineCount);
        Assert.Equal(device.AvgMs, compacted.AvgMs);
        Assert.Equal(device.FirstSeenAt, compacted.FirstSeenAt);
        Assert.Equal(device.LastSeenAt, compacted.LastSeenAt);
        var firstDayAfter = Assert.IsType<ArchivedDeviceStatistics>(
            db.GetArchivedDeviceStatistics(deviceMac, through: dayThrough));
        Assert.Equal(firstDayBefore.FirstSeenAt, firstDayAfter.FirstSeenAt);
        Assert.Equal(firstDayBefore.LastSeenAt, firstDayAfter.LastSeenAt);
    }

    [Fact]
    public void ScanArchive_StatisticsStayWholeWhileACompactionRunsConcurrently()
    {
        var db = new KnownDevicesDb(DbPath());
        var baseline = new DateTimeOffset(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        const string network = "AA:BB:CC:11:22:33";
        const string mac = "DD:EE:FF:00:11:22";
        SaveArchive(db, baseline.AddDays(-9).AddHours(-3), network, mac, true, 10);
        SaveArchive(db, baseline.AddDays(-9).AddHours(-2), network, mac, true, 20);
        SaveArchive(db, baseline.AddDays(-9).AddHours(-1), network, mac, true, 30);
        var before = db.GetScanArchiveStatistics();
        var compacted = 0;
        db.ArchiveStatisticsRunHook = () =>
        {
            if (compacted == 0) compacted = db.CompactScanArchiveOlderThan(1, baseline);
        };

        var during = db.GetScanArchiveStatistics();
        db.ArchiveStatisticsRunHook = null;

        Assert.Equal(2, compacted);
        Assert.Equal(3, before.RunCount);
        Assert.Equal(before.RunCount, during.RunCount);
        Assert.Equal(before.SampleCount, during.SampleCount);
        Assert.Equal(before.Devices.Single().ObservedCount, during.Devices.Single().ObservedCount);
    }

    [Fact]
    public void ScanArchive_ImportingAPreCompactionBackup_DoesNotDoubleCompactedRuns()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        var baseline = new DateTimeOffset(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        const string network = "AA:BB:CC:11:22:33";
        const string mac = "DD:EE:FF:00:11:22";
        SaveArchive(db, baseline.AddDays(-9).AddHours(-3), network, mac, true, 10);
        SaveArchive(db, baseline.AddDays(-9).AddHours(-2), network, mac, false);
        SaveArchive(db, baseline.AddDays(-9).AddHours(-1), network, mac, true, 30);
        SaveArchive(db, baseline.AddDays(-8), network, mac, true, 20);
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "before-compaction.json");
        db.ExportScanArchive(backup);
        Assert.Equal(2, db.CompactScanArchiveOlderThan(1, baseline));
        var before = db.GetScanArchiveStatistics();

        var result = db.ImportScanArchive(backup);

        Assert.Equal(new ArchiveImportResult(0, 4), result);
        var after = db.GetScanArchiveStatistics();
        Assert.Equal(before.RunCount, after.RunCount);
        Assert.Equal(before.SampleCount, after.SampleCount);
    }

    [Fact]
    public void ScanArchive_EncryptedJsonBackupImportAndRetentionAreSafe()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        var now = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, now.AddDays(-3), "network", "DD:EE:FF:00:11:22", true, 12);
        SaveArchive(db, now.AddDays(-2), "network", "DD:EE:FF:00:11:22", false);
        var directory = Path.GetDirectoryName(path)!;
        var export = Path.Combine(directory, "archive.json");

        Assert.Equal(2, db.ExportScanArchive(export));
        var raw = File.ReadAllText(export);
        Assert.DoesNotContain("192.0.2.8", raw);
        Assert.Equal(2, db.DeleteScanArchiveRange(now.AddDays(-4), now));
        Assert.Empty(db.ListScanArchive());
        Assert.Equal(new ArchiveImportResult(2, 0), db.ImportScanArchive(export));
        Assert.Equal(2, db.ListScanArchive().Count);
        Assert.Equal(0, db.PurgeScanArchiveOlderThan(0, now));

        var backups = Path.Combine(directory, "backups");
        var oldBackup = db.CreateAutomaticScanArchiveBackup(backups, 30, now.AddDays(-31));
        var currentBackup = db.CreateAutomaticScanArchiveBackup(backups, 30, now);
        Assert.NotNull(oldBackup);
        Assert.NotNull(currentBackup);
        Assert.False(File.Exists(oldBackup!));
        Assert.True(File.Exists(currentBackup!));
        Assert.Equal(0, db.PurgeAutomaticScanArchiveBackups(backups, 0, now));
    }

    [Fact]
    public void ScanArchive_ExportReplacementKeepsImportablePreviousGeneration()
    {
        var db = new KnownDevicesDb(DbPath());
        var export = Path.Combine(Directory.CreateTempSubdirectory().FullName, "archive.json");
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, at, "network", "DD:EE:FF:00:11:22", true, 12);
        Assert.Equal(1, db.ExportScanArchive(export));
        SaveArchive(db, at.AddDays(1), "network", "DD:EE:FF:00:11:22", false);

        Assert.Equal(2, db.ExportScanArchive(export));
        Assert.False(File.Exists(export + ".tmp"));
        Assert.True(File.Exists(export + ".bak"));
        db.DeleteScanArchiveRange(at.AddDays(-1), at.AddDays(2));
        Assert.Equal(new ArchiveImportResult(1, 0), db.ImportScanArchive(export + ".bak"));
        Assert.Equal(new ArchiveImportResult(1, 1), db.ImportScanArchive(export));
    }

    [Fact]
    public void ScanArchive_ExportReplacementFailureKeepsPreviousFile()
    {
        var db = new KnownDevicesDb(DbPath());
        var export = Path.Combine(Directory.CreateTempSubdirectory().FullName, "archive.json");
        SaveArchive(db, new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero),
            "network", "DD:EE:FF:00:11:22", true, 12);
        db.ExportScanArchive(export);
        SaveArchive(db, new DateTimeOffset(2030, 2, 11, 12, 0, 0, TimeSpan.Zero),
            "network", "DD:EE:FF:00:11:22", false);
        db.ExportScanArchive(export);
        var original = File.ReadAllBytes(export);
        var backup = File.ReadAllBytes(export + ".bak");
        using (var locked = new FileStream(export, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => db.ExportScanArchive(export));

        Assert.Equal(original, File.ReadAllBytes(export));
        Assert.Equal(backup, File.ReadAllBytes(export + ".bak"));
        Assert.False(File.Exists(export + ".tmp"));
        Assert.Equal(2, db.ImportScanArchive(export).Skipped);
    }

    [Fact]
    public void ScanArchive_BackupRetentionDeletesExpiredPreviousGenerations()
    {
        var db = new KnownDevicesDb(DbPath());
        var backups = Directory.CreateTempSubdirectory().FullName;
        var old = new DateTimeOffset(2030, 2, 1, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, old, "network", "DD:EE:FF:00:11:22", true, 12);
        var backup = db.CreateAutomaticScanArchiveBackup(backups, 30, old)!;
        File.Copy(backup, backup + ".bak");
        Assert.True(File.Exists(backup + ".bak"));

        Assert.Equal(2, db.PurgeAutomaticScanArchiveBackups(backups, 30, old.AddDays(31)));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(backup + ".bak"));
    }

    [Fact]
    public void ScanArchive_StatisticsShowLatestDeviceIdentity()
    {
        var db = new KnownDevicesDb(DbPath());
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        const string mac = "DD:EE:FF:00:11:22";
        db.SaveScanArchive(at, at.AddMinutes(1), "network", [], "{}",
            [new Device("192.0.2.8") { Mac = mac, Hostname = "old-host" }], []);
        db.SaveScanArchive(at.AddDays(1), at.AddDays(1).AddMinutes(1), "network", [], "{}",
            [new Device("192.0.2.9") { Mac = mac, Hostname = "new-host" }], []);

        var stats = Assert.IsType<ArchivedDeviceStatistics>(db.GetArchivedDeviceStatistics(mac));
        Assert.Equal("192.0.2.9", stats.Ip);
        Assert.Equal("new-host", stats.Hostname);
    }


    [Fact]
    public void ScanArchive_MaintenanceFollowsArchiveRetentionNotDeviceRetention()
    {
        var db = new KnownDevicesDb(DbPath());
        var now = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, now.AddDays(-100), "network", "DD:EE:FF:00:11:22", true, 5);
        SaveArchive(db, now.AddDays(-1), "network", "DD:EE:FF:00:11:22", true, 5);

        db.ApplyArchiveMaintenance(new ScanConfig { DbRetentionDays = 30, ArchiveRetentionDays = 0, ArchiveCompactionDays = 0 }, now);
        Assert.Equal(2, db.ListScanArchive().Count);

        db.ApplyArchiveMaintenance(new ScanConfig { DbRetentionDays = 0, ArchiveRetentionDays = 90, ArchiveCompactionDays = 0 }, now);
        Assert.Single(db.ListScanArchive());
    }

    [Fact]
    public void ScanArchive_ImportingTheSameFileTwiceKeepsTheRunCount()
    {
        var db = new KnownDevicesDb(DbPath());
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, at, "network", "DD:EE:FF:00:11:22", true, 12);
        SaveArchive(db, at.AddDays(1), "network", "DD:EE:FF:00:11:22", false);
        var export = Path.Combine(Directory.CreateTempSubdirectory().FullName, "archive.json");
        db.ExportScanArchive(export);
        db.DeleteScanArchiveRange(at.AddDays(-1), at.AddDays(3));

        Assert.Equal(new ArchiveImportResult(2, 0), db.ImportScanArchive(export));
        Assert.Equal(new ArchiveImportResult(0, 2), db.ImportScanArchive(export));
        Assert.Equal(2, db.ListScanArchive().Count);
    }

    [Fact]
    public void ScanArchive_CancelledRunIsListedButExcludedFromStatistics()
    {
        var db = new KnownDevicesDb(DbPath());
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        const string mac = "DD:EE:FF:00:11:22";
        var online = new Device("192.0.2.8") { Mac = mac };
        online.RecordPing(new PingResult(true, 3.0, 64));
        var offline = new Device("192.0.2.8") { Mac = mac };
        offline.RecordPing(new PingResult(false, null, null));
        db.SaveScanArchive(at, at.AddMinutes(1), "network", [], "{}", [online], []);
        db.SaveScanArchive(at.AddDays(1), at.AddDays(1).AddMinutes(1), "network", [], "{}", [offline], [], cancelled: true);

        var runs = db.ListScanArchive();
        Assert.Equal(2, runs.Count);
        Assert.Single(runs, r => r.Cancelled);
        var stats = db.GetScanArchiveStatistics();
        Assert.Equal(1, stats.RunCount);
        Assert.Equal(100.0, Assert.Single(stats.Devices).AvailabilityPercent);
    }

    [Fact]
    public void Open_CreatesTheGatewayLookupIndex()
    {
        var path = DbPath();
        _ = new KnownDevicesDb(path);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_known_ip'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Open_AfterTheFileWasDeleted_BuildsTheSchemaAgain()
    {
        var path = DbPath();
        var first = new KnownDevicesDb(path);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, "scanner.db*")) File.Delete(f);

        var second = new KnownDevicesDb(path);
        second.Save("AA:BB:CC:11:22:33", new[] { new Device("192.0.2.7") { Mac = "DD:EE:FF:00:11:22" } }, "20260919_120000");

        Assert.Single(second.Load("AA:BB:CC:11:22:33"));
    }

    [Fact]
    public void Open_AfterTheFileWasReplacedInPlace_BuildsTheSchemaAgain()
    {
        var path = DbPath();
        var first = new KnownDevicesDb(path);
        var stale = Path.Combine(Path.GetDirectoryName(path)!, "stale.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stale};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE unrelated (x INTEGER)";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, "scanner.db-*")) File.Delete(f);
        File.Copy(stale, path, overwrite: true);

        var second = new KnownDevicesDb(path);
        second.Save("AA:BB:CC:11:22:33", new[] { new Device("192.0.2.7") { Mac = "DD:EE:FF:00:11:22" } }, "20260919_120000");

        Assert.Single(second.Load("AA:BB:CC:11:22:33"));
    }

    [Fact]
    public void ScanArchive_OldDatabaseWithoutCancelledColumnIsUpgraded()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, at, "network", "DD:EE:FF:00:11:22", true, 12);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var drop = conn.CreateCommand();
            drop.CommandText = "ALTER TABLE scans DROP COLUMN cancelled";
            drop.ExecuteNonQuery();
        }
        KnownDevicesDb.ForgetInitializedFiles();

        var reopened = new KnownDevicesDb(path);
        var run = Assert.Single(reopened.ListScanArchive());
        Assert.False(run.Cancelled);
    }

    [Fact]
    public void ScanArchive_SecondAutomaticBackupOnTheSameDayWritesNothing()
    {
        var db = new KnownDevicesDb(DbPath());
        var backups = Directory.CreateTempSubdirectory().FullName;
        var now = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, now.AddDays(-1), "network", "DD:EE:FF:00:11:22", true, 12);

        var first = db.CreateAutomaticScanArchiveBackup(backups, 30, now);
        Assert.NotNull(first);
        var bytes = File.ReadAllBytes(first!);
        Assert.Null(db.CreateAutomaticScanArchiveBackup(backups, 30, now.AddHours(3)));
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.False(File.Exists(first + ".bak"));

        var manual = db.CreateManualScanArchiveBackup(backups, now.AddHours(3));
        Assert.EndsWith("scan-archive-20300210-150000.json", manual);
        Assert.True(File.Exists(manual!));
        Assert.Equal(bytes, File.ReadAllBytes(first));
    }

    [Fact]
    public void ScanArchive_RowWithMalformedTimestampIsSkipped()
    {
        var path = DbPath();
        var db = new KnownDevicesDb(path);
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        SaveArchive(db, at, "network", "DD:EE:FF:00:11:22", true, 12);
        var badId = SaveArchive(db, at.AddDays(1), "network", "DD:EE:FF:00:11:22", true, 12);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE scans SET started_at='not a time' WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", badId);
            cmd.ExecuteNonQuery();
        }

        Assert.Single(db.ListScanArchive());
        Assert.Null(db.LoadScanArchive(badId));
        Assert.Equal(1, db.GetScanArchiveStatistics().RunCount);
    }


    private static int RowCount(string path, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string[] SixTables => new[]
    {
        "known_devices_v2", "device_ports", "latency_history_v2", "scans", "scan_devices", "scan_findings"
    };

    private static KnownDevicesDb FilledDb(string path)
    {
        var db = new KnownDevicesDb(path);
        var at = new DateTimeOffset(2030, 2, 10, 12, 0, 0, TimeSpan.Zero);
        var device = new Device("192.0.2.8") { Mac = "DD:EE:FF:00:11:22", Hostname = "host" };
        device.RecordPing(new PingResult(true, 4.0, 64));
        db.Save("AA:BB:CC:11:22:33", new[] { device }, "20300210_120000");
        db.SaveLatencyHistory(new[] { device }, "20300210_120000");
        db.SavePortScan("AA:BB:CC:11:22:33", "DD:EE:FF:00:11:22", "192.0.2.8",
            new[] { new NetService("http", 80, NetProtocol.Tcp) }, at, false);
        db.SaveScanArchive(at, at.AddMinutes(1), "network", ["192.0.2.0/24"], "{}", new[] { device },
            new[] { new ConflictFinding("192.0.2.8", "DD:EE:FF:00:11:22", ConflictKind.MacChanged, "detail") });
        return db;
    }

    [Fact]
    public void Clear_WithArchive_EmptiesAllSixTables()
    {
        var path = DbPath();
        var db = FilledDb(path);
        foreach (var table in SixTables) Assert.True(RowCount(path, table) > 0, table);

        Assert.Equal(1, db.Clear(includeArchive: true));
        foreach (var table in SixTables) Assert.Equal(0, RowCount(path, table));
    }

    [Fact]
    public void Clear_WithoutArchive_KeepsTheArchiveTables()
    {
        var path = DbPath();
        var db = FilledDb(path);

        Assert.Equal(1, db.Clear(includeArchive: false));
        foreach (var table in new[] { "known_devices_v2", "device_ports", "latency_history_v2" })
            Assert.Equal(0, RowCount(path, table));
        foreach (var table in new[] { "scans", "scan_devices", "scan_findings" })
            Assert.True(RowCount(path, table) > 0, table);
    }

    [Fact]
    public void DeleteArchiveBackups_SkipsALockedFileInsteadOfFailing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var locked = Path.Combine(dir, "scan-archive-20300210.json");
        File.WriteAllText(locked, "x");
        File.WriteAllText(Path.Combine(dir, "scan-archive-20300211.json"), "x");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(1, KnownDevicesDb.DeleteArchiveBackups(dir));
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void DeleteArchiveBackups_RemovesOnlyBackupFiles()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "scan-archive-20300210.json"), "x");
        File.WriteAllText(Path.Combine(dir, "scan-archive-20300210.json.bak"), "x");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");

        Assert.Equal(2, KnownDevicesDb.DeleteArchiveBackups(dir));
        Assert.Equal(new[] { "notes.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        Assert.Equal(0, KnownDevicesDb.DeleteArchiveBackups(Path.Combine(dir, "missing")));
    }


    private static long SaveArchive(KnownDevicesDb db, DateTimeOffset started, string network,
        string mac, bool online, params double[] latencies)
    {
        var device = new Device("192.0.2.8") { Mac = mac, Hostname = "archive-device" };
        if (online)
        {
            foreach (var latency in latencies) device.RecordPing(new PingResult(true, latency, 64));
        }
        else device.RecordPing(new PingResult(false, null, null));
        return db.SaveScanArchive(started, started.AddMinutes(1), network, new[] { "192.0.2.0/24" },
            "{}", new[] { device }, Array.Empty<ConflictFinding>());
    }

    private static void InsertPlainRow(string dbPath, params (string Net, string Mac, string Ip, string Host, string Seen)[] rows)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        conn.Open();
        conn.CreateCommand("CREATE TABLE known_devices (network_mac TEXT, mac TEXT, ip TEXT, hostname TEXT, last_seen TEXT)")
            .ExecuteNonQuery();
        foreach (var r in rows)
        {
            using var insert = conn.CreateCommand("INSERT INTO known_devices VALUES ($n,$m,$i,$h,$t)");
            insert.Parameters.AddWithValue("$n", r.Net);
            insert.Parameters.AddWithValue("$m", r.Mac);
            insert.Parameters.AddWithValue("$i", r.Ip);
            insert.Parameters.AddWithValue("$h", r.Host);
            insert.Parameters.AddWithValue("$t", r.Seen);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void MergeFrom_SkipsRowsThatFailTheImportValidation_AndNormalizesDashedMacs()
    {
        const string net = "AA:BB:CC:11:22:33";
        var sourcePath = DbPath();
        InsertPlainRow(sourcePath,
            (net, "DD:EE:FF:00:11:22", "192.168.1.9", "good", "20260601_120000"),
            (net, "dd-ee-ff-00-11-23", "192.168.1.10", "dashed", "20260601_120000"),
            (net, "=HYPERLINK(\"x\")", "192.168.1.11", "bad-mac", "20260601_120000"),
            (net, "DD:EE:FF:00:11:24", "=cmd|' /c calc'!A1", "bad-ip", "20260601_120000"),
            (net, "DD:EE:FF:00:11:25", "192.168.1.12", "bad-date", "yesterday"));
        var main = new KnownDevicesDb(DbPath());

        Assert.Equal(2, main.MergeFrom(sourcePath));
        var loaded = main.Load(net);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, d => d.Mac == "DD:EE:FF:00:11:23" && d.Hostname == "dashed");
    }

    [Fact]
    public void ExportCsv_DefusesAndQuotesEveryField_ForRowsThatBypassedValidation()
    {
        var dbPath = DbPath();
        var db = new KnownDevicesDb(dbPath);
        db.Save("=1+1", new List<Device> { new("@SUM(1,2)") { Mac = "+cmd,x", Hostname = "h" } }, "-2+3");
        var csvPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");

        Assert.Equal(1, db.ExportCsv(csvPath));
        var line = File.ReadAllLines(csvPath)[1];
        Assert.StartsWith("'=1+1,", line);
        Assert.Contains(",\"'+CMD,X\",", line);
        Assert.Contains(",\"'@SUM(1,2)\",", line);
        Assert.EndsWith(",h,'-2+3", line);
    }

    [Fact]
    public void ImportCsv_DashedMac_IsStoredInColonForm_AndUpdatesTheExistingDevice()
    {
        const string net = "AA:BB:CC:11:22:33";
        var db = new KnownDevicesDb(DbPath());
        db.Save(net, new List<Device> { new("192.168.1.9") { Mac = "DD:EE:FF:00:11:22", Hostname = "kept" } },
            "20260601_120000");

        var older = WriteCsv($"aa-bb-cc-11-22-33,dd-ee-ff-00-11-22,192.168.1.50,stale,20260101_000000");
        Assert.Equal((0, 1), db.ImportCsv(older));
        var newer = WriteCsv($"aa-bb-cc-11-22-33,dd-ee-ff-00-11-22,192.168.1.50,fresh,20260701_000000");
        Assert.Equal((1, 0), db.ImportCsv(newer));

        var loaded = Assert.Single(db.Load(net));
        Assert.Equal("DD:EE:FF:00:11:22", loaded.Mac);
        Assert.Equal("fresh", loaded.Hostname);
    }
}
