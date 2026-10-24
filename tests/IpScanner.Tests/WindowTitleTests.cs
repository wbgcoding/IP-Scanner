using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace IpScanner.Tests;

/// <summary>A window without a title shows up nameless to screen readers and in the task switcher.</summary>
public class WindowTitleTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryWindowDeclaresATitle()
    {
        var project = Path.Combine(RepoRoot(), "src", "IpScanner");
        var sep = Path.DirectorySeparatorChar;
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep)))
        {
            var root = XDocument.Load(file).Root;
            if (root is null || root.Name.LocalName != "Window") continue;
            if (string.IsNullOrWhiteSpace((string?)root.Attribute("Title")))
                missing.Add(Path.GetRelativePath(project, file));
        }
        Assert.True(missing.Count == 0, "Windows without a Title:\n" + string.Join("\n", missing));
    }
}
