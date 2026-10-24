using System.Collections.Generic;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceGrouperTests
{
    [Fact]
    public void Group_DevicesWithSharedMacPrefix_GetSameGroup()
    {
        var devices = new List<Device>
        {
            new("192.168.1.10") { Mac = "AA:BB:CC:11:22:33", Hostname = "pc-a" },
            new("192.168.1.11") { Mac = "AA:BB:CC:44:55:66", Hostname = "pc-b" },
            new("192.168.1.12") { Mac = "DD:EE:FF:00:11:22", Hostname = "other" },
        };
        DeviceGrouper.AssignGroups(devices, gatewayIp: null);
        Assert.Equal(devices[0].GroupId, devices[1].GroupId);
        Assert.NotEqual(devices[0].GroupId, devices[2].GroupId);
    }

    [Fact]
    public void Group_Gateway_GetsDedicatedGroup()
    {
        var devices = new List<Device> { new("192.168.1.1") { Mac = "AA:BB:CC:11:22:33" } };
        DeviceGrouper.AssignGroups(devices, gatewayIp: "192.168.1.1");
        Assert.Equal(DeviceGrouper.GatewayGroupId, devices[0].GroupId);
    }

    [Fact]
    public void AssignGroups_ReturnsOnlyDevicesWhoseGroupIdChanged()
    {
        var devices = new List<Device>
        {
            new("192.168.1.10") { Mac = "AA:BB:CC:11:22:33", Hostname = "pc-a" },
            new("192.168.1.11") { Mac = "AA:BB:CC:44:55:66", Hostname = "pc-b" },
            new("192.168.1.12") { Mac = "DD:EE:FF:00:11:22", Hostname = "other" },
        };
        var firstPass = DeviceGrouper.AssignGroups(devices, gatewayIp: null);
        Assert.Equal(3, firstPass.Count);

        var secondPass = DeviceGrouper.AssignGroups(devices, gatewayIp: null);
        Assert.Empty(secondPass);

        devices[2].Hostname = "pc-c";
        var thirdPass = DeviceGrouper.AssignGroups(devices, gatewayIp: null);
        Assert.Equal(new[] { devices[2] }, thirdPass);
        Assert.Equal(devices[0].GroupId, devices[2].GroupId);
    }
}
