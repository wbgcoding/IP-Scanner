using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ProgressViewModelTests
{
    [Fact]
    public void DeviceFractions_SumToScannedRatio()
    {
        var vm = new ProgressViewModel();
        vm.SetDevices(online: 10, offline: 40, unknown: 0, total: 100);
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
}
