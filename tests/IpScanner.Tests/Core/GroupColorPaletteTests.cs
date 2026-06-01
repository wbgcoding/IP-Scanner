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
}
