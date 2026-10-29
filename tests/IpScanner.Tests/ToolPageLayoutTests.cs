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

    [Fact]
    public void PortCheck_TablesSitInStarRowsWithASplitterBetween()
    {
        var doc = Load("PortCheckTool.xaml");
        var grid = doc.Root!.Elements(Wpf + "Grid").Single();
        var rows = grid.Element(Wpf + "Grid.RowDefinitions")!.Elements().ToList();
        Assert.Equal("3*", (string?)rows[2].Attribute("Height"));
        Assert.Equal("2*", (string?)rows[4].Attribute("Height"));
        var splitter = grid.Element(Wpf + "GridSplitter")!;
        Assert.Equal("3", (string?)splitter.Attribute("Grid.Row"));
        Assert.Equal("6", (string?)splitter.Attribute("Height"));
        Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("SizeNS", (string?)splitter.Attribute("Cursor"));
        var results = grid.Elements(Wpf + "DataGrid").Single();
        Assert.Equal("2", (string?)results.Attribute("Grid.Row"));
        Assert.True(double.Parse((string)results.Attribute("MinHeight")!) >= 80);
        var checks = grid.Elements(Wpf + "Grid").Single(g => (string?)g.Attribute("Grid.Row") == "4").Element(Wpf + "DataGrid")!;
        Assert.True(double.Parse((string)checks.Attribute("MinHeight")!) >= 80);
        Assert.Null(checks.Attribute("Height"));
    }

    [Fact]
    public void PingGraph_HasTimeTicksUnderTheCanvasAndARepliesList()
    {
        var doc = Load("PingRouteTool.xaml");
        var canvas = Named(doc, "PingGraphCanvas");
        var ticks = Named(doc, "PingGraphTicks");
        Assert.Same(canvas.Parent, ticks.Parent);
        Assert.Contains(ticks, canvas.ElementsAfterSelf());
        Assert.Equal("{Binding GraphTicks}", (string?)ticks.Attribute("ItemsSource"));
        var list = Named(doc, "PingReplies");
        Assert.Equal("{Binding Replies}", (string?)list.Attribute("ItemsSource"));
        Assert.True(double.Parse((string)Named(doc, "PingRepliesScroll").Attribute("Height")!) <= 120);
    }

    [Fact]
    public void ComboBoxStyle_SelectionBoxHonoursDisplayMemberPath()
    {
        var doc = Load(Path.Combine("..", "..", "Resources", "Styles.xaml"));
        var style = doc.Descendants(Wpf + "Style").First(s => (string?)s.Attribute("TargetType") == "ComboBox" && s.Attribute(X + "Key") is null);
        var site = style.Descendants(Wpf + "ContentPresenter").First(e => (string?)e.Attribute(X + "Name") == "contentSite");
        Assert.Equal("{TemplateBinding ItemTemplateSelector}", (string?)site.Attribute("ContentTemplateSelector"));
        Assert.Equal("{TemplateBinding SelectionBoxItemStringFormat}", (string?)site.Attribute("ContentStringFormat"));
    }
}
