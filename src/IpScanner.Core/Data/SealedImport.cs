using System.IO;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

/// <summary>Exports do not carry the SNMP community and the recent Wake-on-LAN targets (they live
/// in the sealed settings), so an import must not reset them to defaults when the file lacks them.</summary>
public static class SealedImport
{
    /// <summary>True when the config file has an active (uncommented) line for <paramref name="key"/>.</summary>
    public static bool FileHasKey(string path, string key)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;
                var idx = line.IndexOf('=');
                if (idx > 0 && string.Equals(line[..idx].Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (IOException ex) { Log.Warn(LogArea.Config, "Sealed import read failed", ex); }
        catch (UnauthorizedAccessException ex) { Log.Warn(LogArea.Config, "Sealed import was denied", ex); }
        return false;
    }

    /// <summary>Copies the current sealed values into <paramref name="imported"/> for every key the
    /// imported file did not carry.</summary>
    public static void KeepCurrentUnlessImported(ScanConfig imported, ScanConfig current, string importedPath)
    {
        if (!FileHasKey(importedPath, "snmp_community")) imported.SnmpCommunity = current.SnmpCommunity;
        if (!FileHasKey(importedPath, "recent_wake_targets")) imported.RecentWakeTargets = new List<string>(current.RecentWakeTargets);
    }
}
