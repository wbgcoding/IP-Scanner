using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.Core;

// Named collection so no other test class that touches the process-wide
// Loc.German flag (e.g. DeviceViewModelTests) can interleave with these
// - DisableParallelization is set on the CollectionDefinition below.
[Collection("Localization")]
public class LocalizationTests
{
    [Fact]
    public void OfflineRecheckTooltip_HasNoDashesInAnyLanguage()
    {
        try
        {
            foreach (var code in new[] { "en", "de", "ar", "es", "fr", "hi", "ja", "pt", "ru", "zh" })
            {
                Loc.SetLanguage(code);
                var text = Loc.TipOfflineRecheck;
                Assert.DoesNotContain('\u2014', text);
                Assert.DoesNotContain('\u2013', text);
            }
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void InvalidEntry_WithoutInput_IsAFullSentenceNotADanglingColon()
    {
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Enter an entry first.", Loc.InvalidEntry(""));
            Assert.Equal("Enter an entry first.", Loc.InvalidEntry("  "));
            Assert.Equal("Invalid entry: abc", Loc.InvalidEntry("abc"));
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void BuiltinLanguages_UseInlineStrings()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Settings", Loc.Settings);
        Loc.SetLanguage("de");
        Assert.Equal("Einstellungen", Loc.Settings);
        Loc.SetLanguage("en");   // restore for other tests in this collection
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

    [Fact]
    public void TxtReport_FollowsActiveLanguage_NotFixedEnglish()
    {
        // The TXT report labels follow the UI language: the German report must read German.
        Loc.SetLanguage("de");
        var info = new NetworkInfo { Ip = "192.168.1.100" };
        var d = new Device("192.168.1.1") { Hostname = "router" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = TxtExporter.Write(new[] { d }, info, dir, "20260601_120000", "router");
            var text = File.ReadAllText(path);
            Assert.Contains("NETZWERK-SCAN-BERICHT", text);
            Assert.Contains("ONLINE", text);   // StatusOnline uppercases the German "Online" -> same spelling
            Assert.DoesNotContain("NETWORK SCAN REPORT", text);
        }
        finally { Loc.SetLanguage("en"); }
    }

    // A permanent gate against the drift where a new S(de, en) is added to
    // Loc.cs without adding the matching key to all nine Resources\lang\*.json files
    // (English falls back silently, so the gap goes unnoticed until someone switches
    // language). Loc.Collector records every English key actually passed to S() -
    // no separate source parser to keep in sync.
    [Fact]
    public void EveryLocKey_ExistsInEveryLanguageFile_AndNoLanguageFileHasOrphanKeys()
    {
        Loc.Collector = new HashSet<string>();
        HashSet<string> keys;
        try
        {
            foreach (var prop in typeof(Loc).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (prop.PropertyType != typeof(string) || prop.GetIndexParameters().Length > 0) continue;
                _ = prop.GetValue(null);
            }
        }
        finally
        {
            keys = Loc.Collector;
            Loc.Collector = null;
        }

        var asm = typeof(Loc).Assembly;
        var langFiles = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase) && n.Contains(".lang."))
            .ToArray();
        Assert.True(langFiles.Length >= 9, $"expected 9 embedded language files, found {langFiles.Length}");

        var problems = new List<string>();
        foreach (var name in langFiles)
        {
            using var stream = asm.GetManifestResourceStream(name);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;

            var missing = keys.Where(k => !map.ContainsKey(k)).ToArray();
            if (missing.Length > 0)
                problems.Add($"{name} is missing keys: {string.Join(" | ", missing)}");

            var orphans = map.Keys.Where(k => !keys.Contains(k)).ToArray();
            if (orphans.Length > 0)
                problems.Add($"{name} has orphan keys no longer produced by Loc: {string.Join(" | ", orphans)}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // The report header timestamp must follow the date/number format setting
    // (CultureInfo.CurrentCulture) like LastScanText, not a fixed "dd.MM.yyyy HH:mm:ss".
    [Fact]
    public async Task ReportTimestamp_FollowsCurrentCulture_NotAFixedGermanPattern()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var dir = Directory.CreateTempSubdirectory().FullName;
            var vm = new MainViewModel(
                pingFunc: (ip, _) => ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
                detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
                dispatch: a => a());
            vm.Config = new ScanConfig { PingCount = 1, FileOutput = true, OutputDirectory = dir, KnownDevicesDb = false, EnableInternetPing = false };

            await vm.RunScanAsync(new[] { "10.0.0" });

            Assert.NotNull(vm.LastExportPath);
            var text = File.ReadAllText(vm.LastExportPath!);
            var expected = DateTime.Now.ToString("M/d/yyyy", CultureInfo.GetCultureInfo("en-US"));
            Assert.Contains(expected, text);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // Product names, protocol and format names, key caps and operating systems that read the same in every language.
    private static readonly HashSet<string> KeptInEnglish = new(StringComparer.Ordinal)
    {
        "Ctrl", "Del", "Ping", "DNS", "MAC", "N/A", "OK", "Windows", "Linux/Unix", "ULA", "DHCP discover", "TTL",
    };

    // Words a Latin-script language spells exactly like English (or borrows as is), per language.
    private static readonly string[] SameInSpanishFrenchPortuguese =
        { "App", "Top 1000", "Normal", "Internet", "Pings", "Total", "\u00D8 Total" };

    private static readonly Dictionary<string, string[]> SameSpelled = new()
    {
        ["es"] = SameInSpanishFrenchPortuguese.Concat(new[] { "Pings:", "Offline", "Online", "Error", "General", "Global", "No" }).ToArray(),
        ["fr"] = SameInSpanishFrenchPortuguese.Concat(new[]
            { "Action", "Code", "Date", "Format", "Interface", "Max", "Min", "Mode", "Note", "Port", "Ports", "Route", "Service", "Services", "Threads", "Type", "Version", "max" }).ToArray(),
        ["pt"] = SameInSpanishFrenchPortuguese.Concat(new[]
            { "Backup (*.zip)|*.zip", "Broadcast", "Gateway", "Interface", "Link-local", "Online/Offline", "Status", "Threads", "Offline", "Online", "Global", "Pings:" }).ToArray(),
    };

    [Theory]
    [InlineData("ar")]
    [InlineData("hi")]
    [InlineData("ja")]
    [InlineData("ru")]
    [InlineData("zh")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt")]
    public void NonLatinLanguage_TranslatesEveryEnglishText(string code)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "IP-Scanner.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "src", "IpScanner.Core", "Resources", "lang", code + ".json");
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var untranslated = map
            .Where(kv => kv.Key.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            .Where(kv => kv.Value == kv.Key && !KeptInEnglish.Contains(kv.Key) && !(SameSpelled.TryGetValue(code, out var same) && same.Contains(kv.Key)))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(untranslated.Count == 0, code + " still has English values for: " + string.Join(" | ", untranslated));
    }

    [Fact]
    public void Languages_ListsEnglishFirstThenRestAlphabeticalByEnglishName()
    {
        // English name order: Arabic, Chinese, French, German, Hindi, Japanese,
        // Portuguese, Russian, Spanish - matches the "System, English, rest
        // alphabetical" dropdown order (System itself is prepended by the UI).
        Assert.Equal(
            new[] { "en", "ar", "zh", "fr", "de", "hi", "ja", "pt", "ru", "es" },
            Loc.Languages.Select(l => l.Code).ToArray());
    }

    [Theory]
    [InlineData("de-DE", "Automatisch (Deutsch)")]
    [InlineData("fr-FR", "Automatisch (Français)")]
    [InlineData("it-IT", "Automatisch (English)")]
    public void LangAutoDetected_NamesTheSystemLanguage(string culture, string expected)
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Loc.SetLanguage("de");
            Assert.Equal(expected, Loc.LangAutoDetected);
            Loc.SetLanguage("en");
            Assert.Equal(expected.Replace("Automatisch", "Automatic"), Loc.LangAutoDetected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
            Loc.SetLanguage("en");
        }
    }
}

// Marker for the "Localization" collection: disables parallelization between
// any test classes tagged [Collection("Localization")], since they share the
// process-wide Loc.German static, which parallel tests would race on.
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection { }
