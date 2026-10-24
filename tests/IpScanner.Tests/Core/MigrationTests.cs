using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class MigrationTests
{
    // ── conf migration ──────────────────────────────────────────────────────

    [Fact]
    public void Conf_UpToDate_NotRewritten()
    {
        var path = TempConf();
        ConfigManager.Save(path, new ScanConfig());
        var before = File.GetLastWriteTimeUtc(path);
        System.Threading.Thread.Sleep(20);

        ConfigManager.MigrateIfNeeded(path);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Conf_MissingKey_RewrittenWithDefault()
    {
        var path = TempConf();
        // Write a minimal conf that omits most keys.
        File.WriteAllText(path, "ping_count = 50\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);

        var text = File.ReadAllText(path);
        Assert.Contains("ping_count = 50", text);       // existing value kept
        Assert.Contains("graphs_enabled", text);        // new key added
        Assert.Contains("graph_max_seconds", text);     // new key added
    }

    [Fact]
    public void Conf_MigratedValues_LoadCorrectly()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 77\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var cfg = ConfigManager.Load(path);

        Assert.Equal(77, cfg.PingCount);    // original value preserved
        Assert.True(cfg.GraphsEnabled);     // default applied for missing key
    }

    // A conf written before table_sort/table_widths existed still loads
    // (Abnahme: "a config written by an older version still loads").
    [Fact]
    public void Conf_MissingTableSortAndWidths_LoadsWithEmptyDefaults()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 33\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var cfg = ConfigManager.Load(path);

        Assert.Equal("", cfg.TableSort);
        Assert.Equal("", cfg.TableWidths);
    }

    // An existing install's conf predates this key and must default to
    // "" (automatic adapter), never a stale or invalid forced address.
    [Fact]
    public void Conf_MissingNetworkInterfaceIp_DefaultsToEmpty()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 33\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var cfg = ConfigManager.Load(path);

        Assert.Equal("", cfg.NetworkInterfaceIp);
    }

    [Fact]
    public void Conf_MissingSidebarExportsExpanded_MigratesToFalse()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 5\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var text = File.ReadAllText(path);
        var cfg = ConfigManager.Load(path);

        Assert.Contains("sidebar_exports_expanded", text);
        Assert.False(cfg.SidebarExportsExpanded);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(30, 30)]
    public void Conf_WithoutArchiveRetention_FollowsTheHistoryRetention(int history, int expectedArchive)
    {
        var path = TempConf();
        File.WriteAllText(path, $"db_retention_days = {history}\n");

        Assert.Equal(expectedArchive, ConfigManager.Load(path).ArchiveRetentionDays);

        ConfigManager.MigrateIfNeeded(path);
        Assert.Equal(expectedArchive, ConfigManager.Load(path).ArchiveRetentionDays);
        Assert.Contains($"archive_retention_days = {expectedArchive}", File.ReadAllText(path));
    }

    [Fact]
    public void Conf_WithArchiveRetention_KeepsItsOwnValue()
    {
        var path = TempConf();
        File.WriteAllText(path, "db_retention_days = 0\narchive_retention_days = 90\n");

        Assert.Equal(90, ConfigManager.Load(path).ArchiveRetentionDays);
    }

    [Fact]
    public void Conf_MissingSidebarSectionKeys_AreAddedOnMigrate()
    {
        var path = TempConf();
        var cfg = new ScanConfig { SidebarNetworksExpanded = false };
        ConfigManager.Save(path, cfg);
        var withoutKeys = File.ReadAllLines(path)
            .Where(l => !l.StartsWith("sidebar_networks_expanded") && !l.StartsWith("sidebar_pinned_expanded")
                && !l.StartsWith("sidebar_internet_expanded"));
        File.WriteAllLines(path, withoutKeys);

        ConfigManager.MigrateIfNeeded(path);
        var text = File.ReadAllText(path);

        Assert.Contains("sidebar_networks_expanded = ", text);
        Assert.Contains("sidebar_pinned_expanded = ", text);
        Assert.Contains("sidebar_internet_expanded = ", text);
    }

    [Fact]
    public void Conf_MissingHeatMs_MigratesToDefaults()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 5\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var text = File.ReadAllText(path);
        var cfg = ConfigManager.Load(path);

        Assert.Contains("heat_ms_1", text);
        Assert.Equal(ScanConfig.Bounds.HeatMs1Default, cfg.HeatMs1);
        Assert.Equal(ScanConfig.Bounds.HeatMs4Default, cfg.HeatMs4);
    }

    [Fact]
    public void Conf_HeatMsOutOfOrder_CorrectedAscendingOnLoad()
    {
        var path = TempConf();
        File.WriteAllText(path,
            "heat_ms_1 = 400\nheat_ms_2 = 200\nheat_ms_3 = 100\nheat_ms_4 = 50\n",
            System.Text.Encoding.UTF8);

        var cfg = ConfigManager.Load(path);

        Assert.Equal(50, cfg.HeatMs1);
        Assert.Equal(100, cfg.HeatMs2);
        Assert.Equal(200, cfg.HeatMs3);
        Assert.Equal(400, cfg.HeatMs4);
    }

    [Fact]
    public void Conf_MissingSettingsPage_MigratesToScan()
    {
        var path = TempConf();
        File.WriteAllText(path, "ping_count = 5\n", System.Text.Encoding.UTF8);

        ConfigManager.MigrateIfNeeded(path);
        var text = File.ReadAllText(path);
        var cfg = ConfigManager.Load(path);

        Assert.Contains("settings_page", text);
        Assert.Equal("Scan", cfg.SettingsPage);
    }

    [Fact]
    public void Conf_UnknownSettingsPage_FallsBackToScan()
    {
        var path = TempConf();
        File.WriteAllText(path, "settings_page = Bogus\n", System.Text.Encoding.UTF8);

        var cfg = ConfigManager.Load(path);

        Assert.Equal("Scan", cfg.SettingsPage);
    }

    // ── DB migration ────────────────────────────────────────────────────────

    [Fact]
    public void Db_LegacyPlainTable_MigratedToSealedV2AndDropped()
    {
        // A pre-2.11 database has a plain "known_devices" table. Opening it
        // must migrate every row into the sealed "known_devices_v2" table and drop the
        // old one, not just add a missing column.
        // Own subdirectory: scanner.key sits next to the .db, sharing the flat %TEMP%
        // root with another test's database would race both onto the same key file.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "scanner.db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.CreateCommand(
                    "CREATE TABLE known_devices (network_mac TEXT NOT NULL, mac TEXT NOT NULL," +
                    " ip TEXT, hostname TEXT, last_seen TEXT, PRIMARY KEY (network_mac, mac))")
                    .ExecuteNonQuery();
                conn.CreateCommand(
                    "INSERT INTO known_devices VALUES ('AA:BB:CC:11:22:33','AA:BB:CC:11:22:33'," +
                    "'192.168.1.1','router','20260601_120000')").ExecuteNonQuery();
            }

            var db = new KnownDevicesDb(path);

            using (var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                verify.Open();
                using var pragma = verify.CreateCommand(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='known_devices'");
                Assert.Null(pragma.ExecuteScalar());   // old table dropped, not patched
            }
            var loaded = Assert.Single(db.Load("AA:BB:CC:11:22:33"));
            Assert.Equal("192.168.1.1", loaded.Ip);
            Assert.Equal("router", loaded.Hostname);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Db_CheckedInLegacyFile_MigratesItsRows()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "scanner.db");
        const string network = "02:00:00:00:00:01";
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-known-devices.sqlite"), path);

            var db = new KnownDevicesDb(path);

            var loaded = db.Load(network).OrderBy(d => d.Ip, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "192.0.2.10", "192.0.2.11", "192.0.2.12" }, loaded.Select(d => d.Ip));
            Assert.Equal(new[] { "02:00:00:00:00:10", "02:00:00:00:00:11", "02:00:00:00:00:12" }, loaded.Select(d => d.Mac));
            Assert.Equal("printer", loaded[0].Hostname);
            Assert.Equal("nas", loaded[1].Hostname);
            Assert.Equal(Device.Unknown, loaded[2].Hostname);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(new long[] { 4, 4, 4 }, ReadFamilies(path, "known_devices_v2"));

            // The migrated copy opens again without changing anything.
            Assert.Equal(3, new KnownDevicesDb(path).Load(network).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Db_AllColumnsPresent_NoError()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        try
        {
            _ = new KnownDevicesDb(path);
            _ = new KnownDevicesDb(path);   // second open = no-op migration
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static List<long> ReadFamilies(string path, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT family FROM {table} ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var result = new List<long>();
        while (r.Read()) result.Add(r.GetInt64(0));
        return result;
    }

    private static void Exec(string path, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Db_SchemaWithoutFamilyColumn_GainsItAndKeepsRowsAsIpv4()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "scanner.db");
        const string network = "AA:BB:CC:11:22:33";
        const string mac = "DD:EE:FF:00:11:22";
        try
        {
            var first = new KnownDevicesDb(path);
            first.Save(network, new[] { new Device("192.0.2.7") { Mac = mac, Hostname = "old-host" } }, "20260601_120000");
            first.SavePortScan(network, mac, "192.0.2.7",
                new[] { new NetService("HTTPS", 443, NetProtocol.Tcp, "b") }, DateTimeOffset.UtcNow, false);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Put the file back into the shape the previous release wrote: no family columns, marker 1.
            Exec(path, "ALTER TABLE known_devices_v2 DROP COLUMN family");
            Exec(path, "ALTER TABLE device_ports DROP COLUMN family");
            Exec(path, "PRAGMA user_version=1");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            KnownDevicesDb.ForgetInitializedFiles();

            var db = new KnownDevicesDb(path);

            var loaded = Assert.Single(db.Load(network));
            Assert.Equal("192.0.2.7", loaded.Ip);
            Assert.Equal("old-host", loaded.Hostname);
            Assert.NotNull(db.LoadPortScan(network, mac, "192.0.2.7"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(new long[] { 4 }, ReadFamilies(path, "known_devices_v2"));
            Assert.Equal(new long[] { 4 }, ReadFamilies(path, "device_ports"));

            // A second open of the migrated file is a no-op, and a new IPv6 row is stored as family 6.
            db = new KnownDevicesDb(path);
            db.Save(network, new[] { new Device("2001:DB8:0:0:0:0:0:5") { Mac = "DD:EE:FF:00:11:33" } }, "20260601_120100");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(new long[] { 4, 6 }, ReadFamilies(path, "known_devices_v2"));
            Assert.Contains(db.Load(network), d => d.Ip == "2001:db8::5");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Db_Ipv6Rows_RoundTripThroughCsvAndMerge()
    {
        var dirA = Directory.CreateTempSubdirectory().FullName;
        var dirB = Directory.CreateTempSubdirectory().FullName;
        const string network = "AA:BB:CC:11:22:33";
        try
        {
            var a = new KnownDevicesDb(Path.Combine(dirA, "scanner.db"));
            a.Save(network, new[]
            {
                new Device("fe80::1%12") { Mac = "DD:EE:FF:00:11:22", Hostname = "gw" },
                new Device("192.0.2.9") { Mac = "DD:EE:FF:00:11:23" },
            }, "20260601_120000");
            var csv = Path.Combine(dirA, "export.csv");
            Assert.Equal(2, a.ExportCsv(csv));

            var b = new KnownDevicesDb(Path.Combine(dirB, "scanner.db"));
            var (imported, skipped) = b.ImportCsv(csv);
            Assert.Equal((2, 0), (imported, skipped));
            Assert.Contains(b.Load(network), d => d.Ip == "fe80::1");

            var c = new KnownDevicesDb(Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(2, c.MergeFrom(Path.Combine(dirA, "scanner.db")));
            Assert.Contains(c.Load(network), d => d.Ip == "fe80::1");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static string TempConf()
        => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
}
