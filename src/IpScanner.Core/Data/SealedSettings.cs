using System.IO;
using System.Text.Json;

namespace IpScanner.Core.Data;

/// <summary>The sensitive part of the settings: SNMP community, recent Wake-on-LAN targets and the
/// named profiles. Content of <c>settings.dat</c>.</summary>
public sealed class SealedSettingsData
{
    /// <summary>Null when never stored (lets a legacy value from the .conf win exactly once).</summary>
    public string? SnmpCommunity { get; set; }
    public List<string> RecentWakeTargets { get; set; } = new();
    public Dictionary<string, ProfileData> Profiles { get; set; } = new();
    /// <summary>True when the file existed and was read (even with empty content).</summary>
    public bool Existed { get; set; }
}

/// <summary>One sealed file <c>settings.dat</c> next to <c>scanner.db</c>, encrypted with the same
/// per-installation key as the overrides and written atomically with a <c>.bak</c>.
/// Content: <c>{ "version": 1, "snmpCommunity": ..., "recentWakeTargets": [...], "profiles": {...} }</c>.</summary>
public static class SealedSettings
{
    public const string FileName = "settings.dat";
    private const int CurrentVersion = 1;
    private static readonly object Gate = new();
    // Folders whose settings.dat could not be read and could not be moved aside (a transient lock):
    // nothing is written there until a later load reads it, so the good copy is never rotated away.
    private static readonly HashSet<string> ReadOnlyDirs = new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string dataDir) => Path.GetFullPath(dataDir).TrimEnd('\\');
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class FileModel
    {
        public int Version { get; set; }
        public string? SnmpCommunity { get; set; }
        public List<string>? RecentWakeTargets { get; set; }
        public Dictionary<string, ProfileData>? Profiles { get; set; }
    }

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Folder that holds <c>settings.dat</c> for a configured database path (next to the database).</summary>
    public static string DirFor(string databasePath)
        => Path.GetDirectoryName(Models.ScanConfig.ResolvePath(databasePath)) ?? AppContext.BaseDirectory;

    /// <summary>Startup load into the runtime config. Values an older conf file still carries
    /// (SNMP community, recent Wake-on-LAN targets) move into the sealed file once, unless it already
    /// holds values. Returns false while such legacy values are not safely sealed (the key is
    /// unavailable or the write failed): they stay in the runtime config and the conf keeps them until
    /// a later sealed write succeeds. With an unavailable key and nothing legacy to protect, the
    /// runtime values fall back to defaults and nothing is written.</summary>
    public static bool ApplyTo(Models.ScanConfig cfg, string dataDir, out LoadProblem problem)
    {
        var data = Load(dataDir, out problem);
        var legacySnmp = cfg.SnmpCommunity.Trim();
        if (legacySnmp.Length > Models.ScanConfig.Bounds.SnmpCommunityMaxLength)
            legacySnmp = legacySnmp[..Models.ScanConfig.Bounds.SnmpCommunityMaxLength];
        bool hasLegacySnmp = legacySnmp.Length > 0 && legacySnmp != "public";

        if (problem == LoadProblem.KeyUnavailable)
        {
            if (!hasLegacySnmp && cfg.RecentWakeTargets.Count == 0)
            {
                cfg.SnmpCommunity = "public";
                return true;
            }
            cfg.SnmpCommunity = hasLegacySnmp ? legacySnmp : "public";
            return false;
        }

        var snmp = data.SnmpCommunity;
        var recent = data.RecentWakeTargets;
        bool migrate = false;
        if (snmp is null && hasLegacySnmp) { snmp = legacySnmp; migrate = true; }
        if (recent.Count == 0 && cfg.RecentWakeTargets.Count > 0) { recent = cfg.RecentWakeTargets.ToList(); migrate = true; }

        bool sealedOk = !migrate || Update(dataDir, d => { d.SnmpCommunity = snmp; d.RecentWakeTargets = recent.ToList(); });

        cfg.SnmpCommunity = string.IsNullOrEmpty(snmp) ? "public" : snmp;
        cfg.RecentWakeTargets = recent.ToList();
        return sealedOk;
    }

    /// <summary>Reads the file. When the key is unavailable the returned data is empty and
    /// <paramref name="problem"/> is <see cref="LoadProblem.KeyUnavailable"/>: callers must then not
    /// save, so the sealed file is never overwritten. An unreadable file is moved aside and the last
    /// backup is used.</summary>
    public static SealedSettingsData Load(string dataDir, out LoadProblem problem)
    {
        lock (Gate)
        {
            problem = LoadProblem.None;
            var crypto = LocalCrypto.TryLoad(Path.Combine(dataDir, KnownDevicesDb.KeyFileName));
            if (crypto is null) { problem = LoadProblem.KeyUnavailable; return new(); }

            var path = PathFor(dataDir);
            if (!File.Exists(path)) return new();
            if (TryRead(crypto, path, out var data, out var newer)) { ReadOnlyDirs.Remove(Normalize(dataDir)); return data; }
            if (newer)
            {
                // Written by a newer build: keep the file (and its backup) exactly as they are.
                ReadOnlyDirs.Add(Normalize(dataDir));
                problem = LoadProblem.NewerFormat;
                return new();
            }

            if (AtomicFile.MoveAside(path) is null)
            {
                // The file may only be locked for a moment: leave it and its backup alone and stay read-only.
                ReadOnlyDirs.Add(Normalize(dataDir));
                problem = LoadProblem.Unreadable;
                return new();
            }
            ReadOnlyDirs.Remove(Normalize(dataDir));
            if (TryRead(crypto, path + ".bak", out var backup, out _))
            {
                problem = LoadProblem.RestoredFromBackup;
                WriteLocked(crypto, dataDir, backup);
                return backup;
            }
            problem = LoadProblem.Unreadable;
            return new();
        }
    }

    /// <summary>Writes the file; false when the key is unavailable or the write failed.</summary>
    public static bool Save(string dataDir, SealedSettingsData data)
    {
        lock (Gate)
        {
            if (ReadOnlyDirs.Contains(Normalize(dataDir))) return false;
            var crypto = LocalCrypto.TryLoad(Path.Combine(dataDir, KnownDevicesDb.KeyFileName));
            return crypto is not null && WriteLocked(crypto, dataDir, data);
        }
    }

    /// <summary>Read-modify-write of the file, so one writer (e.g. the SNMP box) does not drop what
    /// another one (profiles) stored. False when the key is unavailable or the write failed.</summary>
    public static bool Update(string dataDir, Action<SealedSettingsData> change)
    {
        lock (Gate)
        {
            var crypto = LocalCrypto.TryLoad(Path.Combine(dataDir, KnownDevicesDb.KeyFileName));
            if (crypto is null) return false;
            var path = PathFor(dataDir);
            SealedSettingsData data;
            if (!File.Exists(path)) data = new();
            else if (!TryRead(crypto, path, out data!, out _)) return false;   // unreadable: Load handles recovery, never overwrite here
            change(data);
            return WriteLocked(crypto, dataDir, data);
        }
    }

    /// <summary>Carries the sealed data over when the database (and with it the key) moves to another
    /// folder: the old folder's data is decrypted and written under the new folder's key. Values the
    /// new folder already holds win; the old folder is left untouched. False when nothing could be
    /// moved safely (a key or file on either side is unusable), so the caller keeps using the old folder.</summary>
    public static bool Relocate(string fromDir, string toDir)
    {
        if (string.Equals(Path.GetFullPath(fromDir).TrimEnd('\\'), Path.GetFullPath(toDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)) return true;
        lock (Gate)
        {
            if (!File.Exists(PathFor(fromDir))) return true;   // nothing sealed yet
            var from = Load(fromDir, out var problem);
            if (problem is LoadProblem.KeyUnavailable or LoadProblem.Unreadable or LoadProblem.NewerFormat) return false;

            var crypto = LocalCrypto.TryLoad(Path.Combine(toDir, KnownDevicesDb.KeyFileName));
            if (crypto is null) return false;
            var merged = from;
            var target = PathFor(toDir);
            if (File.Exists(target))
            {
                if (!TryRead(crypto, target, out var existing, out _)) return false;   // never overwrite what cannot be read
                merged = new SealedSettingsData
                {
                    SnmpCommunity = existing.SnmpCommunity ?? from.SnmpCommunity,
                    RecentWakeTargets = existing.RecentWakeTargets.Count > 0 ? existing.RecentWakeTargets : from.RecentWakeTargets,
                    Profiles = new Dictionary<string, ProfileData>(from.Profiles),
                };
                foreach (var (name, data) in existing.Profiles) merged.Profiles[name] = data;
            }
            return WriteLocked(crypto, toDir, merged);
        }
    }

    private static bool TryRead(LocalCrypto crypto, string file, out SealedSettingsData data, out bool newerFormat)
    {
        data = new();
        newerFormat = false;
        try
        {
            if (!File.Exists(file)) return false;
            var body = crypto.Open(File.ReadAllBytes(file));
            if (body is null) return false;
            var model = JsonSerializer.Deserialize<FileModel>(body, Json);
            if (model is null || model.Version < 1) return false;
            if (model.Version > CurrentVersion) { newerFormat = true; return false; }
            data = new SealedSettingsData
            {
                SnmpCommunity = model.SnmpCommunity,
                RecentWakeTargets = model.RecentWakeTargets ?? new(),
                Profiles = model.Profiles ?? new(),
                Existed = true,
            };
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Config, "Reading the sealed settings failed", ex);
            data = new();
            return false;
        }
    }

    private static bool WriteLocked(LocalCrypto crypto, string dataDir, SealedSettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(new FileModel
            {
                Version = CurrentVersion,
                SnmpCommunity = data.SnmpCommunity,
                RecentWakeTargets = data.RecentWakeTargets,
                Profiles = data.Profiles,
            }, Json);
            AtomicFile.Write(PathFor(dataDir), crypto.Seal(json));
            return true;
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Saving the sealed settings failed", ex); return false; /* best-effort: the in-memory state stays authoritative */ }
    }
}
