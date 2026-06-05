using System.Collections.Generic;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
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

    // ── DB migration ────────────────────────────────────────────────────────

    [Fact]
    public void Db_MissingColumn_AddedOnOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.CreateCommand(
                    "CREATE TABLE known_devices (network_mac TEXT NOT NULL, mac TEXT NOT NULL," +
                    " ip TEXT, hostname TEXT, PRIMARY KEY (network_mac, mac))")
                    .ExecuteNonQuery();
            }

            _ = new KnownDevicesDb(path);

            List<string> cols;
            using (var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                verify.Open();
                using var pragma = verify.CreateCommand("PRAGMA table_info(known_devices)");
                using var r = pragma.ExecuteReader();
                cols = new List<string>();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            Assert.Contains("last_seen", cols);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Db_AllColumnsPresent_NoError()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
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

    private static string TempConf()
        => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
}
