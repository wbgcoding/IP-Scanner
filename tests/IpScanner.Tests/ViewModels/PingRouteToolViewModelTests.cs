using System.Collections.Generic;
using System.Linq;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class PingRouteToolViewModelTests
{
    [Theory]
    [InlineData("1000", true, 1000)]
    [InlineData("100", true, 100)]
    [InlineData("10000", true, 10000)]
    [InlineData("99", false, 0)]
    [InlineData("10001", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    public void TryParseTimeout_AcceptsOnly100To10000(string text, bool expectedOk, int expectedMs)
    {
        var ok = PingToolViewModel.TryParseTimeout(text, out var ms);
        Assert.Equal(expectedOk, ok);
        if (expectedOk) Assert.Equal(expectedMs, ms);
    }

    [Fact]
    public void IdleLoss_ShowsAPlaceholderNotABlankValue()
    {
        var vm = new PingToolViewModel();
        Assert.Equal(PingToolViewModel.NoLoss, vm.LossText);
        Assert.Equal(PingToolViewModel.NoLoss, PingToolViewModel.FormatLoss(0, 0));
        Assert.Equal("25%", PingToolViewModel.FormatLoss(4, 3));
    }

    [Fact]
    public void Stats_UseTheScanTableLatencyNumberFormat()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            IpScanner.Core.Localization.Loc.SetLanguage("en");
            // min 0, avg (0+3+7)/3 = 3,3, max 7, last 7 -> one decimal like "0,5 ms" in the scan table.
            var text = PingToolViewModel.FormatStats(3, 0, 10, 7, 7);
            Assert.Equal("Min 0,0 / avg 3,3 / max 7,0 / last 7,0 ms", text);
            Assert.Equal("", PingToolViewModel.FormatStats(0, 0, 0, 0, 0));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void AddSample_UpdatesCountersStatsAndReplyListLive()
    {
        IpScanner.Core.Localization.Loc.SetLanguage("en");
        var vm = new PingToolViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.AddSample(12);
        Assert.Equal(1, vm.Sent);
        Assert.Equal(1, vm.Received);
        Assert.NotEqual("", vm.StatsText);
        Assert.Equal("0%", vm.LossText);
        Assert.Contains(nameof(PingToolViewModel.StatsText), raised);
        Assert.Contains(nameof(PingToolViewModel.Sent), raised);

        vm.AddSample(null);
        Assert.Equal(2, vm.Sent);
        Assert.Equal(1, vm.Received);
        Assert.Equal("50%", vm.LossText);
        Assert.True(vm.Replies[0].TimedOut);
        Assert.Equal("Timed out", vm.Replies[0].Result);
        Assert.False(vm.Replies[1].TimedOut);
    }

    [Fact]
    public void Replies_KeepTheLatest60WithNewestFirst()
    {
        var vm = new PingToolViewModel();
        for (int i = 1; i <= 75; i++) vm.AddSample(i);
        Assert.Equal(60, vm.Replies.Count);
        Assert.Contains("75", vm.Replies[0].Result);
        Assert.Contains("16", vm.Replies[59].Result);
    }

    [Fact]
    public void Graph_ProducesTimeTicksOnceEnoughSamplesExist()
    {
        var vm = new PingToolViewModel { Interval = "1" };
        for (int i = 0; i < 30; i++) vm.AddSample(10 + i % 5);
        Assert.NotEmpty(vm.GraphTicks);
    }

    [Fact]
    public void CanStart_FalseWhenTargetEmptyOrBusy()
    {
        var vm = new PingToolViewModel();
        Assert.False(vm.CanStart);
        vm.Target = "192.168.1.1";
        Assert.True(vm.CanStart);
        vm.IsRunning = true;
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void Hop_DisplayLine_ShowsAddressNameAndTimes()
    {
        var hop = new RouteToolViewModel.Hop { Number = 3, Address = "10.0.0.1", Name = "gateway.local" };
        hop.Times.Add("2 ms");
        hop.Times.Add("3 ms");
        Assert.Contains("10.0.0.1", hop.DisplayLine);
        Assert.Contains("gateway.local", hop.DisplayLine);
        Assert.Contains("2 ms", hop.DisplayLine);
    }

    [Fact]
    public void Hop_DisplayLine_ShowsAsteriskWhenUnanswered()
    {
        var hop = new RouteToolViewModel.Hop { Number = 5 };
        hop.Times.Add("*");
        Assert.Contains("*", hop.DisplayLine);
    }

    [Fact]
    public void Hop_RaisesDisplayLineWhenAddressNameOrTimesChange()
    {
        var hop = new RouteToolViewModel.Hop { Number = 1 };
        var raised = new List<string?>();
        hop.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        hop.Address = "10.0.0.1";
        hop.Name = "router";
        hop.Times.Add("3 ms");
        Assert.Equal(3, raised.Count(n => n == "DisplayLine"));
    }

    [Fact]
    public void RouteCanStart_FalseWhenTargetEmptyOrBusy()
    {
        var vm = new RouteToolViewModel();
        Assert.False(vm.CanStart);
        vm.Target = "example.com";
        Assert.True(vm.CanStart);
        vm.IsRunning = true;
        Assert.False(vm.CanStart);
    }
}
