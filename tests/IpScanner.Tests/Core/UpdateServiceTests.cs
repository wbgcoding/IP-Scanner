using System;
using IpScanner.Core;
using Xunit;

namespace IpScanner.Tests.Core;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("2.8.0", true)]      // newer build
    [InlineData("v2.8", true)]       // 'v' prefix, missing build
    [InlineData("3.0", true)]        // newer major
    [InlineData("2.7.0", false)]     // same version
    [InlineData("2.7", false)]       // same, short form
    [InlineData("2.6.9", false)]     // older
    [InlineData("1.0.0", false)]     // older major
    [InlineData("", false)]          // empty tag
    [InlineData("not-a-version", false)]
    public void IsNewerTag_ComparesAgainstCurrent(string tag, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewerTag(tag, new Version(2, 7, 0, 0)));
}
