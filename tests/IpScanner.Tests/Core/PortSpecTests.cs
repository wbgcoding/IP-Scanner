using System.Linq;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PortScannerGate")]
public class PortSpecTests
{
    [Fact]
    public void TryParse_SinglePortsAndRanges_ExpandForBothProtocols()
    {
        Assert.True(PortSpec.TryParse("22,80,100-102", out var spec));
        Assert.Equal(new[] { 22, 80, 100, 101, 102 }, spec.Tcp);
        Assert.Equal(spec.Tcp, spec.Udp);
    }

    [Fact]
    public void TryParse_ProtocolGroups_SplitTcpAndUdp()
    {
        Assert.True(PortSpec.TryParse("T:80,443,U:53", out var spec));
        Assert.Equal(new[] { 80, 443 }, spec.Tcp);
        Assert.Equal(new[] { 53 }, spec.Udp);
    }

    [Fact]
    public void TryParse_DashAlone_IsEveryPort()
    {
        Assert.True(PortSpec.TryParse("-", out var spec));
        Assert.Equal(65535, spec.Tcp.Count);
        Assert.Equal(1, spec.Tcp.Min());
        Assert.Equal(65535, spec.Tcp.Max());
    }

    [Fact]
    public void TryParse_OpenEndedRanges_UseTheBounds()
    {
        Assert.True(PortSpec.TryParse("T:65530-", out var high));
        Assert.Equal(6, high.Tcp.Count);
        Assert.True(PortSpec.TryParse("T:-3", out var low));
        Assert.Equal(new[] { 1, 2, 3 }, low.Tcp);
    }

    [Fact]
    public void TryParse_ServiceName_ResolvesThroughTheBuiltInTable()
    {
        Assert.True(PortSpec.TryParse("ssh", out var spec));
        Assert.Contains(22, spec.Tcp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("80,,443")]
    [InlineData("100-50")]
    [InlineData("nosuchservice")]
    public void TryParse_Invalid_ReturnsFalse(string text)
    {
        Assert.False(PortSpec.TryParse(text, out _));
    }

    [Fact]
    public void Top_ReturnsTheMostFrequentPorts()
    {
        var spec = PortSpec.Top(10);
        Assert.Equal(10, spec.Tcp.Count);
        Assert.Contains(80, spec.Tcp);
        Assert.Contains(443, spec.Tcp);
    }

    [Fact]
    public void Common_IsTheLegacyKnownServiceSet()
    {
        var spec = PortSpec.Common();
        Assert.Contains(22, spec.Tcp);
        Assert.Contains(53, spec.Udp);
        Assert.Equal(PortScanner.TcpPorts.Length, spec.Tcp.Count);
        Assert.Equal(PortScanner.UdpPorts.Length, spec.Udp.Count);
    }

    [Fact]
    public void Only_DropsTheSwitchedOffProtocol()
    {
        var spec = PortSpec.Range(1, 5).Only(tcp: true, udp: false);
        Assert.Equal(5, spec.Tcp.Count);
        Assert.Empty(spec.Udp);
        Assert.True(PortSpec.Range(1, 5).Only(false, false).IsEmpty);
    }

    [Fact]
    public void Range_IsInclusive()
    {
        Assert.Equal(new[] { 20, 21, 22 }, PortSpec.Range(20, 22).Tcp);
    }

    [Fact]
    public void PortResult_ReadsAsPortAndService()
    {
        var text = new PortResult(22, NetProtocol.Tcp, PortState.Open, PortReason.SynAck).ToString();
        Assert.StartsWith("22/TCP", text);
        Assert.DoesNotContain("PortResult {", text);
    }
}
