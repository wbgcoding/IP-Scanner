using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ToolsPrefillTests
{
    [Fact]
    public void Apply_FillsEveryTargetTakingPage()
    {
        var vm = new ToolsViewModel();
        new ToolsPrefill().Apply(vm, "192.168.1.20", "AA:BB:CC:DD:EE:FF");

        Assert.Equal("192.168.1.20", vm.PortCheck.Target);
        Assert.Equal("192.168.1.20", vm.DnsLookup.Target);
        Assert.Equal("192.168.1.20", vm.PingRoute.Ping.Target);
        Assert.Equal("192.168.1.20", vm.PingRoute.Route.Target);
        Assert.Equal("192.168.1.20", vm.Certificate.Target);
        Assert.Equal("192.168.1.20", vm.Mac.ArpIp);
        Assert.Equal("AA:BB:CC:DD:EE:FF", vm.WakeOnLan.Mac);
        Assert.Equal("192.168.1.20", vm.WakeOnLan.TargetIp);
        Assert.Equal("AA:BB:CC:DD:EE:FF", vm.Mac.VendorMac);
    }

    [Fact]
    public void Apply_KeepsWhatTheUserTyped()
    {
        var vm = new ToolsViewModel();
        vm.DnsLookup.Target = "example.org";
        vm.WakeOnLan.Mac = "11:22:33:44:55:66";
        new ToolsPrefill().Apply(vm, "10.0.0.5", "AA:BB:CC:DD:EE:FF");

        Assert.Equal("example.org", vm.DnsLookup.Target);
        Assert.Equal("11:22:33:44:55:66", vm.WakeOnLan.Mac);
        Assert.Equal("10.0.0.5", vm.PortCheck.Target);
    }

    [Fact]
    public void Apply_ReplacesAnEarlierPrefillWithTheNewDevice()
    {
        var vm = new ToolsViewModel();
        var prefill = new ToolsPrefill();
        prefill.Apply(vm, "10.0.0.5", "AA:BB:CC:DD:EE:01");
        prefill.Apply(vm, "10.0.0.6", "AA:BB:CC:DD:EE:02");

        Assert.Equal("10.0.0.6", vm.PortCheck.Target);
        Assert.Equal("AA:BB:CC:DD:EE:02", vm.WakeOnLan.Mac);
    }

    [Fact]
    public void Apply_StopsOverwritingAfterTheUserEditsAPrefilledField()
    {
        var vm = new ToolsViewModel();
        var prefill = new ToolsPrefill();
        prefill.Apply(vm, "10.0.0.5", null);
        vm.PortCheck.Target = "10.0.0.5:8080";
        prefill.Apply(vm, "10.0.0.6", null);

        Assert.Equal("10.0.0.5:8080", vm.PortCheck.Target);
        Assert.Equal("10.0.0.6", vm.DnsLookup.Target);
    }

    [Fact]
    public void Apply_WithoutMacLeavesTheWakePageAlone()
    {
        var vm = new ToolsViewModel();
        new ToolsPrefill().Apply(vm, "10.0.0.5", "");

        Assert.Equal("", vm.WakeOnLan.Mac);
        Assert.Null(vm.WakeOnLan.TargetIp);
        Assert.Equal("10.0.0.5", vm.PingRoute.Ping.Target);
    }

    [Fact]
    public void Apply_WithoutIpFillsOnlyTheMacFields()
    {
        var vm = new ToolsViewModel();
        new ToolsPrefill().Apply(vm, null, "AA:BB:CC:DD:EE:FF");

        Assert.Equal("AA:BB:CC:DD:EE:FF", vm.WakeOnLan.Mac);
        Assert.Equal("", vm.PortCheck.Target);
    }
}
