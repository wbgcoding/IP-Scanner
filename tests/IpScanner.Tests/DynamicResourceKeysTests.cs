using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IpScanner.Tests;

/// <summary>A DynamicResource key that no dictionary defines resolves to nothing at run time
/// (no error, just a missing brush), so every referenced key must exist somewhere.</summary>
public class DynamicResourceKeysTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + dir);
    }

    private static readonly Regex Use = new(@"\{DynamicResource\s+(?:ResourceKey=)?([\w.]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex Def = new(@"x:Key=""([\w.]+)""", RegexOptions.Compiled);
    private static readonly Regex CodeSet = new(@"Resources\[""([\w.]+)""\]\s*=", RegexOptions.Compiled);

    private static bool IsBuildOutput(string f) =>
        f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
        || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

    [Fact]
    public void EveryDynamicResourceKey_IsDefined()
    {
        var project = Path.Combine(RepoRoot(), "src", "IpScanner");
        var resources = Path.Combine(project, "Resources");
        var global = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "Theme.Dark.xaml", "Styles.xaml", "Theme.Light.xaml", "Icons.xaml" })
            foreach (Match m in Def.Matches(File.ReadAllText(Path.Combine(resources, name))))
                global.Add(m.Groups[1].Value);
        foreach (var cs in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
            foreach (Match m in CodeSet.Matches(File.ReadAllText(cs)))
                global.Add(m.Groups[1].Value);

        var missing = new List<string>();
        foreach (var xaml in Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var text = File.ReadAllText(xaml);
            var local = Def.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var key in Use.Matches(text).Select(m => m.Groups[1].Value).Distinct())
                if (!global.Contains(key) && !local.Contains(key))
                    missing.Add($"{Path.GetRelativePath(project, xaml)}: {key}");
        }
        Assert.Empty(missing);
    }
}
