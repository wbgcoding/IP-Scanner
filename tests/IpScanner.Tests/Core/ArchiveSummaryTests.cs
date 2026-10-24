using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ArchiveSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Subnet = ["192.0.2.0/24"];

    private static KnownDevicesDb NewDb() =>
        new(Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db"));

    private static Device Dev(int n, double? ms, int fails = 0, int successes = 2)
    {
        var d = new Device($"192.0.2.{n}") { Mac = $"AA:00:00:00:00:{n:X2}" };
        if (ms is not null)
            for (var i = 0; i < successes; i++) d.RecordPing(new PingResult(true, ms.Value, 64));
        for (var i = 0; i < fails; i++) d.RecordPing(new PingResult(false, 0, 0));
        return d;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<NetService>> Ports(
        string ip, params int[] open) =>
        new Dictionary<string, IReadOnlyList<NetService>>
        {
            [ip] = open.Select(p => new NetService("svc", p, NetProtocol.Tcp)).ToArray()
        };

    private static long Save(KnownDevicesDb db, DateTimeOffset at, IReadOnlyList<Device> devices,
        IReadOnlyList<ConflictFinding>? findings = null, bool cancelled = false,
        IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null, int seconds = 60) =>
        db.SaveScanArchive(at, at.AddSeconds(seconds), "192.0.2.0/24", Subnet, "{}", devices,
            findings ?? Array.Empty<ConflictFinding>(), cancelled, ports);

    [Fact]
    public void Summary_OfEmptyArchive_IsEmpty()
    {
        var summary = NewDb().GetScanArchiveSummary();
        Assert.Equal(0, summary.RunCount);
        Assert.Empty(summary.Runs);
        Assert.Null(summary.FirstAt);
    }

    [Fact]
    public void Summary_AggregatesFiveRuns()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10), Dev(2, 20)], seconds: 30);
        Save(db, T0.AddHours(1), [Dev(1, 10), Dev(2, 20), Dev(3, 30, fails: 2)], seconds: 60);
        Save(db, T0.AddHours(2), [Dev(1, 10), Dev(2, 20)], cancelled: true, seconds: 5);
        Save(db, T0.AddHours(3), [Dev(1, 10), Dev(2, 20), Dev(3, 30), Dev(4, 40)], seconds: 90);
        Save(db, T0.AddHours(4), [Dev(1, 10), Dev(2, 20), Dev(4, 40, successes: 0, fails: 2)], seconds: 120);

        var s = db.GetScanArchiveSummary();

        Assert.Equal(5, s.RunCount);
        Assert.Equal(1, s.CancelledCount);
        Assert.Equal(T0, s.FirstAt);
        Assert.Equal(T0.AddHours(4), s.LastAt);
        Assert.Equal(4, s.DistinctDevices);
        Assert.Equal(2, s.NewDevicesInRange);
        // Finished runs: 2, 3, 4, 2 online -> 2.75; the newest run has two of three online.
        Assert.Equal(2.75, s.AvgOnlinePerRun, 3);
        Assert.Equal(2, s.LastRunOnline);
        Assert.Equal(3, s.LastRunDevices);
        // Duration averages the four finished runs (30, 60, 90, 120).
        Assert.Equal(75, s.AvgDurationSeconds, 3);
        Assert.Equal(5, s.Runs.Count);
        Assert.True(s.Runs.Zip(s.Runs.Skip(1), (a, b) => a.StartedAt < b.StartedAt).All(x => x));
        Assert.True(s.Runs[2].Cancelled);
    }

    [Fact]
    public void Summary_PingFiguresMatchTheStatistics()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10, successes: 4), Dev(2, 30, fails: 2, successes: 2)]);
        Save(db, T0.AddHours(1), [Dev(1, 10, successes: 4), Dev(2, 30, fails: 2, successes: 2)]);

        var s = db.GetScanArchiveSummary();

        // Per run: 4 + 2 successes and 2 failures, twice.
        Assert.Equal(16, s.PingTotal);
        Assert.Equal(75, s.AvailabilityPercent, 3);
        Assert.Equal(25, s.LossPercent, 3);
        // Latency is weighted by successful pings: (4 * 10 + 2 * 30) / 6 per run.
        Assert.Equal(100.0 / 6, s.AvgLatencyMs!.Value, 3);
        Assert.Equal(10, s.MinLatencyMs);
        Assert.Equal(30, s.MaxLatencyMs);
        Assert.NotNull(s.AvgJitterMs);
    }

    [Fact]
    public void Summary_WithoutSuccessfulPings_HasNoLatency()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, null, fails: 3)]);
        var s = db.GetScanArchiveSummary();
        Assert.Equal(3, s.PingTotal);
        Assert.Equal(0, s.AvailabilityPercent);
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AvgLatencyMs);
        Assert.Null(s.MinLatencyMs);
        Assert.Null(s.MaxLatencyMs);
        Assert.Null(s.AvgJitterMs);
    }

    [Fact]
    public void Summary_CountsConflictsAndPortChanges()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10), Dev(2, 10)], ports: Ports("192.0.2.1", 22, 80));
        Save(db, T0.AddHours(1), [Dev(1, 10), Dev(2, 10)],
            [new ConflictFinding("192.0.2.2", "AA:00:00:00:00:02", ConflictKind.MacChanged, "BB:00:00:00:00:02")],
            ports: Ports("192.0.2.1", 22, 443));
        Save(db, T0.AddHours(2), [Dev(1, 10), Dev(2, 10)], ports: Ports("192.0.2.1", 22, 443, 8080));

        var s = db.GetScanArchiveSummary();

        // Run 2: one conflict plus opened and closed ports; run 3: only opened ports.
        Assert.Equal(new[] { 0, 3, 1 }, s.Runs.Select(x => x.FindingCount).ToArray());
        Assert.Equal(4, s.FindingCount);
        Assert.Equal(3, s.OpenPortCount);
    }

    [Fact]
    public void Summary_OpenPortsComeFromTheNewestFinishedRun()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10)], ports: Ports("192.0.2.1", 22, 80));
        Save(db, T0.AddHours(1), [Dev(1, 10)], ports: Ports("192.0.2.1", 22));
        Save(db, T0.AddHours(2), [Dev(1, 10)], cancelled: true, ports: Ports("192.0.2.1", 1, 2, 3, 4));
        Assert.Equal(1, db.GetScanArchiveSummary().OpenPortCount);
    }

    [Fact]
    public void Summary_NewDevicesUseTheHistoryBeforeTheRange()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10)]);
        Save(db, T0.AddDays(1), [Dev(1, 10), Dev(2, 10)]);
        Save(db, T0.AddDays(2), [Dev(1, 10), Dev(2, 10), Dev(3, 10)]);

        var all = db.GetScanArchiveSummary();
        Assert.Equal(2, all.NewDevicesInRange);

        var recent = db.GetScanArchiveSummary(T0.AddDays(1).AddHours(-1), null);
        Assert.Equal(3, recent.RunCount - 0 + 1);
        Assert.Equal(2, recent.RunCount);
        Assert.Equal(3, recent.DistinctDevices);
        Assert.Equal(2, recent.NewDevicesInRange);

        var last = db.GetScanArchiveSummary(T0.AddDays(2).AddHours(-1), null);
        Assert.Equal(1, last.RunCount);
        Assert.Equal(1, last.NewDevicesInRange);
    }

    [Fact]
    public void Summary_WithPassedStatistics_MatchesTheOwnPass()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10), Dev(2, 20)]);
        Save(db, T0.AddHours(1), [Dev(1, 12), Dev(3, 20)]);
        var own = db.GetScanArchiveSummary();
        var shared = db.GetScanArchiveSummary(statistics: db.GetScanArchiveStatistics());
        Assert.Equal(own with { Runs = [] }, shared with { Runs = [] });
        Assert.Equal(own.Runs, shared.Runs);
    }

    [Fact]
    public void Summary_CompactedRunsKeepTheirPingCounts()
    {
        var db = NewDb();
        Save(db, T0, [Dev(1, 10, fails: 2)]);
        Save(db, T0.AddHours(1), [Dev(1, 10, fails: 2)]);
        Save(db, T0.AddDays(40), [Dev(1, 10)]);
        var before = db.GetScanArchiveSummary();
        Assert.Equal(1, db.CompactScanArchiveOlderThan(30, T0.AddDays(41)));

        var after = db.GetScanArchiveSummary();
        Assert.Equal(before.PingTotal, after.PingTotal);
        Assert.Equal(before.AvailabilityPercent, after.AvailabilityPercent, 3);
        Assert.Equal(2, after.RunCount);
        // The merged run stands for two runs and is left out of the duration average.
        Assert.Equal(60, after.AvgDurationSeconds, 3);
    }
}
