using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

public sealed record ArchivedScan(long Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    string Network, string[] Subnets, string ConfigSnapshot, int DeviceCount, int OnlineCount,
    int PingSuccess, int PingFailed, int SampleCount, bool Cancelled = false);

public sealed record ArchiveImportResult(int Imported, int Skipped);

public sealed record ArchivedDevice(string Ip, string? Mac, string? Hostname, string? Vendor,
    bool Online, int Pings, int Success, int Fail, double? MinMs, double? AvgMs,
    double? MaxMs, double? LossPercent, double? JitterMs, string? Note,
    int ObservedCount = 1, int OnlineCount = 0,
    DateTimeOffset? FirstSeenAt = null, DateTimeOffset? LastSeenAt = null);

public sealed record ArchivedFinding(string Ip, string Kind, string? Detail);

public sealed record ScanArchiveDetail(ArchivedScan Scan, IReadOnlyList<ArchivedDevice> Devices,
    IReadOnlyList<ArchivedFinding> Findings);

public sealed record ArchivedDeviceStatistics(string DeviceKey, string Ip, string? Mac, string? Hostname,
    int ObservedCount, int OnlineCount, int PingSuccess, int PingFailed, double AvailabilityPercent,
    double? MinMs, double? AvgMs, double? MaxMs, double? JitterMs,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

public sealed record ScanArchiveStatistics(int RunCount, int SampleCount,
    IReadOnlyList<ArchivedDeviceStatistics> Devices);

public sealed partial class KnownDevicesDb
{
    private sealed record ScanHeader(int Version, string Network, string[] Subnets, string ConfigSnapshot);
    private sealed record DeviceEnvelope(int Version, ArchivedDevice Device);
    private sealed record FindingEnvelope(int Version, ArchivedFinding Finding);
    private sealed record ArchiveExport(int Version, IReadOnlyList<ScanArchiveDetail> Scans);
    private sealed record ArchiveExportEnvelope(int Version, string Payload);

    private static void CreateArchiveTables(SqliteConnection conn)
    {
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS scans (
              id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, finished_at TEXT NOT NULL,
              network_tok TEXT NOT NULL, payload BLOB NOT NULL,
              device_count INTEGER NOT NULL, online_count INTEGER NOT NULL,
              ping_success INTEGER NOT NULL, ping_failed INTEGER NOT NULL,
              sample_count INTEGER NOT NULL DEFAULT 1, cancelled INTEGER NOT NULL DEFAULT 0)
            """);
        if (!ColumnExists(conn, "scans", "cancelled"))
            conn.Exec("ALTER TABLE scans ADD COLUMN cancelled INTEGER NOT NULL DEFAULT 0");
        conn.Exec("CREATE INDEX IF NOT EXISTS scans_started_at ON scans(started_at)");
        conn.Exec("CREATE INDEX IF NOT EXISTS scans_network_time ON scans(network_tok, started_at)");
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS scan_devices (
              scan_id INTEGER NOT NULL, device_tok TEXT NOT NULL, payload BLOB NOT NULL,
              PRIMARY KEY (scan_id, device_tok))
            """);
        conn.Exec("CREATE INDEX IF NOT EXISTS scan_devices_device ON scan_devices(device_tok, scan_id)");
        conn.Exec(
            """
            CREATE TABLE IF NOT EXISTS scan_findings (
              scan_id INTEGER NOT NULL, payload BLOB NOT NULL)
            """);
        conn.Exec("CREATE INDEX IF NOT EXISTS scan_findings_scan ON scan_findings(scan_id)");
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand($"PRAGMA table_info({table})");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryParseStamp(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private const string ScanColumns =
        "id, started_at, finished_at, payload, device_count, online_count, ping_success, ping_failed, sample_count, cancelled";

    /// <summary>Reads one scans row (columns as in <see cref="ScanColumns"/>); null when a stored
    /// timestamp is malformed so one corrupt row never takes the whole archive down.</summary>
    private ArchivedScan? ReadScanRow(SqliteDataReader reader)
    {
        if (!TryParseStamp(reader.GetString(1), out var started) ||
            !TryParseStamp(reader.GetString(2), out var finished)) return null;
        var json = _crypto!.Open((byte[])reader[3]);
        var header = json is null ? null : JsonSerializer.Deserialize<ScanHeader>(json);
        if (header?.Version != 1) throw new InvalidDataException("Scan archive format is not supported by this version.");
        return new ArchivedScan(reader.GetInt64(0), started, finished, header.Network,
            header.Subnets, header.ConfigSnapshot, reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9) != 0);
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Writes one completed run atomically. All network and device identifiers
    /// are either keyed tokens or sealed payloads, matching the existing v2 tables.</summary>
    public long SaveScanArchive(DateTimeOffset startedAt, DateTimeOffset finishedAt, string network,
        IReadOnlyList<string> subnets, string configSnapshot, IReadOnlyList<Device> devices,
        IReadOnlyList<ConflictFinding> findings, bool cancelled = false,
        IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null)
    {
        if (_crypto is null) return 0;
        if (finishedAt < startedAt) throw new ArgumentOutOfRangeException(nameof(finishedAt));
        var snapshots = devices.Select(d =>
        {
            lock (d)
            {
                var mac = d.Mac is null or Device.Unknown ? null : d.Mac;
                return new ArchivedDevice(d.Ip, mac,
                    d.Hostname is Device.Unknown ? null : d.Hostname,
                    mac is null ? null : MacVendorLookup.Instance.Lookup(mac),
                    d.IsOnline, d.SuccessCount + d.FailCount, d.SuccessCount, d.FailCount,
                    d.MinMs, d.AvgMs, d.MaxMs, d.LossPercent, d.JitterMs, d.Note,
                    1, d.IsOnline ? 1 : 0);
            }
        }).ToArray();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var scan = new ArchivedScan(0, startedAt, finishedAt, network, subnets.ToArray(),
            configSnapshot, snapshots.Length, snapshots.Count(d => d.Online),
            snapshots.Sum(d => d.Success), snapshots.Sum(d => d.Fail), 1, cancelled);
        var id = InsertArchiveDetail(conn, tx, new ScanArchiveDetail(scan, snapshots,
            findings.Select(f => new ArchivedFinding(f.Ip, f.Kind.ToString(), f.Detail))
                .Concat(ports is null ? [] : ScanCompare.PortFindings(ports)).ToArray()));
        tx.Commit();
        return id;
    }

    private long InsertArchiveDetail(SqliteConnection conn, SqliteTransaction tx, ScanArchiveDetail detail)
    {
        var scan = detail.Scan;
        using var head = conn.CreateCommand(
            """
            INSERT INTO scans (started_at, finished_at, network_tok, payload, device_count,
              online_count, ping_success, ping_failed, sample_count, cancelled)
            VALUES ($start,$finish,$network,$payload,$devices,$online,$success,$failed,$samples,$cancelled);
            SELECT last_insert_rowid()
            """);
        head.Transaction = tx;
        head.Parameters.AddWithValue("$start", Stamp(scan.StartedAt));
        head.Parameters.AddWithValue("$finish", Stamp(scan.FinishedAt));
        head.Parameters.AddWithValue("$network", _crypto!.Token(scan.Network));
        head.Parameters.AddWithValue("$payload", _crypto.Seal(JsonSerializer.Serialize(
            new ScanHeader(1, scan.Network, scan.Subnets, scan.ConfigSnapshot))));
        head.Parameters.AddWithValue("$devices", scan.DeviceCount);
        head.Parameters.AddWithValue("$online", scan.OnlineCount);
        head.Parameters.AddWithValue("$success", scan.PingSuccess);
        head.Parameters.AddWithValue("$failed", scan.PingFailed);
        head.Parameters.AddWithValue("$samples", scan.SampleCount);
        head.Parameters.AddWithValue("$cancelled", scan.Cancelled ? 1 : 0);
        var id = (long)head.ExecuteScalar()!;
        foreach (var device in detail.Devices)
        {
            using var cmd = conn.CreateCommand(
                "INSERT INTO scan_devices (scan_id, device_tok, payload) VALUES ($id,$token,$payload)");
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$token", _crypto.Token(device.Ip));
            cmd.Parameters.AddWithValue("$payload", _crypto.Seal(JsonSerializer.Serialize(new DeviceEnvelope(1, device))));
            cmd.ExecuteNonQuery();
        }
        foreach (var finding in detail.Findings)
        {
            using var cmd = conn.CreateCommand("INSERT INTO scan_findings (scan_id, payload) VALUES ($id,$payload)");
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$payload", _crypto.Seal(JsonSerializer.Serialize(new FindingEnvelope(1, finding))));
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    public IReadOnlyList<ArchivedScan> ListScanArchive(DateTimeOffset? from = null, DateTimeOffset? through = null)
    {
        if (_crypto is null) return new List<ArchivedScan>();
        using var conn = Open();
        return ListScanArchive(conn, from, through);
    }

    private List<ArchivedScan> ListScanArchive(SqliteConnection conn, DateTimeOffset? from, DateTimeOffset? through)
    {
        var result = new List<ArchivedScan>();
        using var cmd = conn.CreateCommand(
            $"SELECT {ScanColumns} FROM scans WHERE started_at >= $from AND started_at <= $through ORDER BY started_at DESC, id DESC");
        cmd.Parameters.AddWithValue("$from", from is null ? "" : Stamp(from.Value));
        cmd.Parameters.AddWithValue("$through", through is null ? "~" : Stamp(through.Value));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (ReadScanRow(reader) is { } scan) result.Add(scan);
        return result;
    }

    public ScanArchiveDetail? LoadScanArchive(long id)
    {
        if (_crypto is null) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand($"SELECT {ScanColumns} FROM scans WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        if (ReadScanRow(reader) is not { } scan) return null;
        reader.Close();
        var devices = new List<ArchivedDevice>();
        using (var dc = conn.CreateCommand("SELECT payload FROM scan_devices WHERE scan_id=$id"))
        {
            dc.Parameters.AddWithValue("$id", id);
            using var dr = dc.ExecuteReader();
            while (dr.Read())
            {
                var body = _crypto.Open((byte[])dr[0]);
                var envelope = body is null ? null : JsonSerializer.Deserialize<DeviceEnvelope>(body);
                if (envelope?.Version != 1) throw new InvalidDataException("Scan device format is not supported by this version.");
                devices.Add(envelope.Device);
            }
        }
        var findings = new List<ArchivedFinding>();
        using (var fc = conn.CreateCommand("SELECT payload FROM scan_findings WHERE scan_id=$id"))
        {
            fc.Parameters.AddWithValue("$id", id);
            using var fr = fc.ExecuteReader();
            while (fr.Read())
            {
                var body = _crypto.Open((byte[])fr[0]);
                var envelope = body is null ? null : JsonSerializer.Deserialize<FindingEnvelope>(body);
                if (envelope?.Version != 1) throw new InvalidDataException("Scan finding format is not supported by this version.");
                findings.Add(envelope.Finding);
            }
        }
        return new ScanArchiveDetail(scan, devices, findings);
    }

    /// <summary>Test seam: called after each run's device rows were folded into the statistics.</summary>
    internal Action? ArchiveStatisticsRunHook { get; set; }

    /// <summary>Returns range-wide availability and latency statistics. Aggregate rows
    /// contribute their original sample counts, so daily compaction does not change
    /// the reported availability. Runs are read one at a time and their rows dropped right
    /// after they are aggregated; cancelled runs are skipped unless
    /// <paramref name="includeCancelled"/> is set.</summary>
    public ScanArchiveStatistics GetScanArchiveStatistics(DateTimeOffset? from = null, DateTimeOffset? through = null,
        bool includeCancelled = false) => ReadArchiveStatistics(from, through, includeCancelled, null);

    /// <summary>The statistics pass; <paramref name="perRun"/>, when set, also receives each run with its
    /// device rows right after they were folded in (the rows are only collected for that callback).</summary>
    private ScanArchiveStatistics ReadArchiveStatistics(DateTimeOffset? from, DateTimeOffset? through,
        bool includeCancelled, Action<ArchivedScan, IReadOnlyList<ArchivedDevice>>? perRun)
    {
        if (_crypto is null) return new ScanArchiveStatistics(0, 0, Array.Empty<ArchivedDeviceStatistics>());
        using var conn = Open();
        // One read snapshot for the whole pass: a concurrent compaction or purge cannot drop runs
        // between the id read and the per-run reads.
        using var snapshot = conn.BeginTransaction(deferred: true);
        var ids = new List<long>();
        using (var list = conn.CreateCommand(
            "SELECT id FROM scans WHERE started_at >= $from AND started_at <= $through" +
            (includeCancelled ? "" : " AND cancelled = 0") + " ORDER BY started_at DESC, id DESC"))
        {
            list.Transaction = snapshot;
            list.Parameters.AddWithValue("$from", from is null ? "" : Stamp(from.Value));
            list.Parameters.AddWithValue("$through", through is null ? "~" : Stamp(through.Value));
            using var idReader = list.ExecuteReader();
            while (idReader.Read()) ids.Add(idReader.GetInt64(0));
        }

        var devices = new Dictionary<string, DeviceStatsBuilder>(StringComparer.OrdinalIgnoreCase);
        int runs = 0, samples = 0;
        foreach (var id in ids)
        {
            ArchivedScan? scan;
            using (var head = conn.CreateCommand($"SELECT {ScanColumns} FROM scans WHERE id=$id"))
            {
                head.Transaction = snapshot;
                head.Parameters.AddWithValue("$id", id);
                using var headReader = head.ExecuteReader();
                scan = headReader.Read() ? ReadScanRow(headReader) : null;
            }
            if (scan is null) continue;
            runs++;
            samples += scan.SampleCount;
            using var rowsCmd = conn.CreateCommand("SELECT payload FROM scan_devices WHERE scan_id=$id");
            rowsCmd.Transaction = snapshot;
            rowsCmd.Parameters.AddWithValue("$id", id);
            var runRows = perRun is null ? null : new List<ArchivedDevice>();
            using (var rows = rowsCmd.ExecuteReader())
            {
                while (rows.Read())
                {
                    var body = _crypto.Open((byte[])rows[0]);
                    var envelope = body is null ? null : JsonSerializer.Deserialize<DeviceEnvelope>(body);
                    if (envelope?.Version != 1) throw new InvalidDataException("Scan device format is not supported by this version.");
                    var device = envelope.Device;
                    runRows?.Add(device);
                    var key = ArchiveDeviceKey(device);
                    if (!devices.TryGetValue(key, out var stats))
                        devices.Add(key, stats = new DeviceStatsBuilder(key, device, scan.StartedAt));
                    else stats.Add(device, scan.StartedAt);
                }
            }
            if (runRows is not null) perRun!(scan, runRows);
            ArchiveStatisticsRunHook?.Invoke();
        }
        return new ScanArchiveStatistics(runs, samples,
            devices.Values.Select(x => x.Build()).OrderBy(x => x.DeviceKey, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public ArchivedDeviceStatistics? GetArchivedDeviceStatistics(string deviceIdentifier,
        DateTimeOffset? from = null, DateTimeOffset? through = null) =>
        GetScanArchiveStatistics(from, through).Devices.FirstOrDefault(x =>
            string.Equals(x.DeviceKey, deviceIdentifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Ip, deviceIdentifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Mac, deviceIdentifier, StringComparison.OrdinalIgnoreCase));

    public bool DeleteScanArchive(long id)
    {
        if (_crypto is null || id <= 0) return false;
        using var conn = Open();
        using (var exists = conn.CreateCommand("SELECT 1 FROM scans WHERE id=$id"))
        {
            exists.Parameters.AddWithValue("$id", id);
            if (exists.ExecuteScalar() is null) return false;
        }
        using var tx = conn.BeginTransaction();
        DeleteArchiveRows(conn, tx, new[] { id });
        tx.Commit();
        return true;
    }

    public int DeleteScanArchiveRange(DateTimeOffset from, DateTimeOffset through)
    {
        if (_crypto is null) return 0;
        if (through < from) throw new ArgumentOutOfRangeException(nameof(through));
        using var conn = Open();
        using var select = conn.CreateCommand("SELECT id FROM scans WHERE started_at >= $from AND started_at <= $through");
        select.Parameters.AddWithValue("$from", Stamp(from));
        select.Parameters.AddWithValue("$through", Stamp(through));
        using var reader = select.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        reader.Close();
        if (ids.Count == 0) return 0;
        using var tx = conn.BeginTransaction();
        DeleteArchiveRows(conn, tx, ids);
        tx.Commit();
        return ids.Count;
    }

    /// <summary>Deletes scan runs older than the requested non-zero retention
    /// window. A zero-day request is deliberately refused to prevent a UI or
    /// configuration mistake from deleting the whole archive.</summary>
    public int PurgeScanArchiveOlderThan(int days, DateTimeOffset? now = null)
    {
        if (_crypto is null || days <= 0) return 0;
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-days);
        using var conn = Open();
        using var select = conn.CreateCommand("SELECT id FROM scans WHERE started_at < $cutoff");
        select.Parameters.AddWithValue("$cutoff", Stamp(cutoff));
        using var reader = select.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        reader.Close();
        if (ids.Count == 0) return 0;
        using var tx = conn.BeginTransaction();
        DeleteArchiveRows(conn, tx, ids);
        tx.Commit();
        return ids.Count;
    }

    /// <summary>Combines multiple runs for the same UTC day and network. Device
    /// sample and online counts are summed, preserving range statistics after
    /// compaction.</summary>
    public int CompactScanArchiveOlderThan(int days, DateTimeOffset? now = null)
    {
        if (days <= 0) return 0;
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-days);
        var groups = ListScanArchive(through: cutoff)
            .Where(x => x.StartedAt < cutoff)
            .GroupBy(x => (x.Network, x.StartedAt.UtcDateTime.Date, x.Cancelled));
        int compacted = 0;
        foreach (var group in groups)
        {
            var scans = group.OrderBy(x => x.StartedAt).ToArray();
            if (scans.Length < 2) continue;
            var details = scans.Select(x => LoadScanArchive(x.Id)).Where(x => x is not null).Cast<ScanArchiveDetail>().ToArray();
            if (details.Length < 2) continue;
            var newest = details.OrderBy(x => x.Scan.StartedAt).Last();
            var devices = details.SelectMany(detail => detail.Devices.Select(device => device with
                {
                    FirstSeenAt = device.FirstSeenAt ?? detail.Scan.StartedAt,
                    LastSeenAt = device.LastSeenAt ?? detail.Scan.StartedAt
                })).GroupBy(ArchiveDeviceKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => MergeArchivedDevice(x)).ToArray();
            var findings = details.SelectMany(x => x.Findings).Distinct().ToArray();
            var merged = new ArchivedScan(0, scans.Min(x => x.StartedAt), scans.Max(x => x.FinishedAt),
                group.Key.Network, newest.Scan.Subnets, newest.Scan.ConfigSnapshot, devices.Length,
                devices.Count(x => x.Online), devices.Sum(x => x.Success), devices.Sum(x => x.Fail),
                scans.Sum(x => x.SampleCount), group.Key.Cancelled);
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            DeleteArchiveRows(conn, tx, scans.Select(x => x.Id));
            InsertArchiveDetail(conn, tx, new ScanArchiveDetail(merged, devices, findings));
            tx.Commit();
            compacted += scans.Length - 1;
        }
        return compacted;
    }

    /// <summary>The after-scan housekeeping: compacts runs older than the compaction age (only
    /// while it stays below a finite retention) and deletes runs older than the archive's own
    /// retention. Device-table retention plays no part here.</summary>
    public void ApplyArchiveMaintenance(ScanConfig cfg, DateTimeOffset? now = null)
    {
        if (cfg.ArchiveCompactionDays > 0 &&
            (cfg.ArchiveRetentionDays <= 0 || cfg.ArchiveCompactionDays < cfg.ArchiveRetentionDays))
            CompactScanArchiveOlderThan(cfg.ArchiveCompactionDays, now);
        if (cfg.ArchiveRetentionDays > 0)
            PurgeScanArchiveOlderThan(cfg.ArchiveRetentionDays, now);
    }

    /// <summary>Writes the logical archive as authenticated encrypted JSON. The file
    /// can only be imported by this installation's local key, avoiding a plaintext
    /// export of network and device data.</summary>
    public int ExportScanArchive(string outputPath)
    {
        if (_crypto is null) return 0;
        var details = ListScanArchive().Select(x => LoadScanArchive(x.Id)).Where(x => x is not null).Cast<ScanArchiveDetail>().ToArray();
        var plaintext = JsonSerializer.Serialize(new ArchiveExport(1, details));
        var envelope = JsonSerializer.Serialize(new ArchiveExportEnvelope(1, Convert.ToBase64String(_crypto.Seal(plaintext))));
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                    writer.Write(envelope);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, fullPath + ".bak");
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return details.Length;
    }

    /// <summary>Imports an encrypted archive only after authenticating and validating
    /// the complete payload, then appends every run in one transaction. A run is identified
    /// by its own start time and network, or is already part of a compacted day, so importing
    /// the same file twice (or an older backup after compaction) changes nothing.</summary>
    public ArchiveImportResult ImportScanArchive(string inputPath)
    {
        if (_crypto is null) return new ArchiveImportResult(0, 0);
        var envelope = JsonSerializer.Deserialize<ArchiveExportEnvelope>(File.ReadAllText(inputPath));
        if (envelope?.Version != 1 || string.IsNullOrWhiteSpace(envelope.Payload))
            throw new InvalidDataException("Scan archive export format is not supported by this version.");
        byte[] sealedPayload;
        try { sealedPayload = Convert.FromBase64String(envelope.Payload); }
        catch (FormatException ex) { throw new InvalidDataException("Scan archive export is invalid.", ex); }
        var plaintext = _crypto.Open(sealedPayload);
        var export = plaintext is null ? null : JsonSerializer.Deserialize<ArchiveExport>(plaintext);
        if (export?.Version != 1 || export.Scans.Any(x => x.Scan.FinishedAt < x.Scan.StartedAt))
            throw new InvalidDataException("Scan archive export could not be authenticated or is not supported.");
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int imported = 0, skipped = 0;
        foreach (var detail in export.Scans)
        {
            if (ArchiveRunExists(conn, tx, detail.Scan)) { skipped++; continue; }
            InsertArchiveDetail(conn, tx, detail);
            imported++;
        }
        tx.Commit();
        return new ArchiveImportResult(imported, skipped);
    }

    private bool ArchiveRunExists(SqliteConnection conn, SqliteTransaction tx, ArchivedScan scan)
    {
        // Compaction folds a day's runs into one row that starts with the earliest run and
        // ends with the latest, so a raw run is also known when such a row of the same
        // network (and cancel state) spans it.
        using var cmd = conn.CreateCommand(
            """
            SELECT 1 FROM scans WHERE network_tok=$network AND (started_at=$start OR
              (sample_count>1 AND cancelled=$cancelled AND started_at<=$start AND finished_at>=$finish))
            LIMIT 1
            """);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$start", Stamp(scan.StartedAt));
        cmd.Parameters.AddWithValue("$finish", Stamp(scan.FinishedAt));
        cmd.Parameters.AddWithValue("$network", _crypto!.Token(scan.Network));
        cmd.Parameters.AddWithValue("$cancelled", scan.Cancelled ? 1 : 0);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Creates the day's automatic encrypted archive backup unless one for that
    /// UTC day already exists (null then), and removes older automatic backups only when
    /// a non-zero retention window is supplied.</summary>
    public string? CreateAutomaticScanArchiveBackup(string directory, int retentionDays,
        DateTimeOffset? now = null)
    {
        if (_crypto is null) return null;
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"scan-archive-{stamp}.json");
        if (File.Exists(output)) return null;
        ExportScanArchive(output);
        PurgeAutomaticScanArchiveBackups(directory, retentionDays, now);
        return output;
    }

    /// <summary>Writes a backup on request under a time-stamped name; never replaces the
    /// automatic backup and is never removed by the automatic retention.</summary>
    public string? CreateManualScanArchiveBackup(string directory, DateTimeOffset? now = null)
    {
        if (_crypto is null) return null;
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"scan-archive-{stamp}.json");
        ExportScanArchive(output);
        return output;
    }

    public int PurgeAutomaticScanArchiveBackups(string directory, int days, DateTimeOffset? now = null)
    {
        if (days <= 0 || !Directory.Exists(directory)) return 0;
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime.Date.AddDays(-days);
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "scan-archive-*"))
        {
            var filename = Path.GetFileName(file);
            var suffix = filename.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) ? 9 :
                filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
            if (suffix == 0) continue;
            var name = filename[..^suffix];
            if (name.Length != 21 || !DateTime.TryParseExact(name[13..], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || date >= cutoff) continue;
            File.Delete(file);
            removed++;
        }
        return removed;
    }

    private static void DeleteArchiveRows(SqliteConnection conn, SqliteTransaction tx, IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            foreach (var table in new[] { "scan_devices", "scan_findings", "scans" })
            {
                using var cmd = conn.CreateCommand($"DELETE FROM {table} WHERE {(table == "scans" ? "id" : "scan_id")}=$id");
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static string ArchiveDeviceKey(ArchivedDevice device) =>
        string.IsNullOrWhiteSpace(device.Mac) ? "ip:" + device.Ip : "mac:" + device.Mac.ToUpperInvariant();

    private static ArchivedDevice MergeArchivedDevice(IGrouping<string, ArchivedDevice> group)
    {
        var rows = group.ToArray();
        var latest = rows.Last();
        var success = rows.Sum(x => x.Success);
        var fail = rows.Sum(x => x.Fail);
        var latencyRows = rows.Where(x => x.AvgMs is not null && x.Success > 0).ToArray();
        var latencyWeight = latencyRows.Sum(x => x.Success);
        return latest with
        {
            Online = rows.Sum(x => x.OnlineCount) > 0,
            Pings = rows.Sum(x => x.Pings), Success = success, Fail = fail,
            MinMs = rows.Where(x => x.MinMs is not null).Select(x => x.MinMs!.Value).Cast<double?>().Min(),
            AvgMs = latencyWeight == 0 ? null : latencyRows.Sum(x => x.AvgMs!.Value * x.Success) / latencyWeight,
            MaxMs = rows.Where(x => x.MaxMs is not null).Select(x => x.MaxMs!.Value).Cast<double?>().Max(),
            LossPercent = success + fail == 0 ? null : 100.0 * fail / (success + fail),
            JitterMs = latencyWeight == 0 ? null : latencyRows.Sum(x => x.JitterMs.GetValueOrDefault() * x.Success) / latencyWeight,
            ObservedCount = rows.Sum(x => x.ObservedCount), OnlineCount = rows.Sum(x => x.OnlineCount),
            FirstSeenAt = rows.Min(x => x.FirstSeenAt), LastSeenAt = rows.Max(x => x.LastSeenAt)
        };
    }

    private sealed class DeviceStatsBuilder
    {
        private readonly string _key;
        private ArchivedDevice _latest;
        private DateTimeOffset _first;
        private DateTimeOffset _last;
        private int _observed;
        private int _online;
        private int _success;
        private int _failed;
        private double? _min;
        private double? _max;
        private double _weightedLatency;
        private int _latencyWeight;
        private double _weightedJitter;

        public DeviceStatsBuilder(string key, ArchivedDevice device, DateTimeOffset at)
        {
            _key = key; _latest = device; _first = at; _last = at;
            Add(device, at);
        }

        public void Add(ArchivedDevice device, DateTimeOffset at)
        {
            var first = device.FirstSeenAt ?? at;
            var last = device.LastSeenAt ?? at;
            if (last >= _last) _latest = device;
            _first = _first < first ? _first : first;
            _last = _last > last ? _last : last;
            _observed += device.ObservedCount;
            _online += device.OnlineCount;
            _success += device.Success;
            _failed += device.Fail;
            if (device.MinMs is { } min) _min = _min is null ? min : Math.Min(_min.Value, min);
            if (device.MaxMs is { } max) _max = _max is null ? max : Math.Max(_max.Value, max);
            if (device.AvgMs is { } avg && device.Success > 0)
            {
                _weightedLatency += avg * device.Success;
                _weightedJitter += device.JitterMs.GetValueOrDefault() * device.Success;
                _latencyWeight += device.Success;
            }
        }

        public ArchivedDeviceStatistics Build() => new(_key, _latest.Ip, _latest.Mac, _latest.Hostname,
            _observed, _online, _success, _failed, _observed == 0 ? 0 : 100.0 * _online / _observed,
            _min, _latencyWeight == 0 ? null : _weightedLatency / _latencyWeight, _max,
            _latencyWeight == 0 ? null : _weightedJitter / _latencyWeight, _first, _last);
    }
}
