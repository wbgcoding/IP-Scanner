using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class DeviceViewModelTests
{
    [Fact]
    public void Update_ReflectsDeviceState()
    {
        var d = new Device("192.168.1.1") { Hostname = "router" };
        d.RecordPing(new PingResult(true, 2.5, 64));
        var vm = new DeviceViewModel(d);
        vm.Refresh();
        Assert.Equal("192.168.1.1", vm.Ip);
        Assert.Equal("ONLINE", vm.Status);
        Assert.Equal("router", vm.Hostname);
        Assert.Contains("2,5", vm.AvgDisplay);  // de-DE comma decimal
    }

    [Fact]
    public void Refresh_RaisesPropertyChanged()
    {
        var d = new Device("192.168.1.1");
        var vm = new DeviceViewModel(d);
        bool raised = false;
        vm.PropertyChanged += (_, _) => raised = true;
        d.RecordPing(new PingResult(true, 1.0, 64));
        vm.Refresh();
        Assert.True(raised);
    }
}
