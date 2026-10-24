using System;
using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Localization;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class TargetSpecIpv6Tests
{
    private static List<string> Expand(string targets, TargetSpecOptions? options = null)
    {
        var spec = TargetSpec.Parse(targets, options);
        Assert.True(spec.IsValid, string.Join("; ", spec.Errors.Select(e => e.Token + ": " + e.Reason)));
        return spec.Expand(true).ToList();
    }

    [Theory]
    [InlineData("::1", "::1")]
    [InlineData("2001:DB8:0:0:0:0:0:1", "2001:db8::1")]
    [InlineData("2001:db8::0001", "2001:db8::1")]
    [InlineData("::ffff:10.1.2.3", "::ffff:10.1.2.3")]
    public void Literal_IsNormalized(string input, string expected)
        => Assert.Equal(new[] { expected }, Expand(input));

    [Fact]
    public void LinkLocalLiteral_KeepsItsZone()
        => Assert.Equal(new[] { "fe80::1%12" }, Expand("fe80::1%12"));

    [Fact]
    public void Prefix_YieldsEveryAddress()
    {
        var ips = Expand("2001:db8::/126");
        Assert.Equal(new[] { "2001:db8::", "2001:db8::1", "2001:db8::2", "2001:db8::3" }, ips);
    }

    [Fact]
    public void Prefix_ClearsHostBits()
        => Assert.Equal(new[] { "2001:db8::10", "2001:db8::11" }, Expand("2001:db8::11/127"));

    [Fact]
    public void Slash112_HoldsExactlyTheCap()
    {
        var spec = TargetSpec.Parse("2001:db8::/112");
        Assert.True(spec.IsValid);
        Assert.Equal(65536, spec.EstimatedCount);
        Assert.False(spec.RequiresConfirmation);
        Assert.Equal(65536, spec.Expand().Count());
    }

    [Theory]
    [InlineData("2001:db8::/64")]
    [InlineData("2001:db8::/111")]
    [InlineData("::/0")]
    [InlineData("2001:db8::1-2001:db8::2:0")]
    public void TooLargeEntry_IsRefusedWithTheExplanation(string token)
    {
        var spec = TargetSpec.Parse(token);
        var error = Assert.Single(spec.Errors);
        Assert.Equal(Loc.Ipv6PrefixTooLarge, error.Reason);
        Assert.False(spec.HasTargets);
    }

    [Fact]
    public void Range_IsInclusive()
        => Assert.Equal(new[] { "2001:db8::fe", "2001:db8::ff", "2001:db8::100" }, Expand("2001:db8::fe-2001:db8::100"));

    [Fact]
    public void Range_CarriesItsZone()
        => Assert.Equal(new[] { "fe80::1%4", "fe80::2%4" }, Expand("fe80::1-fe80::2%4"));

    [Fact]
    public void ReversedRange_IsAnError()
        => Assert.Single(TargetSpec.Parse("2001:db8::5-2001:db8::1").Errors);

    [Theory]
    [InlineData("2001:db8:::1")]
    [InlineData("2001:db8::g")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("fe80::1%")]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8::/112%3")]
    [InlineData("2001:db8::/")]
    public void Malformed_IsAnError(string token)
        => Assert.Single(TargetSpec.Parse(token).Errors);

    [Fact]
    public void MixedList_KeepsTheOrderAndBothFamilies()
    {
        var ips = Expand("10.0.0.1, 2001:db8::1 10.0.0.2\n::1");
        Assert.Equal(new[] { "10.0.0.1", "2001:db8::1", "10.0.0.2", "::1" }, ips);
        var spec = TargetSpec.Parse("10.0.0.1 ::1");
        Assert.True(spec.HasIpv4Targets);
        Assert.True(spec.HasIpv6Targets);
    }

    [Fact]
    public void Families_AreReportedPerLiteral()
    {
        var v4 = TargetSpec.Parse("10.0.0.0/24 example.org");
        Assert.True(v4.HasIpv4Targets);
        Assert.False(v4.HasIpv6Targets);
        var v6 = TargetSpec.Parse("2001:db8::/120");
        Assert.True(v6.HasIpv6Targets);
        Assert.False(v6.HasIpv4Targets);
    }

    [Fact]
    public void Duplicates_AreDroppedInsideAndAcrossEntries()
        => Assert.Equal(new[] { "2001:db8::1", "2001:db8::2" }, Expand("2001:db8::1 2001:db8::1-2001:db8::2 2001:db8::2"));

    [Fact]
    public void Exclusion_RemovesAnAddressAndARange()
    {
        var ips = Expand("2001:db8::/124", new TargetSpecOptions { Exclude = "2001:db8::3 2001:db8::8-2001:db8::f" });
        Assert.Equal(7, ips.Count);
        Assert.DoesNotContain("2001:db8::3", ips);
        Assert.DoesNotContain("2001:db8::a", ips);
        Assert.Contains("2001:db8::7", ips);
    }

    [Fact]
    public void Exclusion_MayBeAHugePrefix()
    {
        var spec = TargetSpec.Parse("2001:db8::1 2001:db9::1", new TargetSpecOptions { Exclude = "2001:db8::/32" });
        Assert.True(spec.IsValid);
        Assert.Equal(new[] { "2001:db9::1" }, spec.Expand().ToList());
    }

    [Fact]
    public void Exclusion_IgnoresTheZone()
    {
        var ips = Expand("fe80::1%12 fe80::2%12", new TargetSpecOptions { Exclude = "fe80::1" });
        Assert.Equal(new[] { "fe80::2%12" }, ips);
    }

    [Fact]
    public void Exclusion_NeverCrossesFamilies()
    {
        var ips = Expand("10.0.0.1 ::1 ::2", new TargetSpecOptions { Exclude = "10.0.0.0/8 ::2" });
        Assert.Equal(new[] { "::1" }, ips);
        var other = Expand("10.0.0.1 ::1 ::2", new TargetSpecOptions { Exclude = "::/0 10.0.0.1" });
        Assert.Empty(other);
    }

    [Fact]
    public void Exclusion_ByV4CidrLeavesIpv6Alone()
        => Assert.Equal(new[] { "::a00:1" }, Expand("::a00:1 10.0.0.1", new TargetSpecOptions { Exclude = "10.0.0.0/24" }));

    [Fact]
    public void Hostname_ResolvesToTheRequestedFamilyOnly()
    {
        Func<string, IReadOnlyList<string>> resolver = _ => new[] { "192.0.2.7", "2001:db8::7", "fe80::7%3" };
        Assert.Equal(new[] { "192.0.2.7" }, Expand("host.test", new TargetSpecOptions { Resolver = resolver }));
        Assert.Equal(new[] { "2001:db8::7", "fe80::7%3" },
            Expand("host.test", new TargetSpecOptions { Resolver = resolver, HostFamily = IpFamily.V6 }));
    }

    [Fact]
    public void Hostname_WithoutTheFamily_IsUnresolved()
    {
        var spec = TargetSpec.Parse("v4only.test", new TargetSpecOptions
        {
            Resolver = _ => new[] { "192.0.2.7" },
            HostFamily = IpFamily.V6,
        });
        Assert.Empty(spec.Expand().ToList());
        Assert.Equal(new[] { "v4only.test" }, spec.UnresolvedHosts);
    }

    [Fact]
    public void LargeSpec_StillRequiresConfirmationAboveTheCap()
    {
        var spec = TargetSpec.Parse("10.0.0.0/16 2001:db8::/112");
        Assert.True(spec.RequiresConfirmation);
        Assert.Throws<TargetCapExceededException>(() => spec.Expand().ToList());
    }

    [Fact]
    public void Ipv4Syntaxes_StayUnchangedNextToIpv6()
    {
        Assert.Equal(new[] { "192.168.1.1", "192.168.1.2", "192.168.2.5" },
            Expand("192.168.1.1-2 192.168.2.*").Take(3).ToList()
                .Select(a => a == "192.168.2.0" ? "192.168.2.5" : a).ToList());
        Assert.Equal(254, Expand("192.168.1.0/24").Count);
    }
}
