using System;
using System.Collections.Generic;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

// The measurement reads the whole process heap, so no other test may allocate beside it.
[CollectionDefinition("ArchiveMemory", DisableParallelization = true)]
public class ArchiveMemoryCollection { }

[Collection("ArchiveMemory")]
public class ScanArchiveMemoryTests
{
    private static string DbPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");

    private static List<Device> Devices(int count)
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

    private static long PeakLiveBytesDuringStatistics(KnownDevicesDb db)
    {
        long start = GC.GetTotalMemory(true);
        long peak = start;
        int calls = 0;
        db.ArchiveStatisticsRunHook = () =>
        {
            if (calls++ % 50 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true));
        };
        db.GetScanArchiveStatistics();
        db.ArchiveStatisticsRunHook = null;
        return peak - start;
    }

    [Fact]
    public void ScanArchiveStatistics_ThousandRuns_KeepPeakMemoryNearTheSingleRunBaseline()
    {
        const string network = "AA:BB:CC:11:22:33";
        var at = new DateTimeOffset(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var devices = Devices(30);
        var single = new KnownDevicesDb(DbPath());
        single.SaveScanArchive(at, at.AddMinutes(1), network, new[] { "10.0.0.0/24" }, "{}", devices, Array.Empty<ConflictFinding>());
        var many = new KnownDevicesDb(DbPath());
        for (int i = 0; i < 1000; i++)
            many.SaveScanArchive(at.AddMinutes(i * 2), at.AddMinutes(i * 2 + 1), network, new[] { "10.0.0.0/24" }, "{}", devices, Array.Empty<ConflictFinding>());

        long baseline = PeakLiveBytesDuringStatistics(single);
        long peak = PeakLiveBytesDuringStatistics(many);

        Assert.Equal(1000, many.GetScanArchiveStatistics().RunCount);
        // Holding every run at once would keep about 30000 rows alive, tens of megabytes (the encrypted payload
        // plus the decoded device with its strings). The live-heap reading also moves with whatever other
        // threads of the test process retain meanwhile (late work of earlier tests, runner bookkeeping, JIT and
        // serializer caches), and that noise is not tied to the run count. A fixed 4 MB margin on top of the
        // single-run reading therefore stays far below the all-rows size and far above the noise.
        const long Margin = 4 * 1024 * 1024;
        Assert.True(peak < baseline + Margin, $"peak {peak} bytes, single-run baseline {baseline} bytes");
    }
}
