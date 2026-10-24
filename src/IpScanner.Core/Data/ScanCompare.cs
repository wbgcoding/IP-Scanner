using System.Globalization;
using IpScanner.Core.Net;

namespace IpScanner.Core.Data;

public enum ScanChangeType { New, Gone, IpChanged, MacChanged, PortsOpened, PortsClosed, LatencyChanged }

/// <summary>One difference for one host. <see cref="Ip"/> is the address in the newer run (the older
/// one for a host that is gone); <see cref="Before"/> and <see cref="After"/> carry the changed value
/// as display text (old and new address, MAC, port list or latency).</summary>
public sealed record ScanChange(ScanChangeType Type, string Ip, string? Mac, string? Hostname,
    string? Before, string? After);

/// <summary>Latency counts as changed when the absolute difference is at least
/// <see cref="LatencyMinMs"/> and more than <see cref="LatencyPercent"/> percent of the older value.</summary>
public sealed record ScanCompareOptions(double LatencyPercent = 50, double LatencyMinMs = 10);

public sealed record ScanCompareResult(ArchivedScan A, ArchivedScan B, ScanCompareOptions Options,
    IReadOnlyList<ScanChange> Changes, bool PortsCompared);

/// <summary>Pure comparison of two stored scan runs: no scanning, no network traffic, no I/O.
/// A host is present in a run when it was online there. Hosts are paired by MAC first (so a changed
/// address is an IP change, not a gone plus a new host), then by IP (a different MAC behind the same
/// address is a MAC change). Ports are compared only for hosts that were port-scanned in both runs, and
/// never against a run that merges several (<c>SampleCount</c> above one).</summary>
public static class ScanCompare
{
    /// <summary>Archive finding kinds that carry port data next to the conflict findings.</summary>
    public const string PortScannedKind = "PortScanned";
    public const string OpenPortKind = "OpenPort";

    public static ScanCompareResult Compute(ScanArchiveDetail a, ScanArchiveDetail b, ScanCompareOptions? options = null)
    {
        options ??= new ScanCompareOptions();
        var devicesA = a.Devices.Where(d => d.Online).OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer).ToList();
        var devicesB = b.Devices.Where(d => d.Online).OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer).ToList();
        // A compacted day merges many runs: its port findings are unioned under old addresses while the
        // merged device keeps the latest one, so the comparison would invent closed ports. Skip it.
        var portsA = IsAggregated(a) ? [] : ReadPorts(a.Findings);
        var portsB = IsAggregated(b) ? [] : ReadPorts(b.Findings);

        var pairs = new List<(ArchivedDevice A, ArchivedDevice B)>();
        var usedB = new HashSet<ArchivedDevice>();
        var unmatchedA = new List<ArchivedDevice>();

        foreach (var da in devicesA)
        {
            ArchivedDevice? match = null;
            if (!string.IsNullOrEmpty(da.Mac))
            {
                var candidates = devicesB.Where(db => !usedB.Contains(db)
                    && string.Equals(db.Mac, da.Mac, StringComparison.OrdinalIgnoreCase)).ToList();
                match = candidates.FirstOrDefault(db => db.Ip == da.Ip) ?? candidates.FirstOrDefault();
            }
            if (match is null) unmatchedA.Add(da);
            else { usedB.Add(match); pairs.Add((da, match)); }
        }
        var goneCandidates = new List<ArchivedDevice>();
        foreach (var da in unmatchedA)
        {
            var match = devicesB.FirstOrDefault(db => !usedB.Contains(db) && db.Ip == da.Ip);
            if (match is null) goneCandidates.Add(da);
            else { usedB.Add(match); pairs.Add((da, match)); }
        }

        var changes = new List<ScanChange>();
        foreach (var da in goneCandidates)
            changes.Add(new ScanChange(ScanChangeType.Gone, da.Ip, da.Mac, da.Hostname, null, null));
        foreach (var db in devicesB.Where(db => !usedB.Contains(db)))
            changes.Add(new ScanChange(ScanChangeType.New, db.Ip, db.Mac, db.Hostname, null, null));

        var portsCompared = false;
        foreach (var (da, db) in pairs)
        {
            var host = db.Hostname ?? da.Hostname;
            var mac = db.Mac ?? da.Mac;
            if (da.Ip != db.Ip)
                changes.Add(new ScanChange(ScanChangeType.IpChanged, db.Ip, mac, host, da.Ip, db.Ip));
            if (!string.IsNullOrEmpty(da.Mac) && !string.IsNullOrEmpty(db.Mac)
                && !string.Equals(da.Mac, db.Mac, StringComparison.OrdinalIgnoreCase))
                changes.Add(new ScanChange(ScanChangeType.MacChanged, db.Ip, mac, host, da.Mac, db.Mac));

            if (portsA.TryGetValue(da.Ip, out var oldPorts) && portsB.TryGetValue(db.Ip, out var newPorts))
            {
                portsCompared = true;
                var opened = newPorts.Except(oldPorts).OrderBy(PortOrder).ToList();
                var closed = oldPorts.Except(newPorts).OrderBy(PortOrder).ToList();
                if (opened.Count > 0)
                    changes.Add(new ScanChange(ScanChangeType.PortsOpened, db.Ip, mac, host, null, string.Join(", ", opened)));
                if (closed.Count > 0)
                    changes.Add(new ScanChange(ScanChangeType.PortsClosed, db.Ip, mac, host, string.Join(", ", closed), null));
            }

            if (da.AvgMs is { } before && db.AvgMs is { } after && LatencyChanged(before, after, options))
                changes.Add(new ScanChange(ScanChangeType.LatencyChanged, db.Ip, mac, host,
                    FormatMs(before), FormatMs(after)));
        }

        var ordered = changes.OrderBy(c => c.Type).ThenBy(c => c.Ip, Net.IpAddressKey.TextComparer).ToList();
        return new ScanCompareResult(a.Scan, b.Scan, options, ordered, portsCompared);
    }

    /// <summary>True for a run that stands for several merged runs (see compaction in the archive).</summary>
    private static bool IsAggregated(ScanArchiveDetail run) => run.Scan.SampleCount > 1;

    internal static bool LatencyChanged(double before, double after, ScanCompareOptions options)
    {
        var delta = Math.Abs(after - before);
        if (delta < options.LatencyMinMs) return false;
        return delta > before * options.LatencyPercent / 100.0;
    }

    private static string FormatMs(double ms) => ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms";

    private static int PortOrder(string label)
    {
        var slash = label.IndexOf('/');
        return int.TryParse(slash > 0 ? label[..slash] : label, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : int.MaxValue;
    }

    /// <summary>Hosts that were port-scanned in the run, each with the set of open ports (label such as 443/TCP).</summary>
    private static Dictionary<string, HashSet<string>> ReadPorts(IReadOnlyList<ArchivedFinding> findings)
    {
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var f in findings.Where(f => f.Kind == PortScannedKind))
            result[f.Ip] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings.Where(f => f.Kind == OpenPortKind && f.Detail is not null))
            if (result.TryGetValue(f.Ip, out var set)) set.Add(f.Detail!);
        return result;
    }

    /// <summary>Findings for the archive: one marker per port-scanned host plus one entry per open port.</summary>
    public static IEnumerable<ArchivedFinding> PortFindings(IReadOnlyDictionary<string, IReadOnlyList<Scanner.NetService>> ports)
    {
        foreach (var (ip, services) in ports)
        {
            yield return new ArchivedFinding(ip, PortScannedKind, null);
            foreach (var label in services.Select(s => s.PortLabel).Distinct())
                yield return new ArchivedFinding(ip, OpenPortKind, label);
        }
    }
}
