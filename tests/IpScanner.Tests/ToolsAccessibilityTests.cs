using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace IpScanner.Tests;

public class ToolsAccessibilityTests
{
    private static readonly string[] Controls = { "TextBox", "ComboBox", "PasswordBox", "ListBoxItem" };

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "IP-Scanner.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static IEnumerable<string> ToolFiles()
    {
        var views = Path.Combine(RepoRoot(), "src", "IpScanner", "Views");
        foreach (var f in Directory.GetFiles(Path.Combine(views, "Tools"), "*.xaml")) yield return f;
        yield return Path.Combine(views, "ToolsWindow.xaml");
    }

    [Fact]
    public void ToolInputsAndNavEntries_CarryAnAccessibleName()
    {
        var missing = new List<string>();
        var files = ToolFiles().ToList();
        Assert.True(files.Count > 5);
        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var el in doc.Descendants())
            {
                var name = el.Name.LocalName;
                if (name.Contains('.') || !Controls.Contains(name)) continue;
                var ok = el.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy");
                if (!ok)
                    missing.Add(Path.GetFileName(file) + ":" + ((System.Xml.IXmlLineInfo)el).LineNumber + " " + name);
            }
        }
        Assert.True(missing.Count == 0, "Missing AutomationProperties.Name/LabeledBy: " + string.Join(", ", missing));
    }
}
