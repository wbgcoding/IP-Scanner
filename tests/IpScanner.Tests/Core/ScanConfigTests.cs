using System;
using System.IO;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class ScanConfigTests
{
    [Fact]
    public void ResolvePath_RelativePath_AnchorsAtExeFolder_NotCurrentDirectory()
    {
        // A shortcut with a different "Start in" folder, or "Run as
        // Administrator" (which can change the effective process CWD), must not
        // change where a relative DatabasePath/OutputDirectory/ConfigDirectory
        // resolves - it has to stay pinned to the exe's own folder. Deliberately
        // NOT testing this by mutating Environment.CurrentDirectory: that's global
        // process state and tests run in parallel - flipping it here
        // would risk the exact cross-test flakiness this project is elsewhere
        // removing. Asserting the output is anchored at AppContext.BaseDirectory
        // is sufficient and deterministic without touching shared state.
        var resolved = ScanConfig.ResolvePath(@".\scanner.db");
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scanner.db")), resolved);
    }

    [Fact]
    public void ResolvePath_AbsolutePath_PassesThroughUnchanged()
    {
        var abs = Path.Combine(Path.GetTempPath(), "custom", "scanner.db");
        Assert.Equal(Path.GetFullPath(abs), ScanConfig.ResolvePath(abs));
    }

    [Fact]
    public void ResolvePath_EmptyPath_ReturnsExeFolder()
    {
        Assert.Equal(AppContext.BaseDirectory, ScanConfig.ResolvePath(""));
    }

    [Fact]
    public void PaletteHeat_CustomThresholds_ColorsFollowConfiguredBounds()
    {
        var (m1, m2, m3, m4) = (IpScanner.Core.Palette.HeatMs1, IpScanner.Core.Palette.HeatMs2,
            IpScanner.Core.Palette.HeatMs3, IpScanner.Core.Palette.HeatMs4);
        try
        {
            IpScanner.Core.Palette.HeatMs1 = 20;
            IpScanner.Core.Palette.HeatMs2 = 40;
            IpScanner.Core.Palette.HeatMs3 = 80;
            IpScanner.Core.Palette.HeatMs4 = 150;

            Assert.Equal(IpScanner.Core.Palette.Green, IpScanner.Core.Palette.Heat(20));
            Assert.Equal(IpScanner.Core.Palette.Lime, IpScanner.Core.Palette.Heat(40));
            Assert.Equal(IpScanner.Core.Palette.Yellow, IpScanner.Core.Palette.Heat(80));
            Assert.Equal(IpScanner.Core.Palette.Peach, IpScanner.Core.Palette.Heat(150));
            Assert.Equal(IpScanner.Core.Palette.Red, IpScanner.Core.Palette.Heat(151));
            Assert.Equal(IpScanner.Core.Palette.MidGray, IpScanner.Core.Palette.Heat(null));
        }
        finally
        {
            (IpScanner.Core.Palette.HeatMs1, IpScanner.Core.Palette.HeatMs2,
                IpScanner.Core.Palette.HeatMs3, IpScanner.Core.Palette.HeatMs4) = (m1, m2, m3, m4);
        }
    }
}
