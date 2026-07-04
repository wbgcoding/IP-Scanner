using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests.Core;

public class LocalizationTests
{
    [Fact]
    public void BuiltinLanguages_UseInlineStrings()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Settings", Loc.Settings);
        Loc.SetLanguage("de");
        Assert.Equal("Einstellungen", Loc.Settings);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("ru")]
    public void EmbeddedLanguage_TranslatesAndDiffersFromEnglish(string code)
    {
        Loc.SetLanguage(code);
        Assert.False(string.IsNullOrWhiteSpace(Loc.Settings));
        Assert.NotEqual("Settings", Loc.Settings);   // a real translation loaded
        Loc.SetLanguage("en");                        // restore for other tests
    }

    [Fact]
    public void UnknownLanguage_FallsBackToSystemWithoutThrowing()
    {
        Loc.SetLanguage("xx");
        Assert.False(string.IsNullOrWhiteSpace(Loc.Settings));
        Loc.SetLanguage("en");
    }
}
