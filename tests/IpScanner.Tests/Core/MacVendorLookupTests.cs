using IpScanner.Core.Export;
using Xunit;

namespace IpScanner.Tests.Core;

public class MacVendorLookupTests
{
    [Fact]
    public void Lookup_KnownPrefix_ReturnsVendor()
        => Assert.Equal("Cisco Systems", MacVendorLookup.Instance.Lookup("B8:27:3C:11:22:33"));

    [Fact]
    public void Lookup_UnknownPrefix_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup("FF:FF:FF:00:00:00"));

    [Fact]
    public void Lookup_NullOrEmpty_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup(""));
}
