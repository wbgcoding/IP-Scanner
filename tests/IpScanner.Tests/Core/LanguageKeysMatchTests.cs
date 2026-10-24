using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>All Resources\lang\*.json files must define the exact same key set - a
/// key present in one language but missing in another silently falls back to English
/// there instead of failing loudly, which is easy to miss when only one file gets a
/// new string added.</summary>
public class LanguageKeysMatchTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + dir);
    }

    private static Dictionary<string, string[]> LoadAllLangKeys()
    {
        var dir = Path.Combine(RepoRoot(), "src", "IpScanner.Core", "Resources", "lang");
        var result = new Dictionary<string, string[]>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            result[Path.GetFileName(file)] = map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    [Fact]
    public void AllLanguageFiles_DefineTheSameKeySet()
    {
        var perFile = LoadAllLangKeys();
        Assert.True(perFile.Count > 1, "Expected multiple language files.");
        var reference = perFile["en.json"];
        Assert.NotEmpty(reference);
        foreach (var (file, keys) in perFile)
            Assert.Equal(reference, keys);
    }

    /// <summary>A repeated key deserializes without complaint and the later entry silently wins, so a
    /// copy with a typo can override the good translation.</summary>
    [Fact]
    public void NoLanguageFile_RepeatsAKey()
    {
        var dir = Path.Combine(RepoRoot(), "src", "IpScanner.Core", "Resources", "lang");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var repeated = new List<string>();
            var reader = new Utf8JsonReader(File.ReadAllBytes(file), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            var depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                else if (reader.TokenType == JsonTokenType.PropertyName && depth == 1 && !seen.Add(reader.GetString()!))
                    repeated.Add(reader.GetString()!);
            }
            Assert.True(repeated.Count == 0, $"{Path.GetFileName(file)} repeats: {string.Join(", ", repeated)}");
        }
    }

    [Fact]
    public void NoLanguageFile_HasEmptyTranslationValues()
    {
        var dir = Path.Combine(RepoRoot(), "src", "IpScanner.Core", "Resources", "lang");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            var empty = map.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToArray();
            Assert.True(empty.Length == 0, $"{Path.GetFileName(file)} has empty values for: {string.Join(", ", empty)}");
        }
    }
}
