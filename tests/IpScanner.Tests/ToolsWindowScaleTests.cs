using System.Windows;
using System.Windows.Media;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

public class ToolsWindowScaleTests
{
    [Fact]
    public void ScaleAndFlow_FollowsTextSizeAndDirection()
    {
        var (t100, f100) = ToolsWindow.ScaleAndFlow(1.0, false);
        Assert.Null(t100);
        Assert.Equal(FlowDirection.LeftToRight, f100);

        var (t200, _) = ToolsWindow.ScaleAndFlow(2.0, false);
        Assert.Equal(2.0, Assert.IsType<ScaleTransform>(t200).ScaleX);

        var (_, flow) = ToolsWindow.ScaleAndFlow(1.0, true);
        Assert.Equal(FlowDirection.RightToLeft, flow);

        var (t300, _) = ToolsWindow.ScaleAndFlow(3.0, false);
        Assert.Equal(2.0, Assert.IsType<ScaleTransform>(t300).ScaleX);
    }

    [Fact]
    public void ScaledMinSize_FollowsFactorAndStaysInsideWorkArea()
    {
        Assert.Equal((820.0, 560.0), ToolsWindow.ScaledMinSize(1.0, 1920, 1040));
        Assert.Equal((1640.0, 1040.0), ToolsWindow.ScaledMinSize(2.0, 1920, 1040));
        Assert.Equal((1500.0, 1120.0), ToolsWindow.ScaledMinSize(2.0, 1500, 1200));
        Assert.Equal((1640.0, 1120.0), ToolsWindow.ScaledMinSize(3.0, 2000, 2000));
    }

    [Fact]
    public void OwnerRepair_RestoresAMainWindowThatTheCloseMinimizedOrShrank()
    {
        var big = new Rect(0, 0, 1400, 900);
        Assert.Equal(((WindowState?)WindowState.Normal, (Rect?)big),
            ToolsWindow.OwnerRepair(WindowState.Normal, big, WindowState.Minimized, big));
        Assert.Equal(((WindowState?)WindowState.Maximized, (Rect?)big),
            ToolsWindow.OwnerRepair(WindowState.Maximized, big, WindowState.Minimized, big));
        Assert.Equal(((WindowState?)null, (Rect?)big),
            ToolsWindow.OwnerRepair(WindowState.Normal, big, WindowState.Normal, new Rect(0, 0, 300, 200)));
    }

    [Fact]
    public void OwnerRepair_LeavesAnUntouchedOrUserMinimizedMainWindowAlone()
    {
        var r = new Rect(0, 0, 1400, 900);
        Assert.Equal(((WindowState?)null, (Rect?)null), ToolsWindow.OwnerRepair(WindowState.Normal, r, WindowState.Normal, r));
        Assert.Equal(((WindowState?)null, (Rect?)null), ToolsWindow.OwnerRepair(WindowState.Minimized, r, WindowState.Minimized, r));
        Assert.Equal(((WindowState?)null, (Rect?)null), ToolsWindow.OwnerRepair(WindowState.Maximized, r, WindowState.Maximized, new Rect(0, 0, 100, 100)));
    }
}
