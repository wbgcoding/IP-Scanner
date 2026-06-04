using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetEntryTests
{
    [Fact]
    public void Parse_TargetOnly()
    {
        var e = NetEntry.Parse("192.168.2.0/24");
        Assert.Equal("192.168.2.0/24", e.Target);
        Assert.Equal("", e.Name);
        Assert.Equal("", e.Color);
    }

    [Fact]
    public void Parse_TargetNameAndColor()
    {
        var e = NetEntry.Parse("192.168.2.10 Drucker Keller a6e3a1");
        Assert.Equal("192.168.2.10", e.Target);
        Assert.Equal("Drucker Keller", e.Name);
        Assert.Equal("#A6E3A1", e.Color);
    }

    [Fact]
    public void ToString_RoundTrips_WithoutHashInColor()
    {
        var e = new NetEntry("10.0.0.0/16", "Werk 1", "#89B4FA");
        Assert.Equal("10.0.0.0/16 Werk 1 89B4FA", e.ToString());
        Assert.Equal(e, NetEntry.Parse(e.ToString()));
    }

    [Fact]
    public void Parse_SingleToken_NeverTreatedAsColor()
    {
        // A lone 6-hex-char token is the target, not a color.
        var e = NetEntry.Parse("abcdef");
        Assert.Equal("abcdef", e.Target);
        Assert.Equal("", e.Color);
    }
}
