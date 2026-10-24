using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests;

/// <summary>{loc:L Key} looks the key up as a property on Loc and shows the bare key when none
/// exists, in every language. A typo or a missing property therefore leaks an English
/// identifier into the German surface without any error.</summary>
public class XamlLocKeyTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + dir);
    }

    [Fact]
    public void EveryXamlLocKey_IsALocProperty()
    {
        var project = Path.Combine(RepoRoot(), "src", "IpScanner");
        var usage = new Regex(@"\{loc:L\s+(?:Key=)?(\w+)\s*\}", RegexOptions.Compiled);
        var missing = Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => usage.Matches(File.ReadAllText(f))
                .Select(m => (File: Path.GetRelativePath(project, f), Key: m.Groups[1].Value)))
            .Where(u => typeof(Loc).GetProperty(u.Key) is null)
            .Select(u => $"{u.File}: {u.Key}")
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }
}
