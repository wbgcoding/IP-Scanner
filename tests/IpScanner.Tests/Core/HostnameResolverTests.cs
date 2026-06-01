using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class HostnameResolverTests
{
    [Fact]
    public void StripLocaldomain_RemovesSuffix()
        => Assert.Equal("myhost", HostnameResolver.Normalize("myhost.localdomain"));

    [Fact]
    public void Normalize_KeepsPlainName()
        => Assert.Equal("router.home", HostnameResolver.Normalize("router.home"));
}
