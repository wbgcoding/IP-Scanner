using System.IO;
using System.Text;
using IpScanner.Core.Data;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

/// <summary>Writes a scan report in any of the four formats. The GUI exports and the command line both end up here.</summary>
public static class ReportFile
{
    /// <summary>Bytes of the file for a rendering: the format's encoding preamble plus the content.</summary>
    public static byte[] Bytes(string content, ReportFormat format)
    {
        var enc = ReportFormats.Encoding(format);
        return enc.GetPreamble().Concat(enc.GetBytes(content)).ToArray();
    }

    /// <summary>Writes into the reports folder as <c>network_scan_{timestamp}{-slug}.{ext}</c> and returns the path.</summary>
    public static string WriteToFolder(ScanReport report, ReportFormat format, string outputDir, string timestamp, string gatewaySlug)
    {
        outputDir = ScanConfig.ResolvePath(outputDir);
        // Restrict the reports folder to this Windows account before the first file lands in it.
        var ownedDir = FileProtection.PrepareDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.{ReportFormats.Extension(format)}");
        FileProtection.WriteReport(path, ReportWriters.Render(report, format), ReportFormats.Encoding(format), ownedDir);
        return path;
    }

    /// <summary>Writes to a path the caller chose (replacing a file that is already there).</summary>
    public static void WriteTo(string path, ScanReport report, ReportFormat format, bool showReason = false)
    {
        WriteAtomic(path, Bytes(ReportWriters.Render(report, format, showReason), format));
    }

    /// <summary>Replaces the file at <paramref name="path"/> with <paramref name="bytes"/>, creating the folder when needed.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = PrepareFolder(path);
        // A temp file next to the target, then a move: a full disk or a crash never leaves a half-written report.
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { /* best effort */ }
            throw;
        }
    }

    /// <summary>Throws when a report could not be written to <paramref name="path"/>; checked before a long scan starts.</summary>
    public static void CheckWritable(string path)
    {
        var dir = PrepareFolder(path);
        if (Directory.Exists(path)) throw new IOException("the path is a folder");
        var probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }

    private static string PrepareFolder(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(dir);
        return dir;
    }
}
