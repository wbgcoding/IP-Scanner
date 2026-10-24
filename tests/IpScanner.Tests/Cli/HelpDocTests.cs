using System.Linq;
using IpScanner.Core.Cli;
using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests.Cli;

[Collection("Localization")]
public sealed class HelpDocTests
{
    [Fact]
    public void CommandLineHelp_CarriesThePurposeAndTheLegalNoteInEnglish()
    {
        Loc.SetLanguage("de");
        try
        {
            var text = CliHelp.Text("IP-Scanner");
            Assert.Contains("Only scan networks you are responsible for.", text);
            Assert.Contains("not a penetration testing suite", text);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void InEnglish_RestoresTheActiveLanguage()
    {
        Loc.SetLanguage("de");
        try
        {
            Assert.Equal("Only scan networks you are responsible for. Scanning other networks can be against the law.",
                Loc.InEnglish(() => Loc.HelpLegalNote));
            Assert.StartsWith("Scanne nur Netzwerke", Loc.HelpLegalNote);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void HelpPageModel_ListsEveryFlagAndTheLegalNoteInTheUiLanguage()
    {
        Loc.SetLanguage("de");
        try
        {
            var shown = HelpDoc.Options().SelectMany(s => s.Rows).Select(r => r.Option).ToHashSet();
            foreach (var f in CliFlags.All) Assert.Contains(f.Display, shown);
            Assert.All(HelpDoc.Tools(), t => { Assert.NotEmpty(t.Name); Assert.NotEmpty(t.Description); });
            Assert.True(HelpDoc.Tools().Count >= 11);
            Assert.StartsWith("Scanne nur Netzwerke", Loc.HelpLegalNote);
        }
        finally { Loc.SetLanguage("en"); }
    }

    /// <summary>Every description, group, example and exit meaning of the tables has a Loc text: English equal to the
    /// table text (so the table stays the source of the command line help) and a German text that differs.</summary>
    [Fact]
    public void EveryHelpEntry_HasAnEnglishTextEqualToTheTableAndADifferentGermanText()
    {
        var entries = new System.Collections.Generic.List<(string What, string Table, System.Func<string> Text)>();
        foreach (var f in CliFlags.All) entries.Add(("flag " + f.Id, f.Help, () => Loc.HelpOption(f.Id)));
        foreach (var g in CliFlags.Groups) entries.Add(("group " + g, g, () => Loc.HelpGroup(g)));
        for (var i = 0; i < CliHelp.Examples.Count; i++)
        {
            var index = i;
            entries.Add(("example " + CliHelp.Examples[i].Args, CliHelp.Examples[i].Description, () => Loc.HelpExample(index)));
        }
        foreach (var (code, meaning) in CliExit.Table) entries.Add(("exit " + code, meaning, () => Loc.HelpExit(code)));

        Loc.SetLanguage("de");
        try
        {
            foreach (var (what, table, text) in entries)
            {
                var en = Loc.InEnglish(text);
                var de = text();
                Assert.True(en == table, $"{what}: English Loc text differs from the table text");
                Assert.True(de.Length > 0 && de != en, $"{what}: German text is missing or equals English");
                Assert.DoesNotContain((char)0x2014, de);
                Assert.DoesNotContain((char)0x2013, de);
            }
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void HelpPageModel_UsesTheUiLanguageForEveryDescription()
    {
        Loc.SetLanguage("de");
        try
        {
            Assert.Equal(CliFlags.Groups.Count, HelpDoc.Options().Count);
            Assert.Contains(HelpDoc.Options(), s => s.Title == "Ziele");
            Assert.All(HelpDoc.Options().SelectMany(s => s.Rows), r => Assert.NotEqual(CliFlags.All.First(f => f.Display == r.Option).Help, r.Help));
            Assert.Equal(CliHelp.Examples.Count, HelpDoc.Examples().Count);
            Assert.All(HelpDoc.Examples().Zip(CliHelp.Examples), z => Assert.NotEqual(z.Second.Description, z.First.Description));
            Assert.Equal(CliExit.Table.Count, HelpDoc.ExitCodes().Count);
            Assert.All(HelpDoc.ExitCodes().Zip(CliExit.Table), z => Assert.NotEqual(z.Second.Meaning, z.First.Meaning));
        }
        finally { Loc.SetLanguage("en"); }
    }
}
