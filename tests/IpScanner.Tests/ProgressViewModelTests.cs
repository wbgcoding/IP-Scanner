using System.Collections.Generic;
using IpScanner.Core.Localization;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests;

public class ProgressViewModelTests
{
    [Fact]
    public void PingCountTip_IsNullWithoutSkippedPings_AndNamesTheCountOtherwise()
    {
        Loc.SetLanguage("en");
        var vm = new ProgressViewModel();
        vm.SetPings(10, 2, 0, 100);
        Assert.Null(vm.PingCountTip);

        vm.SetPings(10, 2, 5, 100);
        Assert.Contains("5", vm.PingCountTip);
    }

    [Fact]
    public void PingCountTip_RaisesWithThePingCounters()
    {
        var vm = new ProgressViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SetPings(1, 1, 1, 10);
        Assert.Contains(nameof(ProgressViewModel.PingCountTip), raised);
    }
}
