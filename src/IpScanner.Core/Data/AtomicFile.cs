using System.IO;
using System.Text;

namespace IpScanner.Core.Data;

/// <summary>Crash-safe file replacement shared by the config, overrides and profile stores.</summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="bytes"/> to a sibling temp file, then swaps it in with an
    /// OS-atomic rename. A crash or power loss mid-write corrupts only the throwaway temp file;
    /// <c>File.Replace</c> keeps one prior generation as <c>.bak</c>. A folder the app created is
    /// locked down before the first byte is written; in any other folder only the files get a
    /// private ACL. The temp file is always created new with that ACL, never reused. Throws on failure.</summary>
    public static void Write(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var owned = Path.GetDirectoryName(full) is { } dir && FileProtection.PrepareDirectory(dir);
        var tmp = full + ".tmp";
        try
        {
            using (var stream = FileProtection.CreatePrivateFile(tmp))
                stream.Write(bytes, 0, bytes.Length);
            if (File.Exists(full))
                File.Replace(tmp, full, full + ".bak");
            else
                File.Move(tmp, full);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
        if (!owned) FileProtection.RestrictFile(full);
    }

    /// <summary>UTF-8 (no BOM) text variant of <see cref="Write(string, byte[])"/>.</summary>
    public static void WriteText(string path, string content) => Write(path, new UTF8Encoding(false).GetBytes(content));

    /// <summary>Renames a file that could not be read to <c>name.unreadable-yyyyMMdd-HHmmss</c>
    /// beside it so a later save cannot overwrite it. Returns the new path, or null when it could
    /// not be moved.</summary>
    public static string? MoveAside(string path)
    {
        try
        {
            var target = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (int i = 2; File.Exists(target); i++)
                target = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}-{i}";
            File.Move(path, target);
            return target;
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Reading a file failed", ex); return null; /* best-effort: the caller proceeds with the backup or an empty store */ }
    }
}
