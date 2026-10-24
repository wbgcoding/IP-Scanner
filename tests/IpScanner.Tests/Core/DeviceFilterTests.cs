using System.Collections.Generic;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceFilterTests
{
    private static readonly int[] NoPorts = System.Array.Empty<int>();

    [Fact]
    public void Matches_EmptyFilter_MatchesEverything()
    {
        Assert.True(DeviceFilter.Matches("", "10.0.0.1", "nas", "AA:BB:CC:00:00:01", "Canon", NoPorts));
    }

    [Fact]
    public void Matches_BareTerm_MatchesAnyOfTheFourFields()
    {
        Assert.True(DeviceFilter.Matches("canon", "10.0.0.1", "printer", "AA:BB:CC:00:00:01", "Canon Inc.", NoPorts));
        Assert.False(DeviceFilter.Matches("canon", "10.0.0.1", "printer", "AA:BB:CC:00:00:01", "Epson", NoPorts));
    }

    [Fact]
    public void Matches_IpPrefix_TargetsOnlyIp()
    {
        Assert.True(DeviceFilter.Matches("ip:192.168.1", "192.168.1.5", "x", "AA", "Vendor", NoPorts));
        Assert.False(DeviceFilter.Matches("ip:192.168.1", "10.0.0.5", "192.168.1.hostname", "AA", "Vendor", NoPorts));
    }

    [Fact]
    public void Matches_NamePrefix_DoesNotMatchVendor()
    {
        // "name:canon" must not match a device only because its vendor is Canon.
        Assert.False(DeviceFilter.Matches("name:canon", "10.0.0.1", "printer", "AA", "Canon Inc.", NoPorts));
        Assert.True(DeviceFilter.Matches("name:canon", "10.0.0.1", "canon-printer", "AA", "Epson", NoPorts));
    }

    [Fact]
    public void Matches_MacPrefix_TargetsOnlyMac()
    {
        Assert.True(DeviceFilter.Matches("mac:AA:BB", "10.0.0.1", "x", "AA:BB:CC:00:00:01", "Vendor", NoPorts));
        Assert.False(DeviceFilter.Matches("mac:AA:BB", "AA:BB.local", "x", "11:22:33:00:00:01", "Vendor", NoPorts));
    }

    [Theory]
    [InlineData("hersteller:canon")]
    [InlineData("vendor:canon")]
    public void Matches_VendorPrefix_BothGermanAndEnglishSpelling(string term)
    {
        Assert.True(DeviceFilter.Matches(term, "10.0.0.1", "x", "AA", "Canon Inc.", NoPorts));
        Assert.False(DeviceFilter.Matches(term, "10.0.0.1", "x", "AA", "Epson", NoPorts));
    }

    [Fact]
    public void Matches_PortPrefix_TargetsKnownOpenPorts()
    {
        Assert.True(DeviceFilter.Matches("port:443", "10.0.0.1", "x", "AA", "Vendor", new[] { 80, 443 }));
        Assert.False(DeviceFilter.Matches("port:443", "10.0.0.1", "x", "AA", "Vendor", new[] { 80, 8080 }));
        Assert.False(DeviceFilter.Matches("port:443", "10.0.0.1", "x", "AA", "Vendor", NoPorts));
    }

    [Fact]
    public void Matches_PrefixIsCaseInsensitive()
    {
        Assert.True(DeviceFilter.Matches("IP:10.0.0", "10.0.0.5", "x", "AA", "Vendor", NoPorts));
        Assert.True(DeviceFilter.Matches("Name:nas", "10.0.0.1", "NAS-01", "AA", "Vendor", NoPorts));
    }

    [Fact]
    public void Matches_MultipleSpaceSeparatedTerms_AreAnded()
    {
        Assert.True(DeviceFilter.Matches("ip:192.168 name:nas", "192.168.1.5", "nas-01", "AA", "Vendor", NoPorts));
        Assert.False(DeviceFilter.Matches("ip:192.168 name:printer", "192.168.1.5", "nas-01", "AA", "Vendor", NoPorts));
    }

    [Fact]
    public void Matches_UnrecognizedPrefix_FallsBackToPlainSearchOfWholeTerm()
    {
        // "http://foo" has no recognized field before the colon, so it is treated
        // as one literal term matched against all four fields.
        Assert.True(DeviceFilter.Matches("http://foo", "10.0.0.1", "http://foo.local", "AA", "Vendor", NoPorts));
    }

    // Quick filter pills.
    [Fact]
    public void PassesQuickFilters_NoPillChecked_AlwaysPasses()
    {
        Assert.True(DeviceFilter.PassesQuickFilters(false, false, false, isOnline: false, isPinned: false, isUnknown: false));
    }

    [Fact]
    public void PassesQuickFilters_OnlyOnline_ExcludesOfflineDevice()
    {
        Assert.True(DeviceFilter.PassesQuickFilters(true, false, false, isOnline: true, isPinned: false, isUnknown: false));
        Assert.False(DeviceFilter.PassesQuickFilters(true, false, false, isOnline: false, isPinned: false, isUnknown: false));
    }

    [Fact]
    public void PassesQuickFilters_OnlyPinned_ExcludesUnpinnedDevice()
    {
        Assert.True(DeviceFilter.PassesQuickFilters(false, true, false, isOnline: false, isPinned: true, isUnknown: false));
        Assert.False(DeviceFilter.PassesQuickFilters(false, true, false, isOnline: false, isPinned: false, isUnknown: false));
    }

    [Fact]
    public void PassesQuickFilters_OnlyUnknown_ExcludesKnownDevice()
    {
        Assert.True(DeviceFilter.PassesQuickFilters(false, false, true, isOnline: false, isPinned: false, isUnknown: true));
        Assert.False(DeviceFilter.PassesQuickFilters(false, false, true, isOnline: false, isPinned: false, isUnknown: false));
    }

    [Fact]
    public void PassesQuickFilters_MultiplePillsChecked_AreAnded()
    {
        // Online + Pinned both checked: device must satisfy both.
        Assert.True(DeviceFilter.PassesQuickFilters(true, true, false, isOnline: true, isPinned: true, isUnknown: false));
        Assert.False(DeviceFilter.PassesQuickFilters(true, true, false, isOnline: true, isPinned: false, isUnknown: false));
    }

    [Fact]
    public void QuickFiltersAndTextFilter_CombineWithAnd()
    {
        // Abnahme: a pill combined with a text term. Device is online and its
        // name matches, so both checks must pass for the row to show.
        Assert.True(DeviceFilter.PassesQuickFilters(true, false, false, isOnline: true, isPinned: false, isUnknown: false)
            && DeviceFilter.Matches("name:nas", "10.0.0.1", "nas-01", "AA", "Vendor", NoPorts));
        // Same text term, but the device is offline - the pill now excludes it.
        Assert.False(DeviceFilter.PassesQuickFilters(true, false, false, isOnline: false, isPinned: false, isUnknown: false)
            && DeviceFilter.Matches("name:nas", "10.0.0.1", "nas-01", "AA", "Vendor", NoPorts));
    }
}
