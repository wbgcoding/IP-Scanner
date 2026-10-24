using IpScanner.Core.Import;
using Xunit;

namespace IpScanner.Tests.Core;

public class ListImportParserTests
{
    [Fact]
    public void Parse_SameTextGivesDifferentCountsPerTargetKind()
    {
        // The import dialog re-parses the browsed text when the target kind changes.
        var text = "192.168.1.0/24\n10.0.0.5\n172.16.0.0/16";
        var asSubnets = ListImportParser.Parse(text, ImportTargetKind.Network);
        var asHosts = ListImportParser.Parse(text, ImportTargetKind.Host);

        Assert.Equal(3, asSubnets.Accepted.Count);
        Assert.Single(asHosts.Accepted);
        Assert.Equal(2, asHosts.SkippedLines);
        Assert.Equal(asSubnets.TotalLines, asHosts.TotalLines);
    }

    [Fact]
    public void Parse_AcceptsSpaceAndCsvFormat_SkipsBlankAndCommentLines()
    {
        var text = "192.168.1.0/24 Buero\n" +
                   "\n" +
                   "# a comment\n" +
                   "192.168.2.5,Server\n";
        var result = ListImportParser.Parse(text, ImportTargetKind.Network);

        Assert.Equal(2, result.Accepted.Count);
        Assert.Equal(0, result.SkippedLines);
        Assert.Equal(2, result.TotalLines);
        Assert.Equal("192.168.1.0/24", result.Accepted[0].Target);
        Assert.Equal("Buero", result.Accepted[0].Name);
        Assert.Equal("192.168.2.5", result.Accepted[1].Target);
        Assert.Equal("Server", result.Accepted[1].Name);
    }

    [Fact]
    public void Parse_SkipsInvalidTargetsAndCountsThem()
    {
        var text = "192.168.1.0/24\nnot-an-ip\n192.168.1.999\n10.0.0.0/8";
        var result = ListImportParser.Parse(text, ImportTargetKind.Network);

        Assert.Equal(2, result.Accepted.Count);
        Assert.Equal(2, result.SkippedLines);
        Assert.Equal(4, result.TotalLines);
    }

    [Fact]
    public void Parse_NetworkKind_RejectsPlainIpWithoutCidrIsStillValid()
    {
        // A bare IP is a valid /32-implicit network entry (same rule as manual entry).
        var result = ListImportParser.Parse("192.168.1.5", ImportTargetKind.Network);
        Assert.Single(result.Accepted);
    }

    [Fact]
    public void Parse_PinnedOrHostKind_RejectsCidrNotation()
    {
        var result = ListImportParser.Parse("192.168.1.0/24", ImportTargetKind.Pinned);
        Assert.Empty(result.Accepted);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void Parse_EmptyText_ReturnsZeroTotals()
    {
        var result = ListImportParser.Parse("", ImportTargetKind.Host);
        Assert.Empty(result.Accepted);
        Assert.Equal(0, result.SkippedLines);
        Assert.Equal(0, result.TotalLines);
    }
}
