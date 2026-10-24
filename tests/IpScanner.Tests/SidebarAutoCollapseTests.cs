using Xunit;

namespace IpScanner.Tests;

public class SidebarAutoCollapseTests
{
    private const double Pills = 200;
    private static double Need => MainWindow.SidebarFilterRowNeed(Pills);

    [Fact]
    public void NarrowWindow_CollapsesAnOpenSidebar()
    {
        Assert.True(MainWindow.ShouldAutoCollapse(Need - 1, 1.0, Pills, open: true, autoCollapsed: false, out var restore));
        Assert.False(restore);
    }

    [Fact]
    public void WideEnoughWindow_KeepsTheSidebar()
    {
        Assert.False(MainWindow.ShouldAutoCollapse(Need, 1.0, Pills, open: true, autoCollapsed: false, out var restore));
        Assert.False(restore);
    }

    [Fact]
    public void UiScale_CountsTowardTheWidth()
    {
        // 2x scale halves the usable width in layout units.
        Assert.True(MainWindow.ShouldAutoCollapse(Need * 1.5, 2.0, Pills, open: true, autoCollapsed: false, out _));
        Assert.False(MainWindow.ShouldAutoCollapse(Need * 2, 2.0, Pills, open: true, autoCollapsed: false, out _));
    }

    [Fact]
    public void Hysteresis_RestoresOnlyWhenClearlyWide()
    {
        Assert.False(MainWindow.ShouldAutoCollapse(Need + 39, 1.0, Pills, open: false, autoCollapsed: true, out var near));
        Assert.False(near);
        Assert.False(MainWindow.ShouldAutoCollapse(Need + 40, 1.0, Pills, open: false, autoCollapsed: true, out var wide));
        Assert.True(wide);
    }

    [Fact]
    public void ManuallyClosedSidebar_IsNeverRestoredOrCollapsedAgain()
    {
        Assert.False(MainWindow.ShouldAutoCollapse(Need + 500, 1.0, Pills, open: false, autoCollapsed: false, out var restore));
        Assert.False(restore);
        Assert.False(MainWindow.ShouldAutoCollapse(Need - 100, 1.0, Pills, open: false, autoCollapsed: false, out _));
    }
}
