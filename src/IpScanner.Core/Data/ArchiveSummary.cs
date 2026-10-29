using System.IO;
using System.Text.Json;

namespace IpScanner.Core.Data;

/// <summary>One run in the summary's time series (oldest first). Cancelled runs are listed
/// but are not meant to be charted.</summary>
public sealed record ArchiveRunPoint(long RunId, DateTimeOffset StartedAt, int OnlineCount, int DeviceCount,
    int FindingCount, bool Cancelled, double? AvgLatencyMs);

/// <summary>The statistics, the headline summary and the run list (newest first) of one date range, read in a single pass.</summary>
public sealed record ArchiveOverview(ScanArchiveStatistics Statistics, ArchiveSummary Summary,
    IReadOnlyList<ArchivedScan> Scans);

/// <summary>Headline figures for a date range of the scan archive.</summary>
public sealed record ArchiveSummary(
    int RunCount, int CancelledCount, DateTimeOffset? FirstAt, DateTimeOffset? LastAt,
    int DistinctDevices, int NewDevicesInRange,
    double AvgOnlinePerRun, int LastRunOnline, int LastRunDevices,
    int PingTotal, double AvailabilityPercent, double LossPercent,
    double? AvgLatencyMs, double? MinLatencyMs, double? MaxLatencyMs, double? AvgJitterMs,
    int FindingCount, int OpenPortCount, double AvgDurationSeconds,
    IReadOnlyList<ArchiveRunPoint> Runs)
{
    public static ArchiveSummary Empty { get; } = new(0, 0, null, null, 0, 0, 0, 0, 0, 0, 0, 0,
        null, null, null, null, 0, 0, 0, Array.Empty<ArchiveRunPoint>());
}

public sealed partial class KnownDevicesDb
{
    /// <summary>Headline figures for the archive page: run counts, devices, availability, latency,
    /// findings and a per-run series. "New" devices are those first seen inside the range: without a
    /// lower bound the oldest finished run in the range is the baseline.</summary>
    public ArchiveSummary GetScanArchiveSummary(DateTimeOffset? from = null, DateTimeOffset? through = null) =>
        GetScanArchiveOverview(from, through).Summary;

    /// <summary>The device statistics and the headline summary of a range from one read of the archive.</summary>
    public ArchiveOverview GetScanArchiveOverview(DateTimeOffset? from = null, DateTimeOffset? through = null)
    {
        if (_crypto is null)
            return new ArchiveOverview(new ScanArchiveStatistics(0, 0, Array.Empty<ArchivedDeviceStatistics>()),
                ArchiveSummary.Empty, Array.Empty<ArchivedScan>());
        var runs = ListScanArchive(from, through);
        HashSet<string>? oldestKeys = null;
        var oldestId = runs.Where(x => !x.Cancelled).MinBy(x => x.StartedAt)?.Id;
        var runLatency = new Dictionary<long, double?>();
        var stats = ReadArchiveStatistics(from, through, false, (scan, rows) =>
        {
            if (scan.Id == oldestId)
                oldestKeys = rows.Select(ArchiveDeviceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            double sum = 0; long weight = 0;
            foreach (var d in rows)
                if (d.AvgMs is { } ms && d.Success > 0) { sum += ms * d.Success; weight += d.Success; }
            runLatency[scan.Id] = weight == 0 ? null : sum / weight;
        });
        if (runs.Count == 0) return new ArchiveOverview(stats, ArchiveSummary.Empty, runs);
        var keys = stats.Devices.Select(x => x.DeviceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Devices seen before the range; with no earlier history the oldest finished run is the baseline.
        HashSet<string>? baseline = null;
        if (from is not null)
        {
            var earlier = GetScanArchiveStatistics(null, from.Value.AddTicks(-1));
            if (earlier.RunCount > 0)
                baseline = earlier.Devices.Select(x => x.DeviceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        baseline ??= oldestKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newDevices = keys.Count(x => !baseline.Contains(x));

        var findingsByRun = ReadRunFindings(runs, out var openPorts);
        var chronological = runs.OrderBy(x => x.StartedAt).ThenBy(x => x.Id).ToArray();
        var points = chronological.Select(x => new ArchiveRunPoint(x.Id, x.StartedAt, x.OnlineCount,
            x.DeviceCount, findingsByRun.GetValueOrDefault(x.Id), x.Cancelled,
            runLatency.GetValueOrDefault(x.Id))).ToArray();

        var done = runs.Where(x => !x.Cancelled).ToArray();
        var latest = done.Length > 0 ? done[0] : runs[0];   // newest first; a cancelled run only headlines when nothing finished
        var singles = done.Where(x => x.SampleCount <= 1).ToArray();
        var success = stats.Devices.Sum(x => x.PingSuccess);
        var fail = stats.Devices.Sum(x => x.PingFailed);
        var total = success + fail;
        var availability = total == 0 ? 0 : 100.0 * success / total;
        double? Weighted(Func<ArchivedDeviceStatistics, double?> pick)
        {
            double sum = 0; long weight = 0;
            foreach (var d in stats.Devices)
                if (pick(d) is { } v && d.PingSuccess > 0) { sum += v * d.PingSuccess; weight += d.PingSuccess; }
            return weight == 0 ? null : sum / weight;
        }

        return new ArchiveOverview(stats, new ArchiveSummary(
            runs.Count, runs.Count(x => x.Cancelled), chronological[0].StartedAt, chronological[^1].StartedAt,
            stats.Devices.Count, newDevices,
            done.Length == 0 ? 0 : done.Average(x => x.OnlineCount), latest.OnlineCount, latest.DeviceCount,
            total, availability, total == 0 ? 0 : 100 - availability,
            Weighted(x => x.AvgMs),
            stats.Devices.Select(x => x.MinMs).Where(x => x is not null).DefaultIfEmpty(null).Min(),
            stats.Devices.Select(x => x.MaxMs).Where(x => x is not null).DefaultIfEmpty(null).Max(),
            Weighted(x => x.JitterMs),
            points.Sum(x => x.FindingCount), openPorts,
            singles.Length == 0 ? 0 : singles.Average(x => (x.FinishedAt - x.StartedAt).TotalSeconds),
            points), runs);
    }

    /// <summary>Counts, per run, the conflict findings plus the port changes against the previous
    /// finished single run (hosts port-scanned in both; opened and closed count once per host),
    /// and the distinct open ip:port pairs of the newest finished run.</summary>
    private Dictionary<long, int> ReadRunFindings(IReadOnlyList<ArchivedScan> runs, out int openPorts)
    {
        var counts = new Dictionary<long, int>();
        var perRun = new Dictionary<long, List<ArchivedFinding>>();
        openPorts = 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        foreach (var run in runs)
        {
            var list = new List<ArchivedFinding>();
            using var cmd = conn.CreateCommand("SELECT payload FROM scan_findings WHERE scan_id=$id");
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", run.Id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var body = _crypto!.Open((byte[])reader[0]);
                var envelope = body is null ? null : JsonSerializer.Deserialize<FindingEnvelope>(body);
                if (envelope?.Version != 1) throw new InvalidDataException("Scan finding format is not supported by this version.");
                list.Add(envelope.Finding);
            }
            perRun[run.Id] = list;
            counts[run.Id] = list.Count(f => f.Kind != ScanCompare.PortScannedKind && f.Kind != ScanCompare.OpenPortKind);
        }

        Dictionary<string, HashSet<string>>? previous = null;
        foreach (var run in runs.Where(x => !x.Cancelled && x.SampleCount <= 1).OrderBy(x => x.StartedAt).ThenBy(x => x.Id))
        {
            var ports = ReadArchivePorts(perRun[run.Id]);
            if (previous is not null)
                foreach (var (ip, now) in ports)
                {
                    if (!previous.TryGetValue(ip, out var before)) continue;
                    if (now.Except(before).Any()) counts[run.Id]++;
                    if (before.Except(now).Any()) counts[run.Id]++;
                }
            previous = ports;
        }

        if (runs.FirstOrDefault(x => !x.Cancelled) is { } latest)
            openPorts = perRun[latest.Id].Where(f => f.Kind == ScanCompare.OpenPortKind && f.Detail is not null)
                .Select(f => f.Ip + "|" + f.Detail).Distinct(StringComparer.Ordinal).Count();
        return counts;
    }

    private static Dictionary<string, HashSet<string>> ReadArchivePorts(IReadOnlyList<ArchivedFinding> findings)
    {
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var f in findings.Where(f => f.Kind == ScanCompare.PortScannedKind))
            result[f.Ip] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings.Where(f => f.Kind == ScanCompare.OpenPortKind && f.Detail is not null))
            if (result.TryGetValue(f.Ip, out var set)) set.Add(f.Detail!);
        return result;
    }
}
