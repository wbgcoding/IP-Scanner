using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ServiceSearchTests
{
    [Fact]
    public void Matches_EmptyQuery_MatchesEverything()
        => Assert.True(ServiceSearch.Matches("HTTP", 80, ""));

    [Theory]
    [InlineData("HTTP", 80, "http")]
    [InlineData("HTTP-Alt", 81, "alt")]
    [InlineData("SSH", 22, "22")]
    public void Matches_ExactSubstringOrPort_Matches(string name, int port, string query)
        => Assert.True(ServiceSearch.Matches(name, port, query));

    [Theory]
    [InlineData("HTTP", "htpp")]      // transposition
    [InlineData("Printer", "printr")] // missing letter
    [InlineData("Telnet", "telnett")] // extra letter
    public void Matches_OneLetterTypo_StillMatches(string name, string typo)
        => Assert.True(ServiceSearch.Matches(name, 1, typo));

    [Fact]
    public void Matches_UnrelatedQuery_DoesNotMatch()
        => Assert.False(ServiceSearch.Matches("HTTP", 80, "postgres"));

    [Fact]
    public void Matches_QueryLongerThanNameByALot_DoesNotFalsePositive()
        => Assert.False(ServiceSearch.Matches("DNS", 53, "completely unrelated search phrase"));

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("http", "htpp", 1)]
    [InlineData("same", "same", 0)]
    [InlineData("", "abc", 3)]
    public void Levenshtein_KnownDistances(string a, string b, int expected)
        => Assert.Equal(expected, ServiceSearch.Levenshtein(a, b));
}
