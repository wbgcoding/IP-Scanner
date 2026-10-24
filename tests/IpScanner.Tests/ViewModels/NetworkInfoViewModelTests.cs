using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class NetworkInfoViewModelTests
{
    // The sidebar network graph must follow the resizable sidebar column: the plotted
    // width tracks whatever the canvas SizeChanged handler reports.
    [Fact]
    public void SetGraphWidth_RedrawsAtTheNewWidth()
    {
        var vm = new NetworkInfoViewModel(1, new NetworkInfo(), "#888888");
        for (int i = 0; i < 10; i++) { vm.SetAvg(10 + i); vm.AddGraphSample(capacity: 60, tickIntervalSeconds: 10); }

        vm.SetGraphWidth(240);
        Assert.InRange(vm.GraphPoints.Last().X, 235, 240);

        vm.SetGraphWidth(560);
        Assert.InRange(vm.GraphPoints.Last().X, 555, 560);
    }

    [Fact]
    public async Task AddGraphSample_ConcurrentWithZoomToggle_DoesNotThrow()
    {
        // AddGraphSample runs on the graph-timer worker thread while a
        // 1m/30s zoom click runs Render() synchronously on the UI thread - both
        // read/write the same sample list. Before the _graphLock fix this could
        // throw (list mutated during enumeration/GetRange) or corrupt state.
        // A tight concurrent hammer is the best available proof for a race -
        // it can't guarantee reproduction, but it reliably caught the bug
        // pre-fix across repeated local runs.
        var vm = new NetworkInfoViewModel(1, new NetworkInfo(), "#888888");

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                vm.AddGraphSample(capacity: 60, tickIntervalSeconds: 10);
        });
        var toggler = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                vm.IsZoomed = i % 2 == 0;
        });

        await Task.WhenAll(writer, toggler);   // must complete without throwing
        Assert.True(true);
    }

    private static IpScanner.Core.Models.OwnIpv6Address Own(string text)
        => new("Ethernet", 1, IpScanner.Core.Net.Ipv6Address.Parse(text), 64);

    [Fact]
    public void BuildIpv6Lines_OrdersGlobalThenUlaThenLinkLocal_AndDropsZones()
    {
        var lines = NetworkInfoViewModel.BuildIpv6Lines(new[]
        {
            Own("fe80::1%12"), Own("fd00:1234::7"), Own("2a00:1450::9"),
        });
        Assert.Equal(new[] { "2a00:1450::9", "fd00:1234::7", "fe80::1" }, lines.Select(l => l.Address));
        Assert.Equal(IpScanner.Core.Localization.Loc.Ipv6KindGlobal, lines[0].Kind);
        Assert.Equal(IpScanner.Core.Localization.Loc.Ipv6KindUla, lines[1].Kind);
        Assert.Equal(IpScanner.Core.Localization.Loc.Ipv6KindLinkLocal, lines[2].Kind);
        Assert.All(lines, l => Assert.NotEmpty(l.Tip));
    }

    [Fact]
    public void BuildIpv6Lines_CollapsesTheRestIntoACountLine()
    {
        var many = Enumerable.Range(1, 7).Select(i => Own("2a00:1450::" + i)).ToList();
        var lines = NetworkInfoViewModel.BuildIpv6Lines(many);
        Assert.Equal(NetworkInfoViewModel.MaxIpv6Lines + 1, lines.Count);
        Assert.Equal("+3", lines[^1].Kind);
        Assert.Equal("", lines[^1].Address);
    }

    [Fact]
    public void ApplyInfo_ShowsIpv6OnlyWhenTheCardHasAddresses()
    {
        var vm = new NetworkInfoViewModel(1, new NetworkInfo(), "#888888");
        Assert.False(vm.HasIpv6);
        var info = new NetworkInfo { Ipv6 = new() { Own("2a00:1450::9") } };
        vm.ApplyInfo("Net", info, primary: true);
        Assert.True(vm.HasIpv6);
        Assert.Single(vm.Ipv6Lines);
    }
}
