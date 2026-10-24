using IpScanner.Core.Scanner;
using System.Linq;
using Xunit;

namespace IpScanner.Tests.Core;

public class GroupColorPaletteTests
{
    [Fact]
    public void Sequence_IsNonEmpty_AndDistinct()
    {
        var seq = GroupColorPalette.Sequence;
        Assert.NotEmpty(seq);
        Assert.Equal(seq.Count, seq.Distinct().Count());
    }

    [Fact]
    public void ColorForIndex_WrapsAround()
    {
        var c0 = GroupColorPalette.ColorForIndex(0);
        var cWrap = GroupColorPalette.ColorForIndex(GroupColorPalette.Sequence.Count);
        Assert.Equal(c0, cWrap);
    }

    [Fact]
    public void ColorForIndex_IsStableAcrossCalls_NotRandomizedPerScan()
    {
        // Same device/group across two scan runs must keep the same color -
        // the palette has no per-run shuffle.
        var first = Enumerable.Range(0, 5).Select(GroupColorPalette.ColorForIndex).ToList();
        var second = Enumerable.Range(0, 5).Select(GroupColorPalette.ColorForIndex).ToList();
        Assert.Equal(first, second);
    }
}
