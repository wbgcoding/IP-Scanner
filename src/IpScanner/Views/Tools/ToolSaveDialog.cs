using System.IO;
using IpScanner.Core.Models;

namespace IpScanner.Views.Tools;

internal static class ToolSaveDialog
{
    /// <summary>The configured report folder when it exists, otherwise null so the dialog keeps its default.</summary>
    internal static string? ToolSaveDirectory(ScanConfig config)
    {
        var dir = ScanConfig.ResolvePath(config.OutputDirectory);
        return Directory.Exists(dir) ? dir : null;
    }

    internal static void ApplyInitialDirectory(Microsoft.Win32.SaveFileDialog dialog, ScanConfig config)
    {
        if (ToolSaveDirectory(config) is { } dir) dialog.InitialDirectory = dir;
    }
}
