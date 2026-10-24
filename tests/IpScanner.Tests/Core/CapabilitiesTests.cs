using System.Linq;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PacketDriverState")]
public class CapabilitiesTests
{
    private static readonly PacketDriverStatus Missing = PacketDriverStatus.Missing;
    private static readonly PacketDriverStatus Present = new(true, null, "1.79");

    [Theory]
    [InlineData(Capability.SynScan)]
    [InlineData(Capability.FinScan)]
    [InlineData(Capability.NullScan)]
    [InlineData(Capability.XmasScan)]
    [InlineData(Capability.AckScan)]
    [InlineData(Capability.TcpSynPing)]
    [InlineData(Capability.TcpAckPing)]
    [InlineData(Capability.OsFingerprint)]
    public void DriverOnly_WithoutDriver_IsUnavailableWithReason(Capability id)
    {
        var s = Capabilities.Evaluate(id, Missing, elevated: true);
        Assert.False(s.Available);
        Assert.Equal(PacketDriver.ReasonMissing, s.Reason);
        Assert.True(Capabilities.NeedsDriver(id));
    }

    [Fact]
    public void DriverOnly_WithDriver_BecomesAvailable()
    {
        foreach (var id in Capabilities.All.Where(Capabilities.NeedsDriver))
            Assert.True(Capabilities.Evaluate(id, Present, elevated: false, transportRegistered: true).Available);
    }

    [Fact]
    public void ScanTypes_AgreeWithThePicker_ForEveryDriverAndTransportState()
    {
        var map = ScanTypes.All.ToDictionary(t => t, t => Capabilities.All.Single(c => Capabilities.RuleScan(c) == t));
        Assert.Equal(ScanTypes.All.Count, map.Count);
        foreach (var driver in new[] { Missing, Present, new PacketDriverStatus(false, PacketDriver.ReasonAdmin) })
            foreach (var transport in new[] { false, true })
                foreach (var (type, id) in map)
                {
                    var picker = ScanTypes.Availability(type, driver, transport);
                    var view = Capabilities.Evaluate(id, driver, elevated: true, transportRegistered: transport);
                    Assert.Equal((picker.Available, picker.Reason), (view.Available, view.Reason));
                }
    }

    [Fact]
    public void RawScanTypes_WithDriverButNoTransport_AreGreyedOutAsTransportMissing()
    {
        foreach (var id in new[] { Capability.SynScan, Capability.WindowScan, Capability.MaimonScan })
        {
            var s = Capabilities.Evaluate(id, Present, elevated: true, transportRegistered: false);
            Assert.False(s.Available);
            Assert.Equal("transport-missing", s.Reason);
        }
    }

    [Fact]
    public void DriverFree_StaysAvailableWithoutDriver()
    {
        foreach (var id in new[] { Capability.TcpConnectScan, Capability.UdpScan, Capability.IcmpEcho, Capability.TcpConnectPing,
                     Capability.UdpPing, Capability.ListScan, Capability.SkipDiscovery, Capability.OsEstimate, Capability.ArpSweep })
            Assert.True(Capabilities.Evaluate(id, Missing, elevated: false).Available);
    }

    [Theory]
    [InlineData(Capability.IcmpTimestamp)]
    [InlineData(Capability.IcmpAddressMask)]
    public void RawIcmp_NeedsAdministrator(Capability id)
    {
        var s = Capabilities.Evaluate(id, Present, elevated: false);
        Assert.False(s.Available);
        Assert.Equal(PacketDriver.ReasonAdmin, s.Reason);
        Assert.True(Capabilities.Evaluate(id, Present, elevated: true).Available);
    }

    [Fact]
    public void Ipv6Target_RejectsIpv4OnlyModes()
    {
        var s = Capabilities.Evaluate(Capability.SynScan, Present, elevated: true, ipv6Target: true);
        Assert.Equal(PacketDriver.ReasonIpv6, s.Reason);
        Assert.True(Capabilities.Evaluate(Capability.TcpConnectScan, Present, elevated: true, ipv6Target: true).Available);
    }

    [Fact]
    public void AdminOnlyDriver_ReportsItsReason()
    {
        var s = Capabilities.Evaluate(Capability.SynScan, new PacketDriverStatus(false, PacketDriver.ReasonAdmin), elevated: false);
        Assert.Equal(PacketDriver.ReasonAdmin, s.Reason);
    }

    [Fact]
    public void EveryCapabilityHasAGroup()
    {
        Assert.Equal(Capabilities.All.Count, Capabilities.All.Distinct().Count());
        foreach (var id in Capabilities.All) Capabilities.GroupOf(id);
    }
}
