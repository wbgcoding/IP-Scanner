using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace IpScanner.Tests;

/// <summary>A local attribute value beats a style trigger for the same property, so the
/// trigger never applies. Every property that an element's own inner Style sets must come
/// from that Style, never from an attribute on the element.</summary>
public class XamlLocalValueTriggerTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "PortCheckTool.xaml|RangeRow",
        "PortCheckTool.xaml|PortListBox",
        "ScanToolPage.xaml|NoteText",
    };

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void NoElementSetsLocallyWhatItsOwnStyleSets()
    {
        var project = Path.Combine(RepoRoot(), "src", "IpScanner");
        var sep = Path.DirectorySeparatorChar;
        var xName = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name";
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep)))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            var fileName = Path.GetFileName(file);
            foreach (var e in doc.Descendants())
            {
                var styles = e.Elements().Where(c => c.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal)).ToList();
                if (styles.Count == 0) continue;
                var name = (string?)e.Attribute(xName) ?? (string?)e.Attribute("Name") ?? "";
                if (Allowed.Contains(fileName + "|" + name)) continue;
                var setProps = styles
                    .SelectMany(s => s.Descendants().Where(d => d.Name.LocalName == "Setter"))
                    .Select(s => (string?)s.Attribute("Property"))
                    .Where(p => p is not null)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var a in e.Attributes())
                {
                    if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) continue;
                    if (a.Name.LocalName.Contains('.')) continue;
                    if (!setProps.Contains(a.Name.LocalName)) continue;
                    var line = ((IXmlLineInfo)e).LineNumber;
                    hits.Add($"{Path.GetRelativePath(project, file)}:{line} {e.Name.LocalName} {a.Name.LocalName}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Local values that block style triggers:\n" + string.Join("\n", hits));
    }
}
