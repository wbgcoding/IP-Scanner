using Xunit;

namespace IpScanner.Tests;

public class LargeManualTargetTests
{
    [Theory]
    [InlineData("192.168.0.0/22", null, true)]
    [InlineData("192.168.0.0", "/22", true)]
    [InlineData("192.168.0.0", "16", true)]
    [InlineData("192.168.0.0/24", null, false)]
    [InlineData("192.168.0.0", "/24", false)]
    [InlineData("", "/22", false)]
    [InlineData("  ", "/22", false)]
    [InlineData("not an ip/22", null, false)]
    public void IsLargeManualTarget_OnlyForValidTargetsWiderThanOneSubnet(string raw, string? cidrText, bool expected)
        => Assert.Equal(expected, MainWindow.IsLargeManualTarget(raw, cidrText));
}
