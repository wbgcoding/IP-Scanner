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
    public void ParseNetbios_ExtractsName()
    {
        const string sample = "    DESKTOP-ABC    <20>  UNIQUE      Registered";
        var name = NetBiosHelper.ParseName(sample);
        Assert.Equal("DESKTOP-ABC", name);
    }
}
