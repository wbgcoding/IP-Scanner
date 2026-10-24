using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class ProfileNameRuleTests
{
    private static readonly string[] Taken = { "Home", "Default" };

    [Fact]
    public void NormalName_IsAccepted()
        => Assert.Null(ProfileNameRule.Validate("Office", Taken));

    [Theory]
    [InlineData("Home, Office")]
    [InlineData("Home;Office")]
    [InlineData(",")]
    public void CommaOrSemicolon_IsRejected(string name)
    {
        Loc.SetLanguage("en");
        Assert.Equal("Names cannot contain commas or semicolons.", ProfileNameRule.Validate(name, Taken));
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("home")]
    [InlineData("DEFAULT")]
    public void Duplicate_IsRejectedIgnoringCase(string name)
    {
        Loc.SetLanguage("en");
        Assert.Equal(Loc.ProfileNameTaken, ProfileNameRule.Validate(name, Taken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_IsRejected(string name)
    {
        Loc.SetLanguage("en");
        Assert.Equal(Loc.ProfileNameRequired, ProfileNameRule.Validate(name, Taken));
    }
}
