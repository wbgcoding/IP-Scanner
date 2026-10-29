using System.Windows.Controls;
using IpScanner;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MainWindowShortcutTests
{
    [Fact]
    public void CopyRowShortcut_LeavesTextEditorsToHandleCopy()
    {
        Assert.False(MainWindow.ShouldCopySelectedRow(true, typeof(TextBox)));
        Assert.False(MainWindow.ShouldCopySelectedRow(true, typeof(RichTextBox)));
        Assert.True(MainWindow.ShouldCopySelectedRow(true, typeof(object)));
        Assert.False(MainWindow.ShouldCopySelectedRow(false, typeof(object)));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData(null, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, "Network connection lost")]
    [InlineData(false, false, null)]
    [InlineData(false, true, "Network connection restored")]
    public void NetworkTransitionMessage_NamesOnlyRealFlips(bool? before, bool now, string? expected)
    {
        Assert.Equal(expected, MainWindow.NetworkTransitionMessage(before, now));
    }

    [Theory]
    [InlineData("5", 10, true)]
    [InlineData("999", 300, true)]
    [InlineData("50", 50, false)]
    [InlineData("", 10, false)]
    [InlineData("abc", 10, false)]
    public void Clamp_ReportsWhetherTheTypedValueWasCorrected(string text, int expected, bool changed)
    {
        Assert.Equal((expected, changed), MainWindow.Clamp(text, 10, 300, 10));
    }
}
