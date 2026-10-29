using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class PortsColumnTests
{
    private static NetService Tcp(int port, string name = "svc") => new(name, port, NetProtocol.Tcp);

    [Fact]
    public void Text_ListsOpenTcpPortsAscending_AndIgnoresUdp()
    {
        var text = PortsSummary.Text(new[] { Tcp(443), Tcp(22), new NetService("dns", 53, NetProtocol.Udp), Tcp(80) });
        Assert.Equal("22, 80, 443", text);
    }

    [Fact]
    public void Text_IsADashWithoutPorts()
        => Assert.Equal("–", PortsSummary.Text(new List<NetService>()));

    [Fact]
    public void Text_CutsAfterTheLimitWithAnEllipsis()
    {
        var many = Enumerable.Range(1, 10).Select(p => Tcp(p)).ToList();
        Assert.Equal("1, 2, 3, 4, 5, 6, …", PortsSummary.Text(many));
        Assert.Equal("1, 2, 3, 4, 5, 6", PortsSummary.Text(many.Take(6)));
    }

    [Fact]
    public void Tip_HasOneLinePerPortWithTheServiceName_AndIsNullWhenEmpty()
    {
        Assert.Equal("22  ssh\n80  http", PortsSummary.Tip(new[] { Tcp(80, "http"), Tcp(22, "ssh") }));
        Assert.Null(PortsSummary.Tip(new List<NetService>()));
    }

    [Fact]
    public void SortKey_OrdersByCountThenFirstPort()
    {
        var none = PortsSummary.SortKey(new List<NetService>());
        var oneHigh = PortsSummary.SortKey(new[] { Tcp(8080) });
        var oneLow = PortsSummary.SortKey(new[] { Tcp(22) });
        var two = PortsSummary.SortKey(new[] { Tcp(443), Tcp(1000) });
        Assert.True(none < oneLow && oneLow < oneHigh && oneHigh < two);
    }

    [Fact]
    public void Viewmodel_RaisesPortsWhenTheServiceListChanges()
    {
        var vm = new DeviceViewModel(new Device("192.0.2.9"));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("–", vm.Ports);

        vm.Services.ReplaceAll(new[] { Tcp(443), Tcp(22) });

        Assert.Equal("22, 443", vm.Ports);
        Assert.Contains(nameof(DeviceViewModel.Ports), raised);
        Assert.Contains(nameof(DeviceViewModel.PortsTip), raised);
        Assert.Contains(nameof(DeviceViewModel.PortsSortKey), raised);
    }

    [Fact]
    public void Search_MatchesAnOpenPortNumberWithoutAPrefix()
    {
        Assert.True(DeviceFilter.Matches("443", "10.0.0.1", "x", "AA", "Vendor", new[] { 80, 443 }));
        Assert.False(DeviceFilter.Matches("443", "10.0.0.1", "x", "AA", "Vendor", new[] { 80, 8443 }));
    }
}
