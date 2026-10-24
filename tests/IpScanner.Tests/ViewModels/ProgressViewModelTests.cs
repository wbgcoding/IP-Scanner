using System;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ProgressViewModelTests
{
    [Fact]
    public void DeviceFractions_SumToScannedRatio()
    {
        var vm = new ProgressViewModel();
        vm.SetDevices(online: 10, offline: 40, total: 100);
        Assert.Equal(0.10, vm.OnlineFraction, 3);
        Assert.Equal(0.40, vm.OfflineFraction, 3);
    }

    [Fact]
    public void PingFractions_Computed()
    {
        var vm = new ProgressViewModel();
        vm.SetPings(success: 100, failed: 50, skipped: 50, total: 1000);
        Assert.Equal(0.10, vm.SuccessFraction, 3);
        Assert.Equal(0.05, vm.FailedFraction, 3);
        Assert.Equal(0.05, vm.SkippedFraction, 3);
    }

    [Fact]
    public void EtaFraction_IgnoresSkippedPings_SoAMostlySkippedScanKeepsARealEstimate()
    {
        // 1000 pings, 900 skipped, 10 of the 100 real ones done after 10 s: about 90 s to go.
        var fraction = ProgressViewModel.EtaFraction(success: 6, failed: 4, skipped: 900, total: 1000);
        Assert.Equal(0.1, fraction, 6);
        var remaining = ProgressViewModel.ComputeRemaining(fraction, TimeSpan.FromSeconds(10));
        Assert.NotNull(remaining);
        Assert.Equal(90.0, remaining!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void EtaFraction_WithoutSkippedPings_IsDoneOverTotal()
        => Assert.Equal(0.5, ProgressViewModel.EtaFraction(success: 40, failed: 10, skipped: 0, total: 100), 6);

    [Fact]
    public void ComputeRemaining_BelowFivePercent_ReturnsNull()
        => Assert.Null(ProgressViewModel.ComputeRemaining(0.049, TimeSpan.FromSeconds(10)));

    [Fact]
    public void ComputeRemaining_AtFiftyPercentAfterSixtySeconds_IsAboutSixtySeconds()
    {
        var remaining = ProgressViewModel.ComputeRemaining(0.5, TimeSpan.FromSeconds(60));
        Assert.NotNull(remaining);
        Assert.Equal(60.0, remaining!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void ComputeRemaining_NeverGoesNegative()
    {
        // fraction already past 100 % worth of elapsed time must clamp at zero, not go negative.
        var remaining = ProgressViewModel.ComputeRemaining(0.5, TimeSpan.FromSeconds(1));
        Assert.True(remaining!.Value.TotalSeconds >= 0);
    }

    [Fact]
    public void FormatEta_UnderAMinute_UsesSecondsOnlyFormat()
    {
        var text = ProgressViewModel.FormatEta(TimeSpan.FromSeconds(12));
        Assert.Contains("12", text);
        Assert.DoesNotContain("min", text);
    }

    [Fact]
    public void FormatEta_OverAMinute_UsesMinutesAndSecondsFormat()
    {
        var text = ProgressViewModel.FormatEta(TimeSpan.FromSeconds(130));
        Assert.Contains("2", text);
        Assert.Contains("min", text);
        Assert.Contains("10", text);
    }

    [Fact]
    public void FormatEta_ZeroOrBelow_ReturnsEmpty()
        => Assert.Equal("", ProgressViewModel.FormatEta(TimeSpan.Zero));

    [Fact]
    public void EtaText_BeforeScanStarted_IsEmpty()
    {
        var vm = new ProgressViewModel();
        vm.SetPings(1, 0, 0, 100);
        Assert.Equal("", vm.EtaText);
    }

    [Fact]
    public void EtaText_InfiniteMode_StaysEmptyEvenAfterScanStart()
    {
        var vm = new ProgressViewModel { InfinitePings = true };
        vm.StartScan();
        vm.SetPings(50, 0, 0, 100);
        Assert.Equal("", vm.EtaText);
    }

    [Fact]
    public void EndScan_ClearsTheEstimateAndKeepsItEmptyOnLaterUpdates()
    {
        var vm = new ProgressViewModel();
        vm.StartScan();
        System.Threading.Thread.Sleep(1200);
        vm.SetPings(10, 0, 0, 100);
        Assert.NotEqual("", vm.EtaText);

        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgressViewModel.EtaText)) raised++; };
        vm.EndScan();
        Assert.Equal("", vm.EtaText);
        Assert.Equal(1, raised);

        vm.SetPings(20, 0, 0, 100);
        Assert.Equal("", vm.EtaText);
    }

    [Fact]
    public void EtaText_BelowFivePercentAfterScanStart_IsEmpty()
    {
        var vm = new ProgressViewModel();
        vm.StartScan();
        vm.SetPings(1, 0, 0, 1000);   // 0.1 %
        Assert.Equal("", vm.EtaText);
    }
}
