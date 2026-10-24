using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class TargetSpecTests
{
    private static List<string> Expand(string targets, TargetSpecOptions? options = null)
    {
        var spec = TargetSpec.Parse(targets, options);
        Assert.True(spec.IsValid, string.Join("; ", spec.Errors.Select(e => e.Token + ": " + e.Reason)));
        return spec.Expand(true).ToList();
    }

    private static IReadOnlyList<string> NoHosts(string _) => Array.Empty<string>();

    // ---- syntaxes ----

    [Fact]
    public void Cidr24_YieldsHostsOneToTwoFiftyFour()
    {
        var ips = Expand("192.168.1.0/24");
        Assert.Equal(254, ips.Count);
        Assert.Equal("192.168.1.1", ips[0]);
        Assert.Equal("192.168.1.254", ips[^1]);
    }

    [Fact]
    public void Cidr_AlignsToTheRealNetworkAddress()
    {
        Assert.Equal(new[] { "10.0.0.77", "10.0.0.78" }, Expand("10.0.0.77/30"));
        Assert.Equal(new[] { "10.0.0.76", "10.0.0.77" }, Expand("10.0.0.77/31"));
        Assert.Equal(new[] { "10.0.0.77" }, Expand("10.0.0.77/32"));
    }

    [Fact]
    public void Cidr30_SkipsNetworkAndBroadcast()
        => Assert.Equal(new[] { "10.0.0.5", "10.0.0.6" }, Expand("10.0.0.4/30"));

    [Fact]
    public void LastOctetRange_IsInclusive()
    {
        var ips = Expand("192.168.1.10-40");
        Assert.Equal(31, ips.Count);
        Assert.Equal("192.168.1.10", ips[0]);
        Assert.Equal("192.168.1.40", ips[^1]);
    }

    [Fact]
    public void MultiOctetRange_EnumeratesTheBoxInAddressOrder()
    {
        var ips = Expand("192.168.1-3.1-254");
        Assert.Equal(3 * 254, ips.Count);
        Assert.Equal("192.168.1.1", ips[0]);
        Assert.Equal("192.168.2.1", ips[254]);
        Assert.Equal("192.168.3.254", ips[^1]);
    }

    [Fact]
    public void Wildcard_CoversTheWholeOctet()
    {
        var ips = Expand("10.0.0.*");
        Assert.Equal(256, ips.Count);
        Assert.Equal("10.0.0.0", ips[0]);
        Assert.Equal("10.0.0.255", ips[^1]);
    }

    [Fact]
    public void FullAddressRange_SpansOctets()
    {
        var ips = Expand("10.0.0.254-10.0.1.2");
        Assert.Equal(new[] { "10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1", "10.0.1.2" }, ips);
    }

    [Fact]
    public void SingleAddress_IsOneTarget()
        => Assert.Equal(new[] { "172.16.0.9" }, Expand("172.16.0.9"));

    [Fact]
    public void CommaAndWhitespaceLists_KeepTheSpecifiedOrder()
        => Assert.Equal(new[] { "10.0.0.9", "10.0.0.1", "10.0.0.2", "10.0.0.3" },
                        Expand("10.0.0.9, 10.0.0.1-3"));

    [Fact]
    public void Hostname_IsResolvedAtExpansionTime()
    {
        int calls = 0;
        var opts = new TargetSpecOptions
        {
            Resolver = h => { calls++; return h == "nas.lan" ? new[] { "10.0.0.50", "10.0.0.51" } : Array.Empty<string>(); },
        };
        var spec = TargetSpec.Parse("nas.lan", opts);
        Assert.True(spec.IsValid);
        Assert.Equal(0, calls);
        Assert.Equal(new[] { "10.0.0.50", "10.0.0.51" }, spec.Expand().ToList());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Hostname_ThatDoesNotResolve_IsReportedNotFatal()
    {
        var spec = TargetSpec.Parse("ghost.lan,10.0.0.1", new TargetSpecOptions { Resolver = NoHosts });
        Assert.Equal(new[] { "10.0.0.1" }, spec.Expand().ToList());
        Assert.Equal(new[] { "ghost.lan" }, spec.UnresolvedHosts);
    }

    [Fact]
    public void Hostname_ResolverThrowing_CountsAsUnresolved()
    {
        var spec = TargetSpec.Parse("boom.lan", new TargetSpecOptions { Resolver = _ => throw new InvalidOperationException() });
        Assert.Empty(spec.Expand().ToList());
        Assert.Equal(new[] { "boom.lan" }, spec.UnresolvedHosts);
    }

    [Fact]
    public void Hostname_IgnoresNonIpv4ResolverAnswers()
    {
        var spec = TargetSpec.Parse("dual.lan", new TargetSpecOptions { Resolver = _ => new[] { "fe80::1", "10.0.0.7" } });
        Assert.Equal(new[] { "10.0.0.7" }, spec.Expand().ToList());
    }

    // ---- files ----

    [Fact]
    public void TargetFile_SkipsCommentsAndBlankLines()
    {
        var lines = new[]
        {
            "# office", "10.0.0.1", "", "  10.0.0.2   # printer", "10.0.1.0/30 , 10.0.2.5", "   # trailing",
        };
        var opts = new TargetSpecOptions { IncludeFile = "targets.txt", ReadLines = _ => lines };
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.1.1", "10.0.1.2", "10.0.2.5" }, Expand("", opts));
    }

    [Fact]
    public void TargetFile_ReadFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "targetspec-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# c\n10.0.0.1\n10.0.0.2\n");
        try { Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, Expand("", new TargetSpecOptions { IncludeFile = path })); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingTargetFile_IsAnErrorNotAnException()
    {
        var spec = TargetSpec.Parse("10.0.0.1", new TargetSpecOptions { IncludeFile = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")) });
        Assert.False(spec.IsValid);
        Assert.Single(spec.Errors);
        Assert.Equal(new[] { "10.0.0.1" }, spec.Expand().ToList());
    }

    [Fact]
    public void InlineText_AlsoHonoursCommentsAndNewlines()
        => Assert.Equal(new[] { "10.0.0.1", "10.0.0.3" }, Expand("10.0.0.1 # one\n10.0.0.3"));

    // ---- exclusions ----

    [Fact]
    public void Exclusion_WinsOverInclusion()
    {
        var ips = Expand("10.0.0.0/29", new TargetSpecOptions { Exclude = "10.0.0.3, 10.0.0.5-6" });
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.4" }, ips);
    }

    [Fact]
    public void Exclusion_AppliesToEveryOverlappingInclude()
    {
        var ips = Expand("10.0.0.1-5, 10.0.0.3-7", new TargetSpecOptions { Exclude = "10.0.0.4" });
        Assert.DoesNotContain("10.0.0.4", ips);
        Assert.Equal(6, ips.Count);
    }

    [Fact]
    public void ExcludeFile_AndHostnameExclusion()
    {
        var opts = new TargetSpecOptions
        {
            Exclude = "gateway.lan",
            ExcludeFile = "skip.txt",
            ReadLines = _ => new[] { "10.0.0.2 # skip", "# nothing", "10.0.0.3" },
            Resolver = h => new[] { "10.0.0.1" },
        };
        Assert.Equal(new[] { "10.0.0.4" }, Expand("10.0.0.1-4", opts));
    }

    [Fact]
    public void Exclusion_OfAWholeBlock_LeavesTheRest()
    {
        var ips = Expand("10.0.0.0/24", new TargetSpecOptions { Exclude = "10.0.0.0/25" });
        Assert.Equal(127, ips.Count);
        Assert.Equal("10.0.0.128", ips[0]);
    }

    [Fact]
    public void Exclusion_MalformedToken_IsReported()
    {
        var spec = TargetSpec.Parse("10.0.0.1", new TargetSpecOptions { Exclude = "10.0.0.999" });
        Assert.False(spec.IsValid);
    }

    // ---- dedupe ----

    [Fact]
    public void Overlaps_AreYieldedOnceInFirstSeenOrder()
    {
        var ips = Expand("10.0.0.1-4, 10.0.0.3-6, 10.0.0.2, 10.0.0.0/29");
        Assert.Equal(ips.Distinct().Count(), ips.Count);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6" }, ips);
    }

    [Fact]
    public void Duplicate_OfAnEarlierSinglePoint_InsideALaterRange_IsSkipped()
    {
        var ips = Expand("10.0.0.5, 10.0.0.1-8");
        Assert.Equal("10.0.0.5", ips[0]);
        Assert.Equal(8, ips.Count);
        Assert.Equal(8, ips.Distinct().Count());
    }

    [Fact]
    public void Hostname_DuplicatingAnAddress_IsDeduped()
    {
        var ips = Expand("web.lan, 10.0.0.1-3", new TargetSpecOptions { Resolver = _ => new[] { "10.0.0.2" } });
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.1", "10.0.0.3" }, ips);
    }

    // ---- malformed ----

    [Theory]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.256")]
    [InlineData("10.0.0.1.2")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/a")]
    [InlineData("10.0.0.0/24/8")]
    [InlineData("/24")]
    [InlineData("10.0.0.40-10")]
    [InlineData("10.0.0.1-")]
    [InlineData("10.0.0.-5")]
    [InlineData("10.0.0.1-2-3")]
    [InlineData("10.0.0.10-10.0.0.5")]
    [InlineData("10.0.0.**")]
    [InlineData("10.0.0.1000")]
    [InlineData("-")]
    [InlineData("exa_mple.com!")]
    [InlineData("under_score.lan")]
    [InlineData("-bad.lan")]
    [InlineData("bad-.lan")]
    [InlineData("a..b")]
    [InlineData("2001:db8::zz")]
    [InlineData("10.0.0.1:80")]
    public void MalformedToken_IsReportedAndYieldsNothing(string token)
    {
        var spec = TargetSpec.Parse(token, new TargetSpecOptions { Resolver = NoHosts });
        Assert.False(spec.IsValid);
        Assert.Equal(token, spec.Errors[0].Token);
        Assert.False(string.IsNullOrWhiteSpace(spec.Errors[0].Reason));
        Assert.Empty(spec.Expand().ToList());
    }

    [Fact]
    public void MalformedToken_DoesNotPoisonTheValidOnes()
    {
        var spec = TargetSpec.Parse("10.0.0.1, 10.0.0.300, 10.0.0.2");
        Assert.Single(spec.Errors);
        Assert.Equal("10.0.0.300", spec.Errors[0].Token);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, spec.Expand().ToList());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# only a comment")]
    public void EmptyInput_IsValidButHasNoTargets(string? text)
    {
        var spec = TargetSpec.Parse(text);
        Assert.True(spec.IsValid);
        Assert.False(spec.HasTargets);
        Assert.Empty(spec.Expand().ToList());
    }

    // ---- laziness, estimate, cap ----

    [Fact]
    public void Slash8_YieldsItsFirstTenTargetsWithoutMaterializing()
    {
        var spec = TargetSpec.Parse("10.0.0.0/8");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var first = spec.Expand(true).Take(10).ToList();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Enumerable.Range(1, 10).Select(i => "10.0.0." + i), first);
        // 16.7M addresses as strings would be ~1 GB; a lazy walk stays far below one megabyte.
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Slash8_WithAnExclusion_StillStartsLazily()
    {
        var spec = TargetSpec.Parse("10.0.0.0/8", new TargetSpecOptions { Exclude = "10.0.0.0/16" });
        Assert.Equal("10.1.0.0", spec.Expand(true).First());
    }

    [Fact]
    public void WildcardBox_IsLazyToo()
    {
        var spec = TargetSpec.Parse("*.*.*.*");
        Assert.Equal(new[] { "0.0.0.0", "0.0.0.1", "0.0.0.2" }, spec.Expand(true).Take(3).ToList());
    }

    [Fact]
    public void Estimate_IsTheSumOfTheEntriesAndExactForASingleRange()
    {
        Assert.Equal(254, TargetSpec.Parse("10.0.0.0/24").EstimatedCount);
        Assert.Equal(762, TargetSpec.Parse("192.168.1-3.1-254").EstimatedCount);
        Assert.Equal(16_777_214, TargetSpec.Parse("10.0.0.0/8").EstimatedCount);
        Assert.Equal(3, TargetSpec.Parse("a.lan, 10.0.0.1, 10.0.0.2").EstimatedCount);
        Assert.Equal(0, TargetSpec.Parse("").EstimatedCount);
    }

    [Fact]
    public void Cap_ExactlyAtTheLimit_NeedsNoConfirmation()
    {
        var spec = TargetSpec.Parse("10.0.0.0-10.0.255.255");
        Assert.Equal(TargetSpec.HardCap, spec.EstimatedCount);
        Assert.False(spec.RequiresConfirmation);
        Assert.Equal(TargetSpec.HardCap, spec.Expand().Count());
    }

    [Fact]
    public void Cap_OneAbove_RequiresConfirmation()
    {
        var spec = TargetSpec.Parse("10.0.0.0-10.1.0.0");
        Assert.True(spec.RequiresConfirmation);
        var ex = Assert.Throws<TargetCapExceededException>(() => spec.Expand());
        Assert.Equal(spec.EstimatedCount, ex.Estimated);
    }

    [Fact]
    public void Cap_ConfirmedByFlag_Expands()
    {
        var spec = TargetSpec.Parse("10.0.0.0/8");
        Assert.Equal(5, spec.Expand(true).Take(5).Count());
    }

    [Fact]
    public void Cap_ConfirmationCallback_ReceivesTheEstimateAndDecides()
    {
        var spec = TargetSpec.Parse("10.0.0.0/8");
        long? seen = null;
        Assert.Throws<TargetCapExceededException>(() => spec.Expand(n => { seen = n; return false; }));
        Assert.Equal(spec.EstimatedCount, seen);
        Assert.Equal(2, spec.Expand(_ => true).Take(2).Count());
    }

    [Fact]
    public void Cap_CallbackIsNotAskedBelowTheLimit()
    {
        var spec = TargetSpec.Parse("10.0.0.0/24");
        bool asked = false;
        Assert.Equal(254, spec.Expand(_ => { asked = true; return false; }).Count());
        Assert.False(asked);
    }

    [Fact]
    public void Cap_HostnameFanOutBeyondTheLimit_StopsUnconfirmedExpansion()
    {
        var many = Enumerable.Range(0, 70_000).Select(i => $"{10 + (i >> 16)}.{(i >> 8) & 255}.{i & 255}.1").ToList();
        var spec = TargetSpec.Parse("big.lan", new TargetSpecOptions { Resolver = _ => many });
        Assert.False(spec.RequiresConfirmation);
        Assert.Throws<TargetCapExceededException>(() => spec.Expand().ToList());
        Assert.Equal(many.Count, spec.Expand(true).Count());
    }

    // ---- sidebar prefix form ----

    [Fact]
    public void FromPrefixes_MatchesTheLegacyHostList()
    {
        var prefixes = new[] { "192.168.1", "10.0.0", "192.168.2" };
        Assert.Equal(Ipv4.HostsInRanges(prefixes, null), TargetSpec.FromPrefixes(prefixes).Expand().ToList());
    }

    [Fact]
    public void FromPrefixes_WithRanges_MatchesTheLegacyHostList()
    {
        var prefixes = new[] { "10.0.32", "10.0.33", "10.0.34" };
        Ipv4.TryParseUInt("10.0.32.200", out var a);
        Ipv4.TryParseUInt("10.0.33.20", out var b);
        Ipv4.TryParseUInt("10.0.33.10", out var c);
        Ipv4.TryParseUInt("10.0.33.30", out var d);
        var ranges = new List<(uint First, uint Last)> { (a, b), (c, d), (d, d) };
        Assert.Equal(Ipv4.HostsInRanges(prefixes, ranges), TargetSpec.FromPrefixes(prefixes, ranges).Expand().ToList());
    }

    [Fact]
    public void FromPrefixes_EmptyRangesOrPrefixes_YieldNothing()
    {
        Assert.Empty(TargetSpec.FromPrefixes(new[] { "10.0.0" }, new List<(uint, uint)>()).Expand().ToList());
        Assert.Empty(TargetSpec.FromPrefixes(Array.Empty<string>()).Expand().ToList());
        Assert.Empty(TargetSpec.FromPrefixes(new[] { "not.a.prefix" }).Expand().ToList());
    }

    [Fact]
    public void FromPrefixes_FullSixtyFiveThousand_StaysUnderTheCap()
    {
        var prefixes = Enumerable.Range(0, Ipv4.MaxPrefixes).Select(i => $"10.0.{i}").ToList();
        var spec = TargetSpec.FromPrefixes(prefixes);
        Assert.False(spec.RequiresConfirmation);
        Assert.Equal(Ipv4.MaxPrefixes * Ipv4.HostsPerSubnet, spec.Expand().Count());
    }
}
