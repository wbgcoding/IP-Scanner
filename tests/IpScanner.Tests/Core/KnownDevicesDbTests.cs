using System.Collections.Generic;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class KnownDevicesDbTests
{
    [Fact]
    public void Save_ThenLoad_ReturnsDevices()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        var devices = new List<Device>
        {
            new("192.168.1.1") { Mac = netMac, Hostname = "router" },
            new("192.168.1.5") { Mac = "DD:EE:FF:00:11:22", Hostname = "nas" },
        };
        db.Save(netMac, devices, "2026-06-01 12:00:00");

        var loaded = db.Load(netMac);
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, d => Assert.False(d.IsOnline));
        Assert.All(loaded, d => Assert.True(d.FromDb));
    }

    [Fact]
    public void GetNetworkMac_FindsGatewayMac()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        db.Save(netMac, new List<Device> { new("192.168.1.1") { Mac = netMac } }, "t");
        Assert.Equal(netMac, db.GetNetworkMac("192.168.1.1"));
    }
}
