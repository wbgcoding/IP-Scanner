using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

public class MessageDialogTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "IP-Scanner.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void Source_NeverCallsTheSystemMessageBox()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("MessageBox.Show", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();
        Assert.True(offenders.Count == 0, "use MessageDialog.Show instead: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CancelResult_IsNoForYesNoAndOkForNotices()
    {
        Assert.Equal(MessageDialogResult.No, MessageDialog.CancelResult(MessageDialogButtons.YesNo));
        Assert.Equal(MessageDialogResult.Ok, MessageDialog.CancelResult(MessageDialogButtons.Ok));
    }

    [Theory]
    [InlineData("Datenbank exportieren …", "Datenbank exportieren")]
    [InlineData("Merge databases ...", "Merge databases")]
    [InlineData("Large range", "Large range")]
    public void CleanTitle_DropsTheMenuEllipsis(string title, string expected)
        => Assert.Equal(expected, MessageDialog.CleanTitle(title));

    public static IEnumerable<object[]> Kinds() => Enum.GetValues<MessageDialogKind>().Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Icon_GeometryParsesAndItsBrushExistsInBothThemes(MessageDialogKind kind)
    {
        Assert.False(Geometry.Parse(MessageDialog.IconFrameData(kind)).IsEmpty());
        Assert.False(Geometry.Parse(MessageDialog.IconGlyphData(kind)).IsEmpty());
        var key = MessageDialog.IconBrushKey(kind);
        foreach (var theme in new[] { "Theme.Dark.xaml", "Theme.Light.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IpScanner", "Resources", theme));
            Assert.Contains($"x:Key=\"{key}\"", xaml);
        }
    }

    [Fact]
    public void Icon_EachSeverityHasItsOwnGlyph()
    {
        var glyphs = Enum.GetValues<MessageDialogKind>().Select(MessageDialog.IconGlyphData).Distinct().Count();
        Assert.Equal(Enum.GetValues<MessageDialogKind>().Length, glyphs);
    }

#if DEBUG
    [Fact]
    public async Task Dialog_BothButtonSetsCarryTooltipsAndAccessibleNames()
    {
        var problems = await WpfTestHost.RunAsync(() =>
        {
            var found = new List<string>();
            foreach (var buttons in Enum.GetValues<MessageDialogButtons>())
            {
                var w = new MessageDialog("Body", "Title", buttons, MessageDialogKind.Warning)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    ShowActivated = false,
                };
                w.Show();
                w.UpdateLayout();
                found.AddRange(TooltipCoverageTests.Problems("message." + buttons, w));
                if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(w)))
                    found.Add("message." + buttons + ": window has no automation name");
                w.Close();
            }
            return Task.FromResult(found);
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
#endif
}
