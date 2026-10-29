using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Routine events reach the log only while the verbose switch is on.</summary>
[Collection("LogFile")]
public class LogEventsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipscanner-logev-" + Guid.NewGuid().ToString("N"));
    private readonly string _origPath = Log.FilePath;
    private readonly Func<bool> _origVerbose = Log.VerboseProvider;

    public LogEventsTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FilePath = Path.Combine(_dir, "error.log");
        Log.ClearBuffer();
    }

    public void Dispose()
    {
        Log.FilePath = _origPath;
        Log.VerboseProvider = _origVerbose;
        Log.ClearBuffer();
        try { Directory.Delete(_dir, true); } catch { /* temp folder */ }
    }

    private static PingResult OnlyDotOne(string ip, int _) =>
        ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

    private static async Task RunScanAsync()
    {
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        await vm.RunScanAsync(new[] { "10.0.0" });
    }

    [Fact]
    public async Task VerboseOn_ScanWritesStartAndEndInfo()
    {
        Log.VerboseProvider = () => true;
        await RunScanAsync();

        var scan = Log.Snapshot().Where(e => e.Area == LogArea.Scan && e.Level == LogLevel.Info).Select(e => e.Message).ToList();
        var start = scan.FindIndex(m => m.StartsWith("Scan started:"));
        var end = scan.FindIndex(m => m.StartsWith("Scan ended:"));
        Assert.True(start >= 0 && end > start);
        Assert.Contains("1 online", scan[end]);
        Assert.Contains("cancelled: no", scan[end]);
    }

    [Fact]
    public async Task VerboseOff_ScanWritesNoInfoOrDebug()
    {
        Log.VerboseProvider = () => false;
        await RunScanAsync();

        Assert.DoesNotContain(Log.Snapshot(), e => e.Level >= LogLevel.Info);
        Assert.False(File.Exists(Log.FilePath) && File.ReadAllText(Log.FilePath).Contains("Scan started"));
    }

    [Fact]
    public async Task ScanEvents_CarryNoDeviceAddresses()
    {
        Log.VerboseProvider = () => true;
        await RunScanAsync();

        Assert.DoesNotContain(Log.Snapshot(), e => e.Message.Contains("10.0.0."));
    }
}
