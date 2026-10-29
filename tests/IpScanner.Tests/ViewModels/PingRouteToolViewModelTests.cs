using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.Core.Localization;
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
        var t0 = new System.DateTime(2026, 1, 1, 10, 0, 0);
        for (int i = 0; i < 30; i++) vm.AddSample(10 + i % 5, t0.AddSeconds(i));
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
    public void Hop_DisplayLine_ShowsAddressNameAndStatistics()
    {
        var hop = new RouteToolViewModel.Hop { Number = 3, Address = "10.0.0.1", Name = "gateway.local" };
        hop.Record(2);
        hop.Record(3);
        Assert.Contains("10.0.0.1", hop.DisplayLine);
        Assert.Contains("gateway.local", hop.DisplayLine);
        Assert.Contains(NumberFormat.Ms(2), hop.DisplayLine);
        Assert.Contains(NumberFormat.Ms(2.5), hop.DisplayLine);
    }

    [Fact]
    public void Hop_DisplayLine_ShowsAsteriskWhenUnanswered()
    {
        var hop = new RouteToolViewModel.Hop { Number = 5 };
        hop.Record(null);
        Assert.Contains("*", hop.DisplayLine);
        Assert.Equal(NumberFormat.Ms(null), hop.LastText);
        Assert.Equal(NumberFormat.Ms(null), hop.WorstText);
        Assert.Equal("100%", hop.LossText);
    }

    [Fact]
    public void Hop_RaisesDisplayLineWhenAddressNameOrProbeChange()
    {
        var hop = new RouteToolViewModel.Hop { Number = 1 };
        var raised = new List<string?>();
        hop.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        hop.Address = "10.0.0.1";
        hop.Name = "router";
        hop.Record(3);
        Assert.Equal(3, raised.Count(n => n == "DisplayLine"));
    }

    [Fact]
    public void Hop_Record_KeepsLossLastAverageBestAndWorst()
    {
        var hop = new RouteToolViewModel.Hop { Number = 2 };
        hop.Record(10, "10.0.0.2");
        hop.Record(null, "10.0.0.99");   // a lost probe never changes the address or the times
        hop.Record(30, "10.0.0.77");     // the address stays the first one seen
        hop.Record(20);
        Assert.Equal("10.0.0.2", hop.Address);
        Assert.Equal(4, hop.Sent);
        Assert.Equal(3, hop.Received);
        Assert.Equal("25%", hop.LossText);
        Assert.Equal(NumberFormat.Ms(20), hop.LastText);
        Assert.Equal(NumberFormat.Ms(20), hop.AvgText);
        Assert.Equal(NumberFormat.Ms(10), hop.BestText);
        Assert.Equal(NumberFormat.Ms(30), hop.WorstText);
    }

    /// <summary>A scripted route: ttl 1 and 2 answer as routers, ttl 3 is the target; a script can override any probe.</summary>
    private sealed class FakeProber : IHopProber
    {
        public static readonly IPAddress Target = IPAddress.Parse("10.0.0.9");
        private readonly object _gate = new();
        private readonly Dictionary<int, int> _calls = new();
        private readonly Dictionary<string, int> _names = new();

        public IPAddress? Resolved { get; set; } = Target;
        public Func<int, int, HopProbe>? Script { get; set; }
        public Action<int, int>? OnProbe { get; set; }

        public int Calls(int ttl) { lock (_gate) return _calls.GetValueOrDefault(ttl); }
        public int TotalCalls { get { lock (_gate) return _calls.Values.Sum(); } }
        public int NameLookups(string address) { lock (_gate) return _names.GetValueOrDefault(address); }

        public Task<IPAddress?> ResolveAsync(string target, CancellationToken ct) => Task.FromResult(Resolved);

        public Task<HopProbe> ProbeAsync(IPAddress target, int ttl, int timeoutMs, CancellationToken ct)
        {
            int n;
            lock (_gate) { n = _calls[ttl] = _calls.GetValueOrDefault(ttl) + 1; }
            OnProbe?.Invoke(ttl, n);
            ct.ThrowIfCancellationRequested();
            var result = Script?.Invoke(ttl, n) ?? Default(ttl);
            return Task.FromResult(result);
        }

        private static HopProbe Default(int ttl)
            => ttl < 3 ? new HopProbe(true, "10.0.0." + ttl, ttl * 10) : new HopProbe(true, Target.ToString(), 30);

        public Task<string?> ReverseNameAsync(string address, CancellationToken ct)
        {
            lock (_gate) _names[address] = _names.GetValueOrDefault(address) + 1;
            return Task.FromResult<string?>("host-" + address);
        }
    }

    private static RouteToolViewModel NewRoute(FakeProber prober, bool continuous = false)
    {
        Loc.SetLanguage("en");
        return new RouteToolViewModel(prober, TimeSpan.Zero) { Target = "example.test", Continuous = continuous };
    }

    [Fact]
    public async Task Route_SinglePass_ProbesEachHopThreeTimesAndStopsAtTheTarget()
    {
        var fake = new FakeProber();
        var vm = NewRoute(fake);
        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.Equal(3, vm.Hops.Count);
        Assert.All(vm.Hops, h => Assert.Equal(3, h.Sent));
        Assert.Equal(9, fake.TotalCalls);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.9" }, vm.Hops.Select(h => h.Address).ToArray());
        Assert.Equal("host-10.0.0.1", vm.Hops[0].Name);
        Assert.Equal(1, fake.NameLookups("10.0.0.1"));
        Assert.Equal(NumberFormat.Ms(20), vm.Hops[1].AvgText);
        Assert.Equal("0%", vm.Hops[0].LossText);
    }

    [Fact]
    public async Task Route_Continuous_ReprobesEveryKnownHopAndKeepsTheStatistics()
    {
        var fake = new FakeProber();
        const int rounds = 4;
        var vm = NewRoute(fake, continuous: true);
        fake.Script = (ttl, n) =>
        {
            if (ttl == 1 && n > 3)
                return n == 5 ? HopProbe.Lost : new HopProbe(true, "10.0.0.1", n switch { 4 => 20, 6 => 30, _ => 10 });
            return new HopProbe(true, ttl < 3 ? "10.0.0." + ttl : "10.0.0.9", ttl == 1 ? 10 : ttl * 10);
        };
        // The last probe of the last round switches the option off; that round still completes.
        fake.OnProbe = (ttl, n) => { if (ttl == 3 && n == 3 + rounds) vm.Continuous = false; };

        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.Equal(3, vm.Hops.Count);
        Assert.Equal(0, fake.Calls(4));
        Assert.Equal(3 * (3 + rounds), fake.TotalCalls);
        var first = vm.Hops[0];
        Assert.Equal(7, first.Sent);
        Assert.Equal(6, first.Received);
        Assert.Equal(PingToolViewModel.FormatLoss(7, 6), first.LossText);
        Assert.Equal(NumberFormat.Ms(10), first.LastText);
        Assert.Equal(NumberFormat.Ms(15), first.AvgText);
        Assert.Equal(NumberFormat.Ms(10), first.BestText);
        Assert.Equal(NumberFormat.Ms(30), first.WorstText);
        Assert.All(vm.Hops, h => Assert.Equal(1, fake.NameLookups(h.Address!)));
    }

    [Fact]
    public async Task Route_Stop_EndsTheRunInTheMiddleOfARound()
    {
        var fake = new FakeProber();
        var vm = NewRoute(fake, continuous: true);
        fake.OnProbe = (ttl, n) => { if (ttl == 2 && n == 5) vm.Stop(); };

        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanStart);
        Assert.Equal(4, vm.Hops[0].Sent);   // the first pass plus one finished round; the interrupted round counts nothing
        Assert.Equal(4, vm.Hops[1].Sent);
    }

    [Fact]
    public async Task Route_UnansweredHop_ShowsAsteriskAndFullLossButIsStillReprobed()
    {
        var fake = new FakeProber();
        var vm = NewRoute(fake, continuous: true);
        fake.Script = (ttl, _) => ttl == 2 ? HopProbe.Lost : new HopProbe(true, ttl == 1 ? "10.0.0.1" : "10.0.0.9", 5);
        fake.OnProbe = (ttl, n) => { if (ttl == 3 && n == 5) vm.Continuous = false; };

        await vm.StartAsync();

        var silent = vm.Hops[1];
        Assert.Null(silent.Address);
        Assert.Equal(5, silent.Sent);
        Assert.Equal(0, silent.Received);
        Assert.Equal("100%", silent.LossText);
        Assert.Equal(NumberFormat.Ms(null), silent.BestText);
        Assert.Equal(0, fake.NameLookups(""));
        Assert.Equal(5, fake.Calls(2));
    }

    [Fact]
    public async Task Route_TargetThatNeverAnswers_StopsAtThirtyHops()
    {
        var fake = new FakeProber { Script = (_, _) => HopProbe.Lost };
        var vm = NewRoute(fake);
        await vm.StartAsync();
        Assert.Equal(30, vm.Hops.Count);
        Assert.All(vm.Hops, h => Assert.Equal(3, h.Sent));
    }

    [Fact]
    public async Task Route_UnresolvableTarget_SetsTheStatusAndTracesNothing()
    {
        var fake = new FakeProber { Resolved = null };
        var vm = NewRoute(fake);
        await vm.StartAsync();
        Assert.Equal(Loc.DnsNotFound, vm.Status);
        Assert.Empty(vm.Hops);
        Assert.Equal(0, fake.TotalCalls);
        Assert.False(vm.IsRunning);
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

    private static readonly System.DateTime T0 = new(2026, 1, 1, 10, 0, 0);

    private static PingToolViewModel Filled(int count, System.Func<int, double?> rtt)
    {
        IpScanner.Core.Localization.Loc.SetLanguage("en");
        var vm = new PingToolViewModel();
        for (int i = 0; i < count; i++) vm.AddSample(rtt(i), T0.AddSeconds(i));
        return vm;
    }

    [Fact]
    public void Session_KeepsEverySampleWhileTheReplyListKeeps60()
    {
        var vm = Filled(200, i => i + 1);
        Assert.Equal(60, vm.Replies.Count);
        var rows = vm.BuildCsv(',').Split("\r\n", System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(201, rows.Length);
        Assert.Equal("time,ms,status", rows[0]);
        Assert.Equal("2026-01-01 10:00:00,1,ok", rows[1]);
    }

    [Fact]
    public void Session_IsCappedAtADayOfSamples()
    {
        var vm = Filled(PingToolViewModel.MaxSessionSamples + 100, i => i % 9 + 1);
        var rows = vm.BuildCsv(',').Split("\r\n", System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(PingToolViewModel.MaxSessionSamples + 1, rows.Length);
        // The oldest 100 probes dropped out: the first row is the 101st probe.
        Assert.StartsWith(T0.AddSeconds(100).ToString("yyyy-MM-dd HH:mm:ss") + ",", rows[1]);
    }

    [Fact]
    public void Csv_HasTimeMsAndStatus_LostProbesLeaveMsEmpty()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            var vm = Filled(3, i => i == 1 ? null : 12.5);
            var rows = vm.BuildCsv(';').Split("\r\n", System.StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[]
            {
                "time;ms;status",
                "2026-01-01 10:00:00;12.5;ok",
                "2026-01-01 10:00:01;;timeout",
                "2026-01-01 10:00:02;12.5;ok",
            }, rows);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Csv_NumbersFollowTheCultureUnlessTheDelimiterIsAComma()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var vm = Filled(1, _ => 12.5);
            Assert.Contains("12,5;ok", vm.BuildCsv(';'));
            Assert.Contains("12.5,ok", vm.BuildCsv(','));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void RefreshLanguage_RelabelsTheZoomChoicesAndKeepsTheSpan()
    {
        IpScanner.Core.Localization.Loc.SetLanguage("de");
        try
        {
            var vm = new PingToolViewModel();
            vm.Zoom = vm.ZoomOptions[1];
            IpScanner.Core.Localization.Loc.SetLanguage("en");
            vm.RefreshLanguage();
            Assert.Equal(IpScanner.Core.Localization.Loc.PingZoom1Min, vm.ZoomOptions[0].Label);
            Assert.Equal("1 min", vm.ZoomOptions[0].Label);
            Assert.Equal(300, vm.Zoom.Seconds);
            Assert.Same(vm.ZoomOptions[1], vm.Zoom);
        }
        finally { IpScanner.Core.Localization.Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Outages_CountRunsOfLostProbes()
    {
        var vm = Filled(7, i => i is 0 or 1 or 4 or 5 ? null : 5);
        Assert.Equal(2, vm.Outages);
        Assert.EndsWith("Outages: 2", vm.StatsText);
        Assert.Equal("Outages: 0", PingToolViewModel.FormatStatsLine(1, 1, 5, 5, 5, 5, 0).Split("  |  ")[^1]);
        Assert.Equal("Outages: 1", PingToolViewModel.FormatStatsLine(1, 0, 0, 0, 0, 0, 1));
        Assert.Equal("", PingToolViewModel.FormatStatsLine(0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Zoom_OffersFourChoicesAndDefaultsToOneMinute()
    {
        IpScanner.Core.Localization.Loc.SetLanguage("en");
        var vm = new PingToolViewModel();
        Assert.Equal(new[] { "1 min", "5 min", "1 h", "All" }, vm.ZoomOptions.Select(z => z.Label).ToArray());
        Assert.Equal(new[] { 60, 300, 3600, 0 }, vm.ZoomOptions.Select(z => z.Seconds).ToArray());
        Assert.Same(vm.ZoomOptions[0], vm.Zoom);
    }

    [Fact]
    public void Zoom_ChangesHowMuchOfTheSessionTheGraphShows()
    {
        var vm = Filled(600, i => i % 7 + 1);
        Assert.Equal(61, vm.Graph!.Points.Count);
        vm.Zoom = vm.ZoomOptions[1];
        Assert.Equal(301, vm.Graph!.Points.Count);
        vm.Zoom = vm.ZoomOptions[3];
        Assert.Equal(600, vm.Graph!.Points.Count);
        vm.Zoom = vm.ZoomOptions[2];
        Assert.Equal(600, vm.Graph!.Points.Count);
    }

    [Fact]
    public void Zoom_RaisesGraphLineWhenChanged()
    {
        var vm = Filled(200, i => i % 7 + 1);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        vm.Zoom = vm.ZoomOptions[3];
        Assert.Contains(nameof(PingToolViewModel.GraphLine), raised);
    }

    [Fact]
    public void Thin_KeepsTheSlowestReplyOfEachGroup()
    {
        var all = new List<PingSample>();
        for (int i = 0; i < 5000; i++) all.Add(new PingSample(T0.AddSeconds(i), i is >= 10 and < 15 ? null : i == 7 ? 900 : 5));
        var thin = PingToolViewModel.Thin(all, 0, out int perPoint);
        Assert.Equal(5, perPoint);
        Assert.Equal(1000, thin.Count);
        Assert.Equal(900, thin[1]);
        Assert.Null(thin[2]);
        var short1 = PingToolViewModel.Thin(all, 4990, out int one);
        Assert.Equal(1, one);
        Assert.Equal(10, short1.Count);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(60, 15)]
    [InlineData(300, 60)]
    [InlineData(3600, 900)]
    [InlineData(86400, 14400)]
    [InlineData(1_000_000, 86400)]
    public void TickStep_LeavesAboutEightLabels(double span, int expected)
        => Assert.Equal(expected, PingToolViewModel.TickStep(span));

    [Fact]
    public async System.Threading.Tasks.Task Save_WritesTheSessionAsUtf8CsvWithTheCultureSeparator()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ipscan-pingsave-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var vm = Filled(2, i => i == 0 ? 3.5 : null);
            Assert.True(vm.CanSave);
            var path = System.IO.Path.Combine(dir, "sub", "ping.csv");
            Assert.True(await vm.SaveAsync(path));
            var bytes = System.IO.File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.StartsWith("time;ms;status\r\n2026-01-01 10:00:00;3,5;ok\r\n", text);
            Assert.Contains("Saved", vm.Status);
            Assert.True(vm.CanSave);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Save_WithoutSamplesOrOnAnInvalidPath_ReturnsFalse()
    {
        IpScanner.Core.Localization.Loc.SetLanguage("en");
        var empty = new PingToolViewModel();
        Assert.False(empty.CanSave);
        Assert.False(await empty.SaveAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "never-written.csv")));

        var vm = Filled(1, _ => 5);
        Assert.False(await vm.SaveAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bad\0name.csv")));
        Assert.Contains("Saving failed", vm.Status);
        Assert.True(vm.CanSave);
    }
}
