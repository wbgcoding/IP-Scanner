using Xunit;

namespace IpScanner.Tests;

public class HeaderTwoRowTests
{
    private const double Need = 780;

    [Fact]
    public void WideEnough_StaysOnOneRow()
        => Assert.False(MainWindow.HeaderNeedsTwoRows(Need, Need, twoRows: false));

    [Fact]
    public void TooNarrow_MovesProgressToSecondRow()
        => Assert.True(MainWindow.HeaderNeedsTwoRows(Need - 1, Need, twoRows: false));

    [Fact]
    public void OnceWrapped_NeedsClearlyMoreRoomToGoBack()
    {
        Assert.True(MainWindow.HeaderNeedsTwoRows(Need + 39, Need, twoRows: true));
        Assert.False(MainWindow.HeaderNeedsTwoRows(Need + 40, Need, twoRows: true));
    }

    [Fact]
    public void UnmeasuredHeader_DoesNotWrap()
        => Assert.False(MainWindow.HeaderNeedsTwoRows(100, -1, twoRows: false));

    [Fact]
    public void ScaledWindow_UsesContentUnits()
    {
        // 1320 px at 200 % leaves 660 DIPs.
        Assert.True(MainWindow.HeaderNeedsTwoRows(1320 / 2.0, Need, twoRows: false));
    }

    [Fact]
    public void AfterTheSwitch_CapEqualsViewport()
    {
        // The wrapped header and filter row are narrower than the viewport, so the cap is the viewport.
        Assert.Equal(700, MainWindow.MainAreaCap(700, 1.0, headerWidth: 640, filterRow: 420), 3);
        Assert.Equal(350, MainWindow.MainAreaCap(700, 2.0, headerWidth: 340, filterRow: 300), 3);
    }

    [Fact]
    public void UnwrappedWideHeader_StillWidensThePage()
        => Assert.Equal(780, MainWindow.MainAreaCap(700, 1.0, headerWidth: 780, filterRow: 420), 3);
}
