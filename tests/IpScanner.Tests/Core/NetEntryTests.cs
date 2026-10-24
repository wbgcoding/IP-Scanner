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
    public void Parse_TargetNameAndColor_LegacyBareHexFormat()
    {
        // Pre-v2.10 files store the color as a bare hex token (no marker).
        // Still read for backward compatibility as long as it contains a
        // letter.
        var e = NetEntry.Parse("192.168.2.10 Drucker Keller a6e3a1");
        Assert.Equal("192.168.2.10", e.Target);
        Assert.Equal("Drucker Keller", e.Name);
        Assert.Equal("#A6E3A1", e.Color);
    }

    [Fact]
    public void Parse_TargetNameAndColor_NewMarkedFormat()
    {
        var e = NetEntry.Parse("192.168.2.10 Drucker Keller ~a6e3a1");
        Assert.Equal("192.168.2.10", e.Target);
        Assert.Equal("Drucker Keller", e.Name);
        Assert.Equal("#A6E3A1", e.Color);
    }

    [Fact]
    public void Parse_NameEndingInSixDigits_NoLongerMisreadAsColor()
    {
        // "Drucker 123456" keeps its number: a plain 6-digit string is also valid
        // hex, so all-digit trailing tokens must not be read as a color.
        var e = NetEntry.Parse("192.168.2.10 Drucker 123456");
        Assert.Equal("Drucker 123456", e.Name);
        Assert.Equal("", e.Color);
    }

    [Fact]
    public void ToString_RoundTrips_UsingUnambiguousMarker()
    {
        var e = new NetEntry("10.0.0.0/16", "Werk 1", "#89B4FA");
        Assert.Equal("10.0.0.0/16 Werk 1 ~89B4FA", e.ToString());
        Assert.Equal(e, NetEntry.Parse(e.ToString()));
    }

    [Fact]
    public void ToString_ThenParse_NameEndingInHexLetters_StillUnambiguous()
    {
        // Even a name that itself ends in six hex-safe letters round-trips
        // correctly now, because the marker (not a heuristic) decides.
        var e = new NetEntry("10.0.0.0/16", "Server AABBCC", "#89B4FA");
        var round = NetEntry.Parse(e.ToString());
        Assert.Equal("Server AABBCC", round.Name);
        Assert.Equal("#89B4FA", round.Color);
    }

    [Fact]
    public void Parse_SingleToken_NeverTreatedAsColor()
    {
        // A lone 6-hex-char token is the target, not a color.
        var e = NetEntry.Parse("abcdef");
        Assert.Equal("abcdef", e.Target);
        Assert.Equal("", e.Color);
    }

    [Fact]
    public void HasDuplicateName_SameNameDifferentCase_IsADuplicate()
    {
        var entries = new[] { "192.168.2.10 Büro", "192.168.2.20 Keller" };
        Assert.True(NetEntry.HasDuplicateName(entries, "büro", excludeTarget: null));
    }

    [Fact]
    public void HasDuplicateName_EditingTheSameEntry_ExcludedFromItsOwnCheck()
    {
        var entries = new[] { "192.168.2.10 Büro", "192.168.2.20 Keller" };
        // Re-saving "Büro" itself (same target) must not warn about a
        // collision with itself.
        Assert.False(NetEntry.HasDuplicateName(entries, "Büro", excludeTarget: "192.168.2.10"));
    }

    [Fact]
    public void HasDuplicateName_BlankName_NeverCounts()
    {
        var entries = new[] { "192.168.2.10", "192.168.2.20" };
        Assert.False(NetEntry.HasDuplicateName(entries, "", excludeTarget: null));
    }

    [Fact]
    public void HasDuplicateName_NoMatch_ReturnsFalse()
    {
        var entries = new[] { "192.168.2.10 Büro" };
        Assert.False(NetEntry.HasDuplicateName(entries, "Keller", excludeTarget: null));
    }

    // Hostname targets for pinned entries.
    [Theory]
    [InlineData("nas.local", true)]
    [InlineData("nas", true)]
    [InlineData("my-nas.local", true)]
    [InlineData("192.168.1.5", true)]     // an IPv4 also passes the hostname shape check
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("has space.local", false)]
    [InlineData("-leading.local", false)]
    [InlineData("trailing-.local", false)]
    [InlineData("a..b", false)]           // empty label between two dots
    public void IsValidHostname_ChecksRfcIshShape(string target, bool expected)
        => Assert.Equal(expected, NetEntry.IsValidHostname(target));

    [Fact]
    public void IsValidHostname_TooLong_IsRejected()
        => Assert.False(NetEntry.IsValidHostname(new string('a', 254)));

    [Fact]
    public void Parse_ThenToString_RoundTrips_HostnameTarget()
    {
        var e = NetEntry.Parse("nas.local NAS ~A6E3A1");
        Assert.Equal("nas.local", e.Target);
        Assert.Equal("NAS", e.Name);
        Assert.Equal("nas.local NAS ~A6E3A1", e.ToString());
    }
}
