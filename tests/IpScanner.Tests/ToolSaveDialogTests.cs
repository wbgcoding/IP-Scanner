using System;
using System.IO;
using IpScanner.Core.Models;
using IpScanner.Views.Tools;
using Xunit;

namespace IpScanner.Tests;

public class ToolSaveDialogTests
{
    [Fact]
    public void ToolSaveDirectory_ReturnsExistingFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipscan-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ScanConfig { OutputDirectory = dir };
            Assert.Equal(dir, ToolSaveDialog.ToolSaveDirectory(config));
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void ToolSaveDirectory_NullWhenFolderMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipscan-missing-" + Guid.NewGuid().ToString("N"));
        var config = new ScanConfig { OutputDirectory = dir };
        Assert.Null(ToolSaveDialog.ToolSaveDirectory(config));
    }
}
