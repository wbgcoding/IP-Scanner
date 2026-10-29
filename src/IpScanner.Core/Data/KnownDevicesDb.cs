using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

/// <summary>Thrown by <see cref="KnownDevicesDb.MergeFrom"/> when the other database's
/// device data was sealed under a different Windows account's key and genuinely cannot be
/// read - not a bug, an inherent property of the per-account encryption. The caller
/// shows the themed "export it there, import it here" message instead of a raw error.</summary>
public sealed class ForeignDatabaseException : Exception;

/// <summary>SQLite store of devices seen per network (keyed by gateway MAC). Every
/// column that carries device data (MAC/IP/hostname) is either a one-way lookup token or an
/// AesGcm-sealed blob under a per-installation key (<see cref="LocalCrypto"/>) - opening
/// scanner.db in a SQLite viewer shows no readable device data. <c>last_seen</c>/<c>ts</c>
/// stay plain (needed for the retention cutoff comparison, not device data by themselves).
/// When a device was first and last seen online is part of the sealed payload (fields 5 and 6);
/// a row written before that falls back to its plain <c>last_seen</c> for both.</summary>
public sealed partial class KnownDevicesDb
{
    internal const string KeyFileName = "scanner.key";
    private readonly string _connStr;
    private readonly LocalCrypto? _crypto;

    /// <summary>False when the key file exists but couldn't be unprotected - almost always
    /// a different Windows account. The UI offers "start a new database?" in that case;
    /// every read/write here becomes a safe no-op instead of throwing.</summary>
    public bool IsUsable => _crypto is not null;

    public KnownDevicesDb(string path)
    {
        // Resolve relative to the exe's folder, not the process's current working
        // directory - a shortcut with a different "Start in" folder, or "Run as
        // Administrator", would make a relative DatabasePath write somewhere
        // other than next to the exe. SQLite resolves a relative DataSource against
        // the CWD too, so the absolute path is what actually gets opened.
        path = ScanConfig.ResolvePath(path);
        var dir = System.IO.Path.GetDirectoryName(path);
        var ownedDir = true;
        if (!string.IsNullOrEmpty(dir))
        {
            // Belt-and-suspenders on top of the AesGcm seal below: a folder the app created is
            // restricted to this Windows account; in any other folder only the database file
            // itself is (below), so the folder keeps its permissions.
            ownedDir = FileProtection.PrepareDirectory(dir);
        }
        _crypto = LocalCrypto.TryLoad(System.IO.Path.Combine(dir ?? AppContext.BaseDirectory, KeyFileName));

        // Builder, not string concatenation - a path containing ';' could
        // otherwise inject extra connection parameters (e.g. "Mode=ReadOnly").
        _connStr = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        lock (InitLock)
        {
            // Schema and legacy migration run once per process and file. A vanished file, a file
            // replaced in place (its schema version marker differs), or one first opened without
            // a usable key is initialized again.
            var cached = _initialized.TryGetValue(path, out var migrated) && System.IO.File.Exists(path);
            using var conn = Open();
            var known = cached && ReadSchemaVersion(conn) == SchemaVersion;
            if (!known || (!migrated && _crypto is not null))
            {
                if (!known) CreateSchema(conn);
                if (_crypto is not null) MigrateLegacyTables(conn);
                if (_crypto is not null) AddSeenStamps(conn);
                _initialized[path] = _crypto is not null;
            }
        }
        if (!ownedDir) FileProtection.RestrictFile(path);
    }

    private static readonly object InitLock = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _initialized =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets which files were already initialized, so tests that alter a database's
    /// schema behind the class's back see it rebuilt.</summary>
    internal static void ForgetInitializedFiles() => _initialized.Clear();

    private const int SchemaVersion = 2;

    private static int ReadSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand("PRAGMA user_version");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        // WAL (persistent in the file); the relaxed sync level is per connection, set in Open().
        conn.Exec("PRAGMA journal_mode=WAL;");
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS known_devices_v2 (
              network_tok TEXT NOT NULL, mac_tok TEXT NOT NULL, ip_tok TEXT,
              payload BLOB, last_seen TEXT, PRIMARY KEY (network_tok, mac_tok))
            """);
        conn.Exec("CREATE INDEX IF NOT EXISTS ix_known_ip ON known_devices_v2(ip_tok)");
        // Latency history is tokenized the same way: a plain mac column here would undermine
        // the "no readable device data in this file" guarantee just as much.
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS latency_history_v2 (
              mac_tok TEXT NOT NULL, ts TEXT NOT NULL, avg_ms REAL, min_ms REAL, max_ms REAL,
              PRIMARY KEY (mac_tok, ts))
            """);
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS device_ports (
              network_tok TEXT NOT NULL, mac_tok TEXT NOT NULL, ip_tok TEXT NOT NULL,
              payload BLOB NOT NULL, seen_at TEXT NOT NULL, partial INTEGER NOT NULL,
              PRIMARY KEY (network_tok, mac_tok, ip_tok))
            """);
        AddFamilyColumns(conn);
        CreateArchiveTables(conn);
        conn.Exec($"PRAGMA user_version={SchemaVersion}");
    }

    /// <summary>Adds the address family (4 or 6) to the device and port tables. Existing rows are IPv4 and
    /// keep the default; a file that already has the columns is left alone.</summary>
    private static void AddFamilyColumns(SqliteConnection conn)
    {
        foreach (var table in new[] { "known_devices_v2", "device_ports" })
            if (!ColumnExists(conn, table, "family"))
                conn.Exec($"ALTER TABLE {table} ADD COLUMN family INTEGER NOT NULL DEFAULT 4");
    }

    /// <summary>The stored form of an address: IPv6 in its normalized spelling so every notation of one host
    /// shares a token, IPv4 text unchanged.</summary>
    private static string StoredIp(string ip) => ip.Contains(':') ? Net.IpAddressKey.Normalize(ip) : ip;

    private static int FamilyOf(string ip) => Net.IpAddressKey.TryParse(ip, out var k) && k.IsV6 ? 6 : 4;

    /// <summary>One-time migration from the pre-2.11 plaintext tables: read every row,
    /// seal/tokenize it into the v2 table, drop the old table. Guarded by its own
    /// transaction per table - a failure leaves the old table untouched and logs, rather
    /// than losing data or crashing startup.</summary>
    private void MigrateLegacyTables(SqliteConnection conn)
    {
        var dropped = false;
        try
        {
            if (TableExists(conn, "known_devices"))
            {
                var rows = ReadPlainDeviceRows(conn);
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (net, mac, ip, host, lastSeen) in rows)
                        InsertSealedDeviceRow(conn, tx, net, mac, ip, host, lastSeen);
                    conn.Exec("DROP TABLE known_devices", tx);
                    tx.Commit();
                    dropped = true;
                }
            }
            if (TableExists(conn, "latency_history"))
            {
                var rows = new List<(string Mac, string Ts, double? Avg, double? Min, double? Max)>();
                using (var read = conn.CreateCommand("SELECT mac, ts, avg_ms, min_ms, max_ms FROM latency_history"))
                using (var r = read.ExecuteReader())
                    while (r.Read())
                        rows.Add((r.GetString(0), r.GetString(1),
                            r.IsDBNull(2) ? null : r.GetDouble(2),
                            r.IsDBNull(3) ? null : r.GetDouble(3),
                            r.IsDBNull(4) ? null : r.GetDouble(4)));
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (mac, ts, avg, min, max) in rows)
                    {
                        using var cmd = conn.CreateCommand(
                            "INSERT OR REPLACE INTO latency_history_v2 (mac_tok, ts, avg_ms, min_ms, max_ms) VALUES ($m,$t,$a,$mn,$mx)");
                        cmd.Transaction = tx;
                        cmd.Parameters.AddWithValue("$m", _crypto!.Token(mac));
                        cmd.Parameters.AddWithValue("$t", ts);
                        cmd.Parameters.AddWithValue("$a", (object?)avg ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$mn", (object?)min ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$mx", (object?)max ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    conn.Exec("DROP TABLE latency_history", tx);
                    tx.Commit();
                    dropped = true;
                }
            }
            if (dropped) conn.Exec("VACUUM");
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Db, "Legacy table migration failed, old tables kept", ex);
        }
    }

    /// <summary>One-time migration: a device row sealed before first/last seen existed gets both stamps
    /// added to its payload, copied from its plain last_seen. Rows that already carry them are left alone.</summary>
    private void AddSeenStamps(SqliteConnection conn)
    {
        try
        {
            var todo = new List<(string Net, string Mac, byte[] Payload)>();
            using (var read = conn.CreateCommand("SELECT network_tok, mac_tok, payload, last_seen FROM known_devices_v2"))
            using (var r = read.ExecuteReader())
                while (r.Read())
                {
                    if (r.IsDBNull(2)) continue;
                    var parts = _crypto!.Open((byte[])r[2])?.Split('\t');
                    if (parts is not { Length: >= 4 } || parts.Length >= 6) continue;
                    var last = r.IsDBNull(3) ? "" : r.GetString(3);
                    var upgraded = _crypto.Seal(string.Join('\t', parts.Take(4).Append(last).Append(last)));
                    todo.Add((r.GetString(0), r.GetString(1), upgraded));
                }
            if (todo.Count == 0) return;
            using var tx = conn.BeginTransaction();
            foreach (var (net, mac, payload) in todo)
            {
                using var cmd = conn.CreateCommand("UPDATE known_devices_v2 SET payload=$p WHERE network_tok=$n AND mac_tok=$m");
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("$p", payload);
                cmd.Parameters.AddWithValue("$n", net);
                cmd.Parameters.AddWithValue("$m", mac);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Db, "First-seen migration failed, rows keep their last seen time", ex);
        }
    }

    private void InsertSealedDeviceRow(SqliteConnection conn, SqliteTransaction tx,
        string net, string mac, string ip, string? host, string lastSeen,
        string? firstSeen = null, string? lastOnline = null)
    {
        ip = StoredIp(ip);
        var payload = _crypto!.Seal($"{net}\t{mac}\t{ip}\t{host ?? ""}\t{firstSeen ?? lastSeen}\t{lastOnline ?? lastSeen}");
        using var cmd = conn.CreateCommand(
            "INSERT OR REPLACE INTO known_devices_v2 (network_tok, mac_tok, ip_tok, payload, last_seen, family) VALUES ($n,$m,$i,$p,$t,$f)");
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$n", _crypto.Token(net));
        cmd.Parameters.AddWithValue("$m", _crypto.Token(mac));
        cmd.Parameters.AddWithValue("$i", _crypto.Token(ip));
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$t", lastSeen);
        cmd.Parameters.AddWithValue("$f", FamilyOf(ip));
        cmd.ExecuteNonQuery();
    }

    private static List<(string Net, string Mac, string Ip, string? Host, string LastSeen)> ReadPlainDeviceRows(SqliteConnection conn)
    {
        var rows = new List<(string, string, string, string?, string)>();
        using var read = conn.CreateCommand("SELECT network_mac, mac, ip, hostname, last_seen FROM known_devices");
        using var r = read.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(2)) continue;   // no IP -> useless
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
        }
        return rows;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand("SELECT name FROM sqlite_master WHERE type='table' AND name=$n");
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        c.Exec("PRAGMA synchronous=NORMAL;");
        return c;
    }

    public void Save(string networkMac, IEnumerable<Device> devices, string timestamp)
    {
        if (_crypto is null) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var net = networkMac.ToUpperInvariant();
        var networkTok = _crypto.Token(net);
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Mac) || d.Mac == Device.Unknown) continue;
            var mac = d.Mac.ToUpperInvariant();
            var macTok = _crypto.Token(mac);
            var hostname = string.IsNullOrEmpty(d.Hostname) || d.Hostname == Device.Unknown ? null : d.Hostname;
            var existing = FindExistingRow(conn, tx, networkTok, macTok);
            // A device that did not answer in this run keeps the times it had; one that did
            // moves its last-seen time up. The first time is set once, on the first insert.
            var online = d.IsOnline || (d.Seen && !d.IsListed);
            var first = timestamp;
            var lastOnline = timestamp;
            if (existing is { } old)
            {
                first = online ? Earlier(old.First, timestamp) : old.First;
                lastOnline = online ? timestamp : old.LastOnline;
            }
            InsertSealedDeviceRow(conn, tx, net, mac, d.Ip, hostname ?? existing?.Host, timestamp, first, lastOnline);
        }
        tx.Commit();
    }

    /// <summary>The earlier of two "yyyyMMdd_HHmmss" stamps; an empty one counts as unknown.</summary>
    private static string Earlier(string a, string b)
        => a.Length == 0 ? b : b.Length == 0 ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;

    /// <summary>The later of two "yyyyMMdd_HHmmss" stamps; an empty one counts as unknown.</summary>
    private static string Later(string a, string b)
        => string.CompareOrdinal(a, b) >= 0 ? a : b;

    /// <summary>First-seen and last-seen-online stamps of a sealed payload; a row from before they
    /// existed uses its plain last_seen column for both.</summary>
    private static (string First, string LastOnline) StampsOf(string[] parts, string columnLastSeen) => (
        parts.Length > 4 && parts[4].Length > 0 ? parts[4] : columnLastSeen,
        parts.Length > 5 && parts[5].Length > 0 ? parts[5] : columnLastSeen);

    private static DateTimeOffset? ParseStamp(string text) =>
        DateTimeOffset.TryParseExact(text, LastSeenFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var when) ? when : null;

    public List<Device> Load(string networkMac)
    {
        var result = new List<Device>();
        if (_crypto is null) return result;
        using var conn = Open();
        using var cmd = conn.CreateCommand("SELECT payload, last_seen FROM known_devices_v2 WHERE network_tok = $n");
        cmd.Parameters.AddWithValue("$n", _crypto.Token(networkMac.ToUpperInvariant()));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            var parts = _crypto.Open((byte[])r[0])?.Split('\t');
            if (parts is not { Length: >= 3 } || parts[2].Length == 0) continue;   // no IP -> useless
            var (first, lastOnline) = StampsOf(parts, r.IsDBNull(1) ? "" : r.GetString(1));
            result.Add(new Device(parts[2])
            {
                Mac = parts[1].Length > 0 ? parts[1] : Device.Unknown,
                Hostname = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : Device.Unknown,
                FromDb = true,
                FirstSeen = ParseStamp(first),
                LastSeen = ParseStamp(lastOnline),
            });
        }
        return result;
    }

    public string? GetNetworkMac(string gatewayIp)
    {
        if (_crypto is null) return null;
        gatewayIp = StoredIp(gatewayIp);
        using var conn = Open();
        using var cmd = conn.CreateCommand("SELECT payload FROM known_devices_v2 WHERE ip_tok = $ip AND mac_tok = network_tok LIMIT 1");
        cmd.Parameters.AddWithValue("$ip", _crypto.Token(gatewayIp));
        if (cmd.ExecuteScalar() is byte[] payload)
        {
            var parts = _crypto.Open(payload)?.Split('\t');
            if (parts is { Length: > 0 } && parts[0].Length > 0) return parts[0];
        }
        return null;
    }

    /// <summary>Deletes every stored device with its port results and latency history, and
    /// with <paramref name="includeArchive"/> the scan archive too, then compacts the file.
    /// Returns how many devices were stored.</summary>
    public int Clear(bool includeArchive)
    {
        using var conn = Open();
        int devices;
        using (var count = conn.CreateCommand("SELECT COUNT(*) FROM known_devices_v2"))
            devices = Convert.ToInt32(count.ExecuteScalar());
        var tables = new List<string> { "known_devices_v2", "device_ports", "latency_history_v2" };
        if (includeArchive) tables.AddRange(new[] { "scan_findings", "scan_devices", "scans" });
        using (var tx = conn.BeginTransaction())
        {
            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand($"DELETE FROM {table}");
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // The rows are gone for good at this point; shrinking the file is only a courtesy and
        // must not turn a finished clear into an error (a scan may hold the file busy).
        try { conn.Exec("VACUUM"); }
        catch (SqliteException ex) { Log.Warn(LogArea.Db, "Shrinking the database after a clear failed", ex); /* compacted by a later clear */ }
        return devices;
    }

    /// <summary>Removes the archive backup files (automatic, manual and their previous
    /// generations) from <paramref name="directory"/>; other files stay. Returns the count.</summary>
    public static int DeleteArchiveBackups(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return 0;
        int removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "scan-archive-*"))
        {
            // One locked copy must not hide that the others are gone.
            try { System.IO.File.Delete(file); removed++; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Warn(LogArea.Db, "An archive backup copy could not be deleted", ex);
            }
        }
        return removed;
    }

    public sealed record StoredPortScan(DateTimeOffset SeenAt, bool Partial, IReadOnlyList<NetService> Services);

    private sealed record PortEntry(int Port, NetProtocol Protocol, string Name, string? Version,
        ServiceConfidence Confidence);
    private sealed record PortPayload(int Version, PortEntry[] Entries);

    public bool SavePortScan(string network, string mac, string ip, IEnumerable<NetService> services,
        DateTimeOffset seenAt, bool partial)
    {
        if (_crypto is null) return false;
        ip = StoredIp(ip);
        var entries = services.Select(s => new PortEntry(s.Port, s.Protocol, s.Name, s.Version, s.Confidence)).ToArray();
        var payload = _crypto.Seal(System.Text.Json.JsonSerializer.Serialize(new PortPayload(1, entries)));
        using var conn = Open();
        using var cmd = conn.CreateCommand(
            """
            INSERT OR REPLACE INTO device_ports (network_tok, mac_tok, ip_tok, payload, seen_at, partial, family)
            VALUES ($n, $m, $i, $p, $t, $partial, $f)
            """);
        cmd.Parameters.AddWithValue("$n", _crypto.Token(network.ToUpperInvariant()));
        cmd.Parameters.AddWithValue("$m", _crypto.Token(mac.ToUpperInvariant()));
        cmd.Parameters.AddWithValue("$i", _crypto.Token(ip));
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$t", seenAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$partial", partial ? 1 : 0);
        cmd.Parameters.AddWithValue("$f", FamilyOf(ip));
        cmd.ExecuteNonQuery();
        return true;
    }

    public StoredPortScan? LoadPortScan(string network, string mac, string ip)
    {
        if (_crypto is null) return null;
        ip = StoredIp(ip);
        using var conn = Open();
        using var cmd = conn.CreateCommand(
            "SELECT payload, seen_at, partial FROM device_ports WHERE network_tok=$n AND mac_tok=$m AND ip_tok=$i");
        cmd.Parameters.AddWithValue("$n", _crypto.Token(network.ToUpperInvariant()));
        cmd.Parameters.AddWithValue("$m", _crypto.Token(mac.ToUpperInvariant()));
        cmd.Parameters.AddWithValue("$i", _crypto.Token(ip));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var json = _crypto.Open((byte[])reader[0]);
        if (json is null || !DateTimeOffset.TryParseExact(reader.GetString(1), "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var seenAt)) return null;
        var payload = System.Text.Json.JsonSerializer.Deserialize<PortPayload>(json);
        if (payload?.Version != 1)
            throw new System.IO.InvalidDataException("Port result format is not supported by this version.");
        return new StoredPortScan(seenAt, reader.GetInt32(2) != 0,
            payload.Entries.Select(s => new NetService(s.Name, s.Port, s.Protocol, s.Version, s.Confidence) { Ip = ip }).ToArray());
    }

    /// <summary>Deletes rows not seen within the retention window.
    /// days &lt;= 0 means "unbegrenzt" - no-op. last_seen/ts are "yyyyMMdd_HHmmss"
    /// strings, which sort lexicographically the same as chronologically, so a
    /// plain string comparison against the cutoff is enough. Also purges
    /// latency_history_v2 on the same cutoff.</summary>
    public int PurgeOlderThan(int days)
    {
        if (days <= 0) return 0;
        var cutoff = DateTime.Now.AddDays(-days).ToString("yyyyMMdd_HHmmss");
        using var conn = Open();
        using var cmd = conn.CreateCommand("DELETE FROM known_devices_v2 WHERE last_seen IS NOT NULL AND last_seen < $cutoff");
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        int n = cmd.ExecuteNonQuery();
        using var cmd2 = conn.CreateCommand("DELETE FROM latency_history_v2 WHERE ts < $cutoff");
        cmd2.Parameters.AddWithValue("$cutoff", cutoff);
        n += cmd2.ExecuteNonQuery();
        using var cmd3 = conn.CreateCommand("DELETE FROM device_ports WHERE seen_at < $cutoff");
        cmd3.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.AddDays(-days).ToUniversalTime().ToString("O"));
        n += cmd3.ExecuteNonQuery();
        return n;
    }

    /// <summary>Records this scan's per-device latency for the 24h/7d
    /// history views. Only devices that answered at least once (AvgMs present)
    /// are worth a row - offline devices would just be null spam.</summary>
    public void SaveLatencyHistory(IEnumerable<Device> devices, string timestamp)
    {
        if (_crypto is null) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Mac) || d.Mac == Device.Unknown || d.AvgMs is null) continue;
            using var cmd = conn.CreateCommand(
                """
                INSERT OR REPLACE INTO latency_history_v2 (mac_tok, ts, avg_ms, min_ms, max_ms)
                VALUES ($m, $t, $avg, $min, $max)
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$m", _crypto.Token(d.Mac.ToUpperInvariant()));
            cmd.Parameters.AddWithValue("$t", timestamp);
            cmd.Parameters.AddWithValue("$avg", d.AvgMs.Value);
            cmd.Parameters.AddWithValue("$min", (object?)d.MinMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max", (object?)d.MaxMs ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Raw per-scan latency rows for one device since a cutoff -
    /// the caller (LatencyHistoryAggregator) buckets them into the graph series.</summary>
    public List<LatencyHistoryAggregator.Row> LoadLatencyHistory(string mac, DateTime since)
    {
        var result = new List<LatencyHistoryAggregator.Row>();
        if (_crypto is null) return result;
        using var conn = Open();
        using var cmd = conn.CreateCommand(
            "SELECT ts, avg_ms, min_ms, max_ms FROM latency_history_v2 WHERE mac_tok = $m AND ts >= $since");
        cmd.Parameters.AddWithValue("$m", _crypto.Token(mac.ToUpperInvariant()));
        cmd.Parameters.AddWithValue("$since", since.ToString("yyyyMMdd_HHmmss"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParseExact(r.GetString(0), "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var ts)) continue;
            result.Add(new LatencyHistoryAggregator.Row(ts,
                r.IsDBNull(1) ? null : r.GetDouble(1),
                r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3)));
        }
        return result;
    }

    /// <summary>Merges another instance's database into this one. On conflicts the newer
    /// last_seen wins; hostnames never get lost. The source can be a pre-2.11
    /// plain database (read directly), a v2 database sealed under a key this same Windows
    /// account can also unprotect (its sibling <c>scanner.key</c> - DPAPI is per-account, not
    /// per-installation, so this works for any v2 database made on this account, any folder),
    /// or a v2 database from a different account, which throws <see cref="ForeignDatabaseException"/>
    /// - genuinely unreadable, not a bug; the caller points at export/import instead.
    /// Returns the number of merged rows.</summary>
    public int MergeFrom(string otherDbPath, IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_crypto is null) throw new ForeignDatabaseException();

        var srcConnStr = new SqliteConnectionStringBuilder
            { DataSource = otherDbPath, Mode = SqliteOpenMode.ReadOnly }.ConnectionString;
        using var src = new SqliteConnection(srcConnStr);
        src.Open();

        var sealedSource = TableExists(src, "known_devices_v2");
        var plainSource = !sealedSource && TableExists(src, "known_devices");
        if (!sealedSource && !plainSource) return 0;
        using var countCommand = src.CreateCommand(sealedSource
            ? "SELECT COUNT(*) FROM known_devices_v2" : "SELECT COUNT(*) FROM known_devices");
        int total = checked(Convert.ToInt32(countCommand.ExecuteScalar()));
        progress?.Report((0, total));
        cancellationToken.ThrowIfCancellationRequested();

        List<(string Net, string Mac, string Ip, string? Host, string LastSeen, string First, string LastOnline)> rows;
        if (sealedSource)
        {
            var srcKeyPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(ScanConfig.ResolvePath(otherDbPath)) ?? AppContext.BaseDirectory,
                KeyFileName);
            var srcCrypto = LocalCrypto.TryLoadExisting(srcKeyPath)
                ?? throw new ForeignDatabaseException();
            rows = new List<(string, string, string, string?, string, string, string)>();
            using var read = src.CreateCommand("SELECT payload, last_seen FROM known_devices_v2");
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (r.IsDBNull(0)) continue;
                var parts = srcCrypto.Open((byte[])r[0])?.Split('\t');
                if (parts is not { Length: >= 3 } || parts[0].Length == 0 || parts[2].Length == 0) continue;
                var srcLast = r.IsDBNull(1) ? "" : r.GetString(1);
                var (srcFirst, srcLastOnline) = StampsOf(parts, srcLast);
                rows.Add((parts[0], parts[1], parts[2], parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null,
                    srcLast, srcFirst, srcLastOnline));
            }
        }
        else
        {
            rows = new List<(string, string, string, string?, string, string, string)>();
            using var read = src.CreateCommand("SELECT network_mac, mac, ip, hostname, last_seen FROM known_devices");
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (r.IsDBNull(2)) continue;
                var plainLast = r.IsDBNull(4) ? "" : r.GetString(4);
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), plainLast, plainLast, plainLast));
            }
        }

        using var dst = Open();
        using var tx = dst.BeginTransaction();
        int count = 0;
        var latestAllowed = DateTimeOffset.Now.AddDays(1);
        foreach (var (rawNet, rawMac, rawIp, rawHost, lastSeen, rowFirst, rowLastOnline) in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeRow(rawNet, rawMac, rawIp, lastSeen, latestAllowed, out var net, out var mac, out var ip)) continue;
            var host = rawHost is null ? null : NetworkText.Sanitize(rawHost);
            if (host is { Length: 0 }) host = null;
            var networkTok = _crypto.Token(net);
            var macTok = _crypto.Token(mac);
            var finalHost = host;
            var finalLastSeen = lastSeen;
            var finalFirst = rowFirst;
            var finalLastOnline = rowLastOnline;
            using (var sel = dst.CreateCommand("SELECT payload, last_seen FROM known_devices_v2 WHERE network_tok=$n AND mac_tok=$m"))
            {
                sel.Transaction = tx;
                sel.Parameters.AddWithValue("$n", networkTok);
                sel.Parameters.AddWithValue("$m", macTok);
                using var er = sel.ExecuteReader();
                if (er.Read())
                {
                    var existingLastSeen = er.IsDBNull(1) ? "" : er.GetString(1);
                    var newerWins = string.CompareOrdinal(lastSeen, existingLastSeen) >= 0;
                    string? existingHost = null;
                    if (!er.IsDBNull(0))
                    {
                        var p = _crypto.Open((byte[])er[0])?.Split('\t');
                        if (p is { Length: > 3 } && p[3].Length > 0) existingHost = p[3];
                        if (p is not null)
                        {
                            var (existingFirst, existingLastOnline) = StampsOf(p, existingLastSeen);
                            finalFirst = Earlier(existingFirst, rowFirst);
                            finalLastOnline = Later(existingLastOnline, rowLastOnline);
                        }
                    }
                    finalHost = newerWins ? (host ?? existingHost) : (existingHost ?? host);
                    finalLastSeen = newerWins ? lastSeen : existingLastSeen;
                }
            }
            InsertSealedDeviceRow(dst, tx, net, mac, ip, finalHost, finalLastSeen, finalFirst, finalLastOnline);
            count++;
            if ((count & 63) == 0) progress?.Report((count, total));
        }
        progress?.Report((total, total));
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        return count;
    }

    /// <summary>The "export stays possible" half - a plain, portable CSV
    /// (network MAC, MAC, IP, hostname, last seen) that reads back through
    /// <see cref="ImportCsv"/>, for moving device history to another Windows account/
    /// machine without a shared key. Returns the number of rows written.</summary>
    public int ExportCsv(string outputPath)
    {
        if (_crypto is null) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand("SELECT payload, last_seen FROM known_devices_v2");
        using var r = cmd.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("network_mac,mac,ip,hostname,last_seen");
        int count = 0;
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            var parts = _crypto.Open((byte[])r[0])?.Split('\t');
            if (parts is not { Length: >= 3 }) continue;
            var host = parts.Length > 3 ? parts[3] : "";
            var lastSeen = r.IsDBNull(1) ? "" : r.GetString(1);
            sb.AppendLine(string.Join(',', new[] { parts[0], parts[1], parts[2], host, lastSeen }
                .Select(x => CsvExporter.Esc(CsvExporter.DefuseFormula(x)))));
            count++;
        }
        System.IO.File.WriteAllText(outputPath, sb.ToString(), System.Text.Encoding.UTF8);
        return count;
    }

    private const string LastSeenFormat = "yyyyMMdd_HHmmss";

    /// <summary>Reads a file written by <see cref="ExportCsv"/> back in. Rows with a
    /// malformed IP or MAC, an unreadable or future last_seen are skipped; for a row that
    /// already exists the newer last_seen wins and a hostname is never lost, as in
    /// <see cref="MergeFrom"/>. Returns how many rows were written and how many were
    /// skipped (invalid, or older than what the database already holds).</summary>
    public (int Imported, int Skipped) ImportCsv(string inputPath)
    {
        if (_crypto is null) return (0, 0);
        var lines = System.IO.File.ReadAllLines(inputPath);
        var latestAllowed = DateTimeOffset.Now.AddDays(1);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int imported = 0, skipped = 0;
        for (int i = 1; i < lines.Length; i++)   // row 0 is the header
        {
            if (lines[i].Trim().Length == 0) continue;
            var f = ParseCsvLine(lines[i]);
            var lastSeen = f.Length > 4 ? f[4] : "";
            if (f.Length < 3 || !TryNormalizeRow(f[0], f[1], f[2], lastSeen, latestAllowed, out var net, out var mac, out var ip))
            { skipped++; continue; }
            var host = f.Length > 3 && f[3].Length > 0 ? CleanImportedText(f[3]) : null;
            if (host is { Length: 0 }) host = null;

            var existing = FindExistingRow(conn, tx, _crypto.Token(net), _crypto.Token(mac));
            var importedStamp = lastSeen;
            string? firstSeen = null, lastOnline = null;
            if (existing is { } old)
            {
                firstSeen = Earlier(old.First, importedStamp);
                lastOnline = Later(old.LastOnline, importedStamp);
                if (string.CompareOrdinal(lastSeen, old.LastSeen) < 0)
                {
                    // Older than what is stored: keep the newer row, only fill a missing hostname.
                    if (old.Host is not null || host is null) { skipped++; continue; }
                    ip = old.Ip;
                    lastSeen = old.LastSeen;
                }
                else host ??= old.Host;
            }
            InsertSealedDeviceRow(conn, tx, net, mac, ip, host, lastSeen, firstSeen, lastOnline);
            imported++;
        }
        tx.Commit();
        return (imported, skipped);
    }

    /// <summary>The one validation every foreign row passes (CSV import and database merge): a plain IPv4 or
    /// an IPv6 address (returned normalized), a MAC (and an empty-or-valid network MAC) and an acceptable last_seen. MACs come back in the
    /// scanner's own form, upper case with colons, so the same device never gets a second token.</summary>
    private static bool TryNormalizeRow(string net, string mac, string ip, string lastSeen,
        DateTimeOffset latestAllowed, out string normalizedNet, out string normalizedMac, out string normalizedIp)
    {
        normalizedNet = net.Replace('-', ':').ToUpperInvariant();
        normalizedMac = mac.Replace('-', ':').ToUpperInvariant();
        normalizedIp = ip;
        if (ip.Contains(':'))
        {
            if (!Net.IpAddressKey.TryParse(ip, out var key) || !key.IsV6) return false;
            normalizedIp = key.Text;
        }
        else if (!IsPlainIpv4(ip) || !Net.Ipv4.IsValid(ip)) return false;
        return WakeOnLan.IsValidMac(normalizedMac)
            && (normalizedNet.Length == 0 || WakeOnLan.IsValidMac(normalizedNet))
            && (lastSeen.Length == 0 || IsAcceptableLastSeen(lastSeen, latestAllowed));
    }

    private static bool IsPlainIpv4(string ip) => ip.All(c => c is >= '0' and <= '9' or '.');

    private static bool IsAcceptableLastSeen(string text, DateTimeOffset latestAllowed) =>
        DateTimeOffset.TryParseExact(text, LastSeenFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var when) && when <= latestAllowed;

    /// <summary>Undoes the export's formula defusing (one leading apostrophe in front of
    /// = + - @ tab or CR), then strips control characters and caps the length.</summary>
    private static string CleanImportedText(string text)
    {
        if (text.Length > 1 && text[0] == '\'' && text[1] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = text[1..];
        return NetworkText.Sanitize(text);
    }

    private (string Ip, string? Host, string LastSeen, string First, string LastOnline)? FindExistingRow(
        SqliteConnection conn, SqliteTransaction tx, string networkTok, string macTok)
    {
        using var sel = conn.CreateCommand(
            "SELECT payload, last_seen FROM known_devices_v2 WHERE network_tok=$n AND mac_tok=$m");
        sel.Transaction = tx;
        sel.Parameters.AddWithValue("$n", networkTok);
        sel.Parameters.AddWithValue("$m", macTok);
        using var r = sel.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        var parts = _crypto!.Open((byte[])r[0])?.Split('\t');
        if (parts is not { Length: >= 3 }) return null;
        var lastSeen = r.IsDBNull(1) ? "" : r.GetString(1);
        var (first, lastOnline) = StampsOf(parts, lastSeen);
        return (parts[2], parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null, lastSeen, first, lastOnline);
    }

    /// <summary>Minimal RFC 4180 reader for <see cref="ImportCsv"/> - our own export's
    /// ip/mac/timestamp fields never need quoting, only a hostname can.</summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
