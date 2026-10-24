using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IpScanner.Tests;

/// <summary>A relative pack URI such as Icon="Resources/x.ico" resolves against the folder
/// of the XAML file, so in a subfolder it points at a resource that does not exist and the
/// window throws while loading. Files outside the project root must use "/Resources/...".</summary>
public class XamlResourcePathTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + dir);
    }

    [Fact]
    public void XamlInSubfolders_UsesRootedResourcePaths()
    {
        var project = Path.Combine(RepoRoot(), "src", "IpScanner");
        var relative = new Regex("(Icon|Source|ImageSource)=\"Resources/", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetDirectoryName(f), project, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => relative.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(project, f))
            .ToList();
        Assert.Empty(offenders);
    }
}
