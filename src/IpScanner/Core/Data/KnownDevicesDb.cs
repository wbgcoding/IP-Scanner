using IpScanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

/// <summary>SQLite store of devices seen per network (keyed by gateway MAC).</summary>
public sealed class KnownDevicesDb
{
    private readonly string _connStr;

    public KnownDevicesDb(string path)
    {
        // Make sure the target folder exists (db lives in the scans folder by default).
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        _connStr = $"Data Source={path}";
        using var conn = Open();
        conn.CreateCommand(
            """
            CREATE TABLE IF NOT EXISTS known_devices (
              network_mac TEXT NOT NULL, mac TEXT NOT NULL, ip TEXT,
              hostname TEXT, last_seen TEXT, PRIMARY KEY (network_mac, mac))
            """).ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    public void Save(string networkMac, IEnumerable<Device> devices, string timestamp)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Mac) || d.Mac == Device.Unknown) continue;
            using var cmd = conn.CreateCommand(
                """
                INSERT INTO known_devices (network_mac, mac, ip, hostname, last_seen)
                VALUES ($n, $m, $ip, $h, $t)
                ON CONFLICT(network_mac, mac) DO UPDATE SET
                  ip=excluded.ip,
                  hostname=COALESCE(excluded.hostname, known_devices.hostname),
                  last_seen=excluded.last_seen
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$n", networkMac.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$m", d.Mac.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$ip", d.Ip);
            cmd.Parameters.AddWithValue("$h",
                (object?)(string.IsNullOrEmpty(d.Hostname) ? null : d.Hostname) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", timestamp);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<Device> Load(string networkMac)
    {
        var result = new List<Device>();
        using var conn = Open();
        var cmd = conn.CreateCommand(
            "SELECT ip, mac, hostname FROM known_devices WHERE network_mac = $n");
        cmd.Parameters.AddWithValue("$n", networkMac.ToUpperInvariant());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;   // a device without IP is useless
            result.Add(new Device(r.GetString(0))
            {
                Mac = r.IsDBNull(1) ? Device.Unknown : r.GetString(1),
                Hostname = r.IsDBNull(2) ? Device.Unknown : r.GetString(2),
                FromDb = true,
            });
        }
        return result;
    }

    public string? GetNetworkMac(string gatewayIp)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand(
            "SELECT network_mac FROM known_devices WHERE ip = $ip AND mac = network_mac LIMIT 1");
        cmd.Parameters.AddWithValue("$ip", gatewayIp);
        return cmd.ExecuteScalar() as string;
    }

    public void Clear()
    {
        using var conn = Open();
        conn.CreateCommand("DELETE FROM known_devices").ExecuteNonQuery();
    }

    /// <summary>Merge another instance's database into this one.
    /// On conflicts the newer last_seen wins; hostnames never get lost.
    /// Returns the number of merged rows.</summary>
    public int MergeFrom(string otherDbPath)
    {
        using var src = new SqliteConnection($"Data Source={otherDbPath};Mode=ReadOnly");
        src.Open();
        var read = src.CreateCommand(
            "SELECT network_mac, mac, ip, hostname, last_seen FROM known_devices");
        using var r = read.ExecuteReader();

        using var dst = Open();
        using var tx = dst.BeginTransaction();
        int count = 0;
        while (r.Read())
        {
            using var cmd = dst.CreateCommand(
                """
                INSERT INTO known_devices (network_mac, mac, ip, hostname, last_seen)
                VALUES ($n, $m, $ip, $h, $t)
                ON CONFLICT(network_mac, mac) DO UPDATE SET
                  ip = CASE WHEN excluded.last_seen >= known_devices.last_seen
                            THEN excluded.ip ELSE known_devices.ip END,
                  hostname = COALESCE(
                      CASE WHEN excluded.last_seen >= known_devices.last_seen
                           THEN excluded.hostname ELSE known_devices.hostname END,
                      known_devices.hostname, excluded.hostname),
                  last_seen = MAX(excluded.last_seen, known_devices.last_seen)
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$n", r.GetString(0));
            cmd.Parameters.AddWithValue("$m", r.GetString(1));
            cmd.Parameters.AddWithValue("$ip", r.IsDBNull(2) ? DBNull.Value : (object)r.GetString(2));
            cmd.Parameters.AddWithValue("$h", r.IsDBNull(3) ? DBNull.Value : (object)r.GetString(3));
            cmd.Parameters.AddWithValue("$t", r.IsDBNull(4) ? "" : r.GetString(4));
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }
}
