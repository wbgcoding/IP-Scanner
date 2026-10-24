using System.IO;
using IpScanner.Core.Cli;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

/// <summary>Vets a config file taken from another machine before it replaces the live settings:
/// values that would not be accepted when typed by hand keep the current value instead.</summary>
public static class ConfigImport
{
    /// <summary>Syntax check shared with the typed path fields.</summary>
    public static bool IsValidPathText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { Path.GetFullPath(path); return true; }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Reading the config file failed", ex); return false; }
    }

    /// <summary>The imported settings with every unsafe or foreign value replaced by the current
    /// one, plus the conf keys whose current value was kept.</summary>
    public static (ScanConfig Result, IReadOnlyList<string> KeptKeys) Sanitize(ScanConfig current, ScanConfig imported)
    {
        var result = imported.Clone();
        var kept = new List<string>();

        if (!UsableFolder(ScanConfig.ResolvePath(imported.DatabasePath), isFile: true))
        {
            result.DatabasePath = current.DatabasePath;
            kept.Add("database_path");
        }
        if (!UsableFolder(ScanConfig.ResolvePath(imported.OutputDirectory), isFile: false))
        {
            result.OutputDirectory = current.OutputDirectory;
            kept.Add("output_directory");
        }
        // An empty config folder means "the default one" and is always fine.
        if (imported.ConfigDirectory.Trim().Length > 0 &&
            !UsableFolder(ScanConfig.ResolvePath(imported.ConfigDirectory), isFile: false))
        {
            result.ConfigDirectory = current.ConfigDirectory;
            kept.Add("config_directory");
        }

        // A crafted file must not smuggle in thousands of targets: every list is capped.
        result.Subnets = Capped(imported.Subnets, "subnet", kept);
        result.PinnedIps = Capped(imported.PinnedIps, "pinned_ips", kept);
        result.InternetHosts = Capped(imported.InternetHosts, "internet_hosts", kept);
        result.CustomPorts = Capped(imported.CustomPorts, "custom_ports", kept);
        result.ScanPresets = Capped(imported.ScanPresets, "scan_presets", kept, ScanPresets.MaxUserPresets);

        // Profiles are local data: the file can neither remove them nor switch to another one
        // (the imported lists land in the profile that is active now, and switching without
        // stashing them first would overwrite the stored lists of the other profile).
        result.ProfileNames = new List<string>(current.ProfileNames);
        result.ActiveProfile = current.ActiveProfile;
        var local = new HashSet<string>(current.ProfileNames, StringComparer.OrdinalIgnoreCase);
        if (imported.ProfileNames.Any(n => !local.Contains(n))) kept.Add("profile_names");
        if (imported.ActiveProfile.Length > 0 &&
            !string.Equals(imported.ActiveProfile, current.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            kept.Add("active_profile");
        return (result, kept);
    }

    /// <summary>True for UNC paths (also written with forward slashes or as \\?\ / \\.\ device
    /// paths) and for drive letters that map to a network share. A conf from another machine
    /// must not point reports or the database at a remote host.</summary>
    internal static bool IsNetworkPath(string path)
    {
        var t = path.Trim();
        if (t.StartsWith(@"\\") || t.StartsWith("//") || t.StartsWith(@"\/") || t.StartsWith(@"/\")) return true;
        try
        {
            var full = Path.GetFullPath(t);
            if (full.StartsWith(@"\\")) return true;
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Applying the config file failed", ex); return false; }
    }

    /// <summary>Most entries one imported list may carry.</summary>
    internal const int MaxListEntries = 1000;

    private static List<string> Capped(List<string> list, string key, List<string> kept, int max = MaxListEntries)
    {
        if (list.Count <= max) return new List<string>(list);
        kept.Add(key);
        return list.Take(max).ToList();
    }

    private static bool UsableFolder(string path, bool isFile)
    {
        if (!IsValidPathText(path) || IsNetworkPath(path)) return false;
        var folder = isFile ? Path.GetDirectoryName(Path.GetFullPath(path)) : path;
        return !string.IsNullOrEmpty(folder) && FileProtection.IsProtectableDirectory(folder);
    }
}
