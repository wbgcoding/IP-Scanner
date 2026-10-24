using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PortScannerGate")]
public class NetworkDetectorTests
{
    [Fact]
    public void LocalIp_IsValidIpv4_OnConnectedMachine()
    {
        var ip = NetworkDetector.GetLocalIpFast();
        if (ip is not null) Assert.True(Ipv4.IsValid(ip));
    }

    [Fact]
    public void DetectAll_PopulatesNetworkInfo()
    {
        var info = NetworkDetector.DetectFast();
        Assert.NotNull(info);
        if (info.Ip is not null) Assert.True(Ipv4.IsValid(info.Ip));
    }

    // The Wake-on-LAN tool's network-card picker.
    [Fact]
    public void ListIpv4Cards_ReturnsValidAddressesOnConnectedMachine()
    {
        var cards = NetworkDetector.ListIpv4Cards();
        Assert.NotNull(cards);
        foreach (var card in cards) Assert.True(Ipv4.IsValid(card.Ip));
    }

    // Forcing a real local adapter address selects that address.
    [Fact]
    public void DetectFast_WithForceIpMatchingAnAdapter_UsesThatIp()
    {
        var cards = NetworkDetector.ListIpv4Cards();
        if (cards.Count == 0) return; // no adapters on this machine, nothing to force
        var forced = cards[0].Ip;
        var info = NetworkDetector.DetectFast(forced);
        Assert.Equal(forced, info.Ip);
    }

    // An address that matches no adapter falls back to auto-detection.
    [Fact]
    public void DetectFast_WithForceIpMatchingNoAdapter_FallsBackToAutoDetected()
    {
        var auto = NetworkDetector.DetectFast();
        var forced = NetworkDetector.DetectFast("203.0.113.123"); // TEST-NET-3, never a local adapter
        Assert.Equal(auto.Ip, forced.Ip);
    }

    [Fact]
    public void SourceFor_BindsOnlyWhenTheFamiliesMatch()
    {
        var saved = NetworkDetector.SelectedSourceIp;
        try
        {
            NetworkDetector.SelectedSourceIp = null;
            Assert.Null(NetworkDetector.SourceFor("192.0.2.1"));

            NetworkDetector.SelectedSourceIp = "127.0.0.1";
            Assert.Equal(System.Net.IPAddress.Loopback, NetworkDetector.SourceFor("192.0.2.1"));
            Assert.Null(NetworkDetector.SourceFor("2001:db8::1"));
            Assert.Null(NetworkDetector.SourceFor("fe80::1"));
        }
        finally { NetworkDetector.SelectedSourceIp = saved; }
    }
}
