using IpScanner.Core.Export;
using Xunit;

namespace IpScanner.Tests.Core;

public class MacVendorLookupTests
{
    [Fact]
    public void Lookup_KnownPrefix_ReturnsVendor()
        => Assert.Equal("XEROX CORPORATION", MacVendorLookup.Instance.Lookup("00:00:00:11:22:33"));

    [Fact]
    public void Lookup_UnknownPrefix_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup("FF:FF:FE:00:00:00"));

    [Fact]
    public void Lookup_RealDatabase_HasThousandsOfEntries()
        => Assert.True(MacVendorLookup.Instance.EntryCount > 30000);

    [Fact]
    public void Lookup_MaSAssignment_PrefersLongestPrefixOverGenericParentOui()
        // 001BC5 itself is "IEEE Registration Authority" (a shared MA-L block); the specific
        // MA-S sub-assignment 001BC5000 must win over that generic parent.
        => Assert.Equal("Converging Systems Inc.", MacVendorLookup.Instance.Lookup("00:1B:C5:00:00:11"));

    [Fact]
    public void Lookup_NullOrEmpty_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup(""));
}
