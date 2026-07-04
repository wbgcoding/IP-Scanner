using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ArpHelperTests
{
    [Fact]
    public void ParseArpOutput_ExtractsMac()
    {
        const string sample = "  192.168.1.1          aa-bb-cc-dd-ee-ff     dynamisch";
        var mac = ArpHelper.ParseMac(sample);
        Assert.Equal("AA-BB-CC-DD-EE-FF", mac);
    }

    [Fact]
    public void ParseArpOutput_NoMac_ReturnsNull()
        => Assert.Null(ArpHelper.ParseMac("no mac here"));

    [Fact]
    public void ParseTable_MapsEveryIpToMac()
    {
        const string sample =
            "Interface: 192.168.1.50 --- 0x5\n" +
            "  192.168.1.1           aa-bb-cc-dd-ee-ff     dynamisch\n" +
            "  192.168.2.7           11-22-33-44-55-66     dynamisch\n" +
            "  192.168.1.255         ff-ff-ff-ff-ff-ff     statisch\n";
        var map = ArpHelper.ParseTable(sample);
        Assert.Equal("AA-BB-CC-DD-EE-FF", map["192.168.1.1"]);
        Assert.Equal("11-22-33-44-55-66", map["192.168.2.7"]);   // other local range
        Assert.False(map.ContainsKey("0.0.0.0"));
    }

    [Fact]
    public void ParseNetbios_ExtractsName()
    {
        const string sample = "    DESKTOP-ABC    <20>  UNIQUE      Registered";
        var name = NetBiosHelper.ParseName(sample);
        Assert.Equal("DESKTOP-ABC", name);
    }
}
