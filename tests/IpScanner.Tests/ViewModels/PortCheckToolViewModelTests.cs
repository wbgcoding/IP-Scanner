using System.Linq;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

[Collection("PortScannerGate")]
public class PortCheckToolViewModelTests
{
    [Fact]
    public void BuildPortSets_Top1000_ReturnsTheMostFrequentPortsFromTheTable()
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeTop1000 };
        var (tcp, udp, error) = vm.BuildPortSets();
        Assert.Null(error);
        Assert.Equal(1000, tcp.Count);
        Assert.Contains(80, tcp);
        Assert.Contains(443, tcp);
        Assert.Contains(53, udp);
        Assert.Equal(PortFrequency.Top(1000, NetProtocol.Tcp).OrderBy(p => p), tcp.OrderBy(p => p));
    }

    [Fact]
    public void BuildPortSets_ProtocolUnchecked_OmitsThatProtocolsPorts()
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeTop1000, Udp = false };
        var (tcp, udp, error) = vm.BuildPortSets();
        Assert.Null(error);
        Assert.Equal(1000, tcp.Count);
        Assert.Empty(udp);
    }

    [Fact]
    public void BuildPortSets_Range_BuildsInclusivePortList()
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeRange, RangeFrom = "20", RangeTo = "25" };
        var (tcp, _, error) = vm.BuildPortSets();
        Assert.Null(error);
        Assert.Equal(new[] { 20, 21, 22, 23, 24, 25 }, tcp.OrderBy(p => p));
    }

    [Theory]
    [InlineData("0", "10")]
    [InlineData("10", "5")]
    [InlineData("abc", "10")]
    [InlineData("1", "70000")]
    public void BuildPortSets_Range_InvalidBoundsReturnsError(string from, string to)
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeRange, RangeFrom = from, RangeTo = to };
        var (_, _, error) = vm.BuildPortSets();
        Assert.Equal(Loc.PortCheckInvalidRange, error);
    }

    [Fact]
    public void BuildPortSets_List_ParsesCommaSeparatedPorts()
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeList, PortList = "22, 80, 443" };
        var (tcp, udp, error) = vm.BuildPortSets();
        Assert.Null(error);
        Assert.Equal(new[] { 22, 80, 443 }, tcp.OrderBy(p => p));
        Assert.Equal(tcp, udp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("22, notaport")]
    [InlineData("22, 70000")]
    public void BuildPortSets_List_InvalidEntryReturnsError(string list)
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeList, PortList = list };
        var (_, _, error) = vm.BuildPortSets();
        Assert.Equal(Loc.PortCheckInvalidList, error);
    }

    private static PortCheckToolViewModel LoopbackVm(string scope)
    {
        var vm = new PortCheckToolViewModel { Scope = scope, Target = "127.0.0.1", Udp = false };
        vm.Initialize(new ScanConfig { Subnets = { "192.168.1.0/24" } });
        return vm;
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_CommonScope_NeverCallsConfirm()
    {
        using var fast = new FastProbeTimeouts();
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeCommon);
        int calls = 0;
        await vm.StartAsync(_ => { calls++; return true; });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_ListOfTenPorts_NeverCallsConfirm()
    {
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeList);
        vm.PortList = "1,2,3,4,5,6,7,8,9,10";
        int calls = 0;
        await vm.StartAsync(_ => { calls++; return true; });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_ListWithTcpAndUdpAddingUpToOver100Probes_CallsConfirm()
    {
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeList);
        vm.Udp = true;
        vm.PortList = string.Join(",", System.Linq.Enumerable.Range(1, 60));   // 60 TCP + 60 UDP
        int calls = 0;
        await vm.StartAsync(_ => { calls++; return false; });
        Assert.Equal(1, calls);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_RangeOutsideNetworks_CallsConfirmWithOutsideTrue()
    {
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeRange);
        vm.RangeFrom = "1"; vm.RangeTo = "3";
        var seen = new System.Collections.Generic.List<bool>();
        await vm.StartAsync(outside => { seen.Add(outside); return true; });
        Assert.Equal(new[] { true }, seen);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_RangeDeclined_ProbesNothing()
    {
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeRange);
        vm.RangeFrom = "1"; vm.RangeTo = "3";
        await vm.StartAsync(_ => false);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);
        Assert.Equal(Loc.WakeStoppedMsg, vm.Status);
    }

    [Fact]
    public void NeedsConfirmation_OnlyForRangeTop1000AndLongLists()
    {
        var vm = new PortCheckToolViewModel();
        vm.Scope = PortCheckToolViewModel.ScopeCommon;
        Assert.False(vm.NeedsConfirmation(0));
        vm.Scope = PortCheckToolViewModel.ScopeList;
        Assert.False(vm.NeedsConfirmation(100));
        Assert.True(vm.NeedsConfirmation(101));
        vm.Scope = PortCheckToolViewModel.ScopeTop1000;
        Assert.True(vm.NeedsConfirmation(1000));
        vm.Scope = PortCheckToolViewModel.ScopeRange;
        Assert.True(vm.NeedsConfirmation(3));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_IpInsideConfiguredSubnet_ReturnsFalse()
    {
        var config = new ScanConfig { Subnets = { "192.168.1.0/24" } };
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("192.168.1.42", config, ownIpv4: NoCards(config)));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_IpOutsideEveryConfiguredSubnet_ReturnsTrue()
    {
        var config = new ScanConfig { Subnets = { "192.168.1.0/24" } };
        Assert.True(PortCheckToolViewModel.IsOutsideConfiguredSubnets("10.0.0.5", config, ownIpv4: NoCards(config)));
    }

    /// <summary>The IPv4 own-network set for a config without reading this machine's real cards.</summary>
    private static OwnNetworks NoCards(ScanConfig config) =>
        OwnNetworks.Build(config.Subnets, config.PinnedIps, System.Array.Empty<NetworkDetector.NetworkCard>());

    [Fact]
    public void IsOutsideConfiguredSubnets_PinnedIpCountsAsInside()
    {
        var config = new ScanConfig { Subnets = { "192.168.1.0/24" }, PinnedIps = { "10.20.30.40 Printer" } };
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("10.20.30.40", config, ownIpv4: NoCards(config)));
        Assert.True(PortCheckToolViewModel.IsOutsideConfiguredSubnets("10.20.30.41", config, ownIpv4: NoCards(config)));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_WideSubnetBeyondTheScanCapStillCounts()
    {
        var config = new ScanConfig { Subnets = { "10.0.0.0/8" } };
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("10.200.3.4", config, ownIpv4: NoCards(config)));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_SecondCardNetworkCountsEvenWithConfiguredSubnets()
    {
        var config = new ScanConfig { Subnets = { "192.168.1.0/24" } };
        var cards = new[] { new NetworkDetector.NetworkCard("Lab", "172.16.5.9", "255.255.254.0") };
        var own = OwnNetworks.Build(config.Subnets, config.PinnedIps, cards);
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("172.16.4.200", config, ownIpv4: own));
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("172.16.5.200", config, ownIpv4: own));
        Assert.True(PortCheckToolViewModel.IsOutsideConfiguredSubnets("172.16.6.1", config, ownIpv4: own));
    }

    private static readonly OwnIpv6Address[] OwnV6 =
    {
        new("Ethernet", 12, Ipv6Address.Parse("fe80::abcd%12"), 64),
        new("Ethernet", 12, Ipv6Address.Parse("2001:db8:1:2::10"), 64),
    };

    [Theory]
    [InlineData("fe80::1%12", false)]
    [InlineData("2001:db8:1:2::55", false)]
    [InlineData("2001:db8:9::1", true)]
    [InlineData("2606:4700::1111", true)]
    public void IsOutsideConfiguredSubnets_Ipv6_OnLinkIsInsideEvenWithOnlyIpv4Subnets(string ip, bool outside)
    {
        var config = new ScanConfig { Subnets = { "192.168.1.0/24" } };
        Assert.Equal(outside, PortCheckToolViewModel.IsOutsideConfiguredSubnets(ip, config, OwnV6));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_Ipv6_ConfiguredPrefixCountsAsInside()
    {
        var config = new ScanConfig { Subnets = { "2001:db8:9::/48 Lab" } };
        Assert.False(PortCheckToolViewModel.IsOutsideConfiguredSubnets("2001:db8:9:5::1", config, OwnV6));
        Assert.True(PortCheckToolViewModel.IsOutsideConfiguredSubnets("2001:db8:a::1", config, OwnV6));
    }

    [Fact]
    public void IsOutsideConfiguredSubnets_Ipv4TargetIgnoresIpv6Subnets()
    {
        var config = new ScanConfig { Subnets = { "2001:db8:9::/48" } };
        Assert.True(PortCheckToolViewModel.IsOutsideConfiguredSubnets("192.168.1.5", config, OwnV6, NoCards(config)));
    }

    [Fact]
    public void PickAddress_PrefersIpv4AndNamesItWhenBothExist()
    {
        var picked = PortCheckToolViewModel.PickAddress(new[]
        {
            System.Net.IPAddress.Parse("2001:db8::7"), System.Net.IPAddress.Parse("203.0.113.7"),
        }, true);
        Assert.Equal("203.0.113.7", picked!.Ip);
        Assert.Equal("IPv4", picked.Family);
    }

    [Fact]
    public void PickAddress_Ipv4OnlyCarriesNoFamilyNote()
    {
        var picked = PortCheckToolViewModel.PickAddress(new[] { System.Net.IPAddress.Parse("203.0.113.7") }, true);
        Assert.Equal("203.0.113.7", picked!.Ip);
        Assert.Null(picked.Family);
    }

    [Fact]
    public void PickAddress_UsesIpv6WhenThereIsNoIpv4AndTheMachineCanReachIt()
    {
        var addresses = new[] { System.Net.IPAddress.Parse("2001:db8::7") };
        var picked = PortCheckToolViewModel.PickAddress(addresses, true);
        Assert.Equal("2001:db8::7", picked!.Ip);
        Assert.Equal("IPv6", picked.Family);
        Assert.Null(PortCheckToolViewModel.PickAddress(addresses, false));
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_ConfirmGetsTcpPlusUdpProbeCount()
    {
        var vm = LoopbackVm(PortCheckToolViewModel.ScopeList);
        vm.Udp = true;
        vm.PortList = string.Join(",", System.Linq.Enumerable.Range(1, 60));   // 60 TCP + 60 UDP
        int seen = -1;
        await vm.StartAsync((_, count) => { seen = count; return false; });
        Assert.Equal(120, seen);
    }

    [Fact]
    public void ScanTypeOptions_ListEveryTypeAndConnectIsAlwaysAvailable()
    {
        var vm = new PortCheckToolViewModel();
        Assert.Equal(ScanTypes.All.Count, vm.ScanTypeOptions.Count);
        Assert.Equal(ScanType.Connect, vm.SelectedScanType.Type);
        Assert.True(vm.ScanTypeOptions.Single(o => o.Type == ScanType.Connect).IsAvailable);
    }

    [Fact]
    public void ScanTypeOptions_WithoutDriver_DriverOnlyTypesAreGreyedOutWithTheReason()
    {
        PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
        try
        {
            var vm = new PortCheckToolViewModel();
            var syn = vm.ScanTypeOptions.Single(o => o.Type == ScanType.Syn);
            Assert.False(syn.IsAvailable);
            Assert.Contains(Loc.PortDriverMissing, syn.Tooltip);
        }
        finally { PacketDriver.OverrideForTests(null); }
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_UnavailableScanType_StartsNothingAndSaysWhy()
    {
        PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
        try
        {
            var vm = LoopbackVm(PortCheckToolViewModel.ScopeCommon);
            vm.SelectedScanType = vm.ScanTypeOptions.Single(o => o.Type == ScanType.Syn);
            await vm.StartAsync(_ => true);
            Assert.Empty(vm.Results);
            Assert.False(vm.IsBusy);
            Assert.Contains(Loc.PortDriverMissing, vm.Status);
        }
        finally { PacketDriver.OverrideForTests(null); }
    }

    [Theory]
    [InlineData(ScanType.Fin)]
    [InlineData(ScanType.Null)]
    [InlineData(ScanType.Xmas)]
    [InlineData(ScanType.Maimon)]
    public void NeedsConfirmation_AggressiveScanTypes_AlwaysAsk(ScanType type)
    {
        var vm = new PortCheckToolViewModel { Scope = PortCheckToolViewModel.ScopeCommon };
        Assert.False(vm.NeedsConfirmation(5));
        vm.SelectedScanType = vm.ScanTypeOptions.Single(o => o.Type == type);
        Assert.True(vm.NeedsConfirmation(5));
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_ShowAllStates_ListsClosedPortsOtherwiseOnlyOpenOnes()
    {
        using var fast = new FastProbeTimeouts();
        PortScanner.ConnectTimeoutMs = 4000; // Windows answers a refused loopback connect only after about a second
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int open = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var tmp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tmp.Start();
        int closed = ((System.Net.IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();
        try
        {
            var vm = LoopbackVm(PortCheckToolViewModel.ScopeList);
            vm.PortList = open + "," + closed;
            await vm.StartAsync(_ => true);
            Assert.Equal(new[] { open }, vm.Results.Select(r => r.Port));

            vm.ShowAllStates = true;
            Assert.Equal(new[] { open, closed }.OrderBy(p => p), vm.Results.Select(r => r.Port));
            Assert.Contains(vm.Results, r => r.Port == closed && r.State == PortState.Closed);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void WarnMarks_FollowScanTypeScopeAndIntrusiveChecks()
    {
        var vm = new PortCheckToolViewModel();
        Assert.False(vm.WarnScanType);
        Assert.False(vm.WarnRange);
        Assert.False(vm.WarnIntrusive);

        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        vm.SelectedScanType = vm.ScanTypeOptions.Single(o => o.Type == ScanType.Xmas);
        vm.Scope = PortCheckToolViewModel.ScopeTop1000;
        vm.CheckIntrusive = true;
        Assert.True(vm.WarnScanType);
        Assert.True(vm.WarnRange);
        Assert.True(vm.WarnIntrusive);
        Assert.Contains(nameof(PortCheckToolViewModel.WarnScanType), raised);
        Assert.Contains(nameof(PortCheckToolViewModel.WarnRange), raised);
        Assert.Contains(nameof(PortCheckToolViewModel.WarnIntrusive), raised);

        vm.Scope = PortCheckToolViewModel.ScopeList;
        Assert.False(vm.WarnRange);
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveAsync_WritesTheShownPortsInEveryFormat()
    {
        using var fast = new FastProbeTimeouts();
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int open = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ipscan-portsave-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var vm = LoopbackVm(PortCheckToolViewModel.ScopeList);
            vm.PortList = open.ToString();
            Assert.False(vm.CanSave);
            Assert.Null(vm.CopyText());
            await vm.StartAsync(_ => true);
            Assert.True(vm.CanSave);
            Assert.Contains(open.ToString(), vm.CopyText());

            foreach (var format in IpScanner.Core.Export.ReportFormats.All)
            {
                var path = System.IO.Path.Combine(dir, "ports." + IpScanner.Core.Export.ReportFormats.Extension(format));
                Assert.True(await vm.SaveAsync(path, format));
                Assert.Contains(open.ToString(), System.IO.File.ReadAllText(path));
            }
        }
        finally
        {
            l.Stop();
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveAsync_WithoutResultsWritesNothing()
    {
        var vm = new PortCheckToolViewModel();
        Assert.False(await vm.SaveAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "never-written.txt"), IpScanner.Core.Export.ReportFormat.Text));
    }
}
