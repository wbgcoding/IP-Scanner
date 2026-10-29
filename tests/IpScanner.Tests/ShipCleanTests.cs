using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IpScanner.Tests;

/// <summary>Keeps shipped sources free of work-item references, typographic dashes and
/// tool or process names that do not belong in a public repository.</summary>
public class ShipCleanTests
{
    private static readonly Regex ItemRef = new(@"\b(items?|punkte?)[\s-]+#?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"(?<![:\w""'])//[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly string[] BannedTokens = { "Claude", "ClaudeLab", "vmctl", "WORK.md" };
    private static readonly Regex OwnerName = new(@"\bben(jamin)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IP-Scanner.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        var self = Path.GetFullPath(Path.Combine(root, "tests", "IpScanner.Tests", "ShipCleanTests.cs"));
        var src = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".json"));
        var tests = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories);
        return src.Concat(tests)
            .Where(f => !IsBuildOutput(f) && !string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static IEnumerable<Match> Comments(string path, string text)
    {
        if (path.EndsWith(".cs"))
            return LineComment.Matches(text).Concat(BlockComment.Matches(text));
        if (path.EndsWith(".xaml"))
            return XmlComment.Matches(text);
        return Enumerable.Empty<Match>();
    }

    [Fact]
    public void Comments_CarryNoWorkItemNumbers()
    {
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var path in SourceFiles())
        {
            var text = File.ReadAllText(path);
            foreach (var c in Comments(path, text))
                if (ItemRef.IsMatch(c.Value))
                    hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, c.Index)}");
        }
        Assert.True(hits.Count == 0, "Item numbers in comments:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Sources_UseNoEmOrEnDashes_ExceptTheEnDashEmptyValue()
    {
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var path in SourceFiles())
        {
            var text = File.ReadAllText(path).Replace("\"–\"", "\"\"");
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '–' || text[i] == '—')
                    hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, i)}");
        }
        Assert.True(hits.Count == 0, "Em or en dashes:\n" + string.Join("\n", hits.Distinct()));
    }

    [Fact]
    public void Docs_UseNoDashesAndNameNoOwnerOrTestRig()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Concat(new[] { Path.Combine(root, "README.md"), Path.Combine(root, "CHANGELOG.md") });
        var banned = new Regex(@"\b(ben|claudelab|vmctl)\b", RegexOptions.IgnoreCase);
        var hits = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\u2013' || text[i] == '\u2014')
                    hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, i)} dash");
            foreach (Match m in banned.Matches(text))
                hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, m.Index)} {m.Value}");
        }
        Assert.True(hits.Count == 0, "Docs ship-clean violations:\n" + string.Join("\n", hits.Distinct()));
    }

    [Fact]
    public void Sources_NameNoToolsOrProcessFiles()
    {
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var path in SourceFiles())
        {
            var text = File.ReadAllText(path);
            foreach (var token in BannedTokens)
            {
                int at = text.IndexOf(token, StringComparison.Ordinal);
                if (at >= 0) hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, at)} {token}");
            }
            foreach (Match m in OwnerName.Matches(text))
                hits.Add($"{Path.GetRelativePath(root, path)}:{LineOf(text, m.Index)} {m.Value}");
        }
        Assert.True(hits.Count == 0, "Banned tokens:\n" + string.Join("\n", hits));
    }
}
