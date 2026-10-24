using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace IpScanner.Tests;

/// <summary>Structure checks on the tool page XAML that no view model test can see.</summary>
public class ToolPageLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static XDocument Load(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx")))
                return XDocument.Load(Path.Combine(d.FullName, "src", "IpScanner", "Views", "Tools", file));
        throw new InvalidOperationException("Could not locate repo root");
    }

    internal static XElement Named(XDocument doc, string name)
        => doc.Descendants().First(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void SkipAndChecksBoxes_ShareTheSameRowStructure()
    {
        var doc = Load("ScanToolPage.xaml");
        foreach (var name in new[] { "SkipBox", "ChecksBox" })
        {
            var box = Named(doc, name);
            var kids = box.Elements().ToList();
            Assert.Equal(2, kids.Count);
            Assert.Equal(Wpf + "TextBlock", kids[0].Name);
            var row = kids[1];
            Assert.Equal(Wpf + "StackPanel", row.Name);
            Assert.Equal("32", (string?)row.Attribute("Height"));
            Assert.Equal("Horizontal", (string?)row.Attribute("Orientation"));
            Assert.All(row.Elements(Wpf + "CheckBox"), c => Assert.Equal("Center", (string?)c.Attribute("VerticalAlignment")));
            Assert.NotEmpty(row.Elements(Wpf + "CheckBox"));
        }
    }

    [Fact]
    public void ScanToolPage_HasNoNumberedSteps()
    {
        var text = Load("ScanToolPage.xaml").ToString();
        Assert.DoesNotContain("StepTarget", text);
        Assert.DoesNotContain("ScanStepHeading", text);
    }
}
