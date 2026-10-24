using System.IO;
using System.Text.Json;

namespace IpScanner.Core.Data;

/// <summary>One named profile's own subnets/pinned IPs/internet hosts.
/// The default (unnamed) profile never appears here - its lists ARE ScanConfig's
/// own Subnets/PinnedIps/InternetHosts fields, so existing users need no migration
/// when this feature is added.</summary>
public sealed class ProfileData
{
    public List<string> Subnets { get; set; } = new();
    public List<string> PinnedIps { get; set; } = new();
    public List<string> InternetHosts { get; set; } = new();
}

/// <summary>Reads/writes the name -> ProfileData map for every profile except the built-in
/// default. Kept apart from the ConfigManager keys, since the number and names of profiles are
/// dynamic - the fixed key=value .conf format only needs to remember which names
/// exist (ScanConfig.ProfileNames) and which one is active. The map lives in the sealed
/// settings file; a plain <c>profiles.json</c> from an earlier version is migrated once
/// (<c>{ "version": 1, "profiles": { ... } }</c>, or the older bare map) and then deleted.</summary>
public static class ProfileStore
{
    private const string FileName = "profiles.json";
    private const int CurrentVersion = 1;

    public static string PathFor(string configDir) => Path.Combine(configDir, FileName);

    /// <summary>Loads the profiles from the sealed settings in <paramref name="dataDir"/>, migrating a
    /// plain profiles.json from <paramref name="configDir"/> on the way. With an unavailable key the
    /// result is empty, the plain file stays untouched and nothing is written.</summary>
    public static Dictionary<string, ProfileData> Load(string configDir, string dataDir, out LoadProblem problem)
    {
        var data = SealedSettings.Load(dataDir, out problem);
        if (problem == LoadProblem.KeyUnavailable) return new();

        var plainPath = PathFor(configDir);
        if (!File.Exists(plainPath)) return data.Profiles;

        var plain = LoadPlain(configDir, out var plainProblem);
        if (problem == LoadProblem.None) problem = plainProblem;
        if (data.Profiles.Count > 0) return data.Profiles;   // the sealed copy wins; the stale plain file is left alone

        if (plain.Count > 0 && Save(dataDir, plain))
        {
            // Only a successful sealed write may delete the readable original and its backup.
            try { File.Delete(plainPath); File.Delete(plainPath + ".bak"); } catch { /* best-effort */ }
        }
        return plain.Count > 0 ? plain : data.Profiles;
    }

    /// <summary>Writes the profiles into the sealed settings; false when the key is unavailable or the write failed.</summary>
    public static bool Save(string dataDir, Dictionary<string, ProfileData> profiles)
        => SealedSettings.Update(dataDir, d => d.Profiles = profiles);

    /// <summary>Stashes the outgoing profile's live lists under its name, loads the incoming
    /// profile's lists (empty for a new one) into <paramref name="config"/> and persists the map through
    /// <paramref name="save"/>. When saving fails, config and map are put back exactly as they were
    /// and false is returned, so a switch never loses lists that were not stored anywhere.</summary>
    public static bool TrySwitch(Models.ScanConfig config, Dictionary<string, ProfileData> profiles,
        string name, Func<bool> save)
    {
        var previous = config.ActiveProfile;
        var hadStash = profiles.TryGetValue(previous, out var oldStash);
        var (subnets, pinned, hosts) = (config.Subnets, config.PinnedIps, config.InternetHosts);

        profiles[previous] = new ProfileData { Subnets = subnets, PinnedIps = pinned, InternetHosts = hosts };
        if (profiles.TryGetValue(name, out var data))
        {
            config.Subnets = data.Subnets;
            config.PinnedIps = data.PinnedIps;
            config.InternetHosts = data.InternetHosts;
        }
        else
        {
            config.Subnets = new();
            config.PinnedIps = new();
            config.InternetHosts = new();
        }
        config.ActiveProfile = name;
        if (save()) return true;

        if (hadStash) profiles[previous] = oldStash!; else profiles.Remove(previous);
        (config.Subnets, config.PinnedIps, config.InternetHosts) = (subnets, pinned, hosts);
        config.ActiveProfile = previous;
        return false;
    }

    /// <summary>Applies <paramref name="edit"/> to the map and persists it through <paramref name="save"/>;
    /// when saving fails the map is restored and false is returned.</summary>
    public static bool TryEdit(Dictionary<string, ProfileData> profiles, Action edit, Func<bool> save)
    {
        var snapshot = new Dictionary<string, ProfileData>(profiles);
        edit();
        if (save()) return true;
        profiles.Clear();
        foreach (var (key, value) in snapshot) profiles[key] = value;
        return false;
    }

    /// <summary>Reads the plain profiles.json. A file that cannot be read is moved aside and the last
    /// backup is used instead; <paramref name="problem"/> says which of the two happened.</summary>
    internal static Dictionary<string, ProfileData> LoadPlain(string configDir, out LoadProblem problem)
    {
        problem = LoadProblem.None;
        var path = PathFor(configDir);
        if (!File.Exists(path)) return new();
        if (TryRead(path, out var map, out var newer)) return map;
        if (newer)
        {
            problem = LoadProblem.NewerFormat;   // left in place; a build that understands it can read it
            return new();
        }

        var movedAside = AtomicFile.MoveAside(path) is not null;
        if (TryRead(path + ".bak", out var backup, out _))
        {
            problem = LoadProblem.RestoredFromBackup;
            // Only a file that was moved aside may be replaced; a locked one keeps its backup untouched.
            if (movedAside)
            {
                try { SavePlain(configDir, backup); } catch { /* best-effort: the backup stays on disk */ }
            }
            return backup;
        }
        problem = LoadProblem.Unreadable;
        return new();
    }

    private static bool TryRead(string file, out Dictionary<string, ProfileData> map, out bool newerFormat)
    {
        map = new();
        newerFormat = false;
        try
        {
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var profiles = root;
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number)
            {
                if (version.GetInt32() > CurrentVersion) { newerFormat = true; return false; }   // written by a newer build
                if (!root.TryGetProperty("profiles", out profiles) || profiles.ValueKind != JsonValueKind.Object) return false;
            }
            map = profiles.Deserialize<Dictionary<string, ProfileData>>() ?? new();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Config, "Reading the profiles failed", ex);
            map = new();
            return false;
        }
    }

    internal static void SavePlain(string configDir, Dictionary<string, ProfileData> profiles)
    {
        var json = JsonSerializer.Serialize(new { version = CurrentVersion, profiles },
            new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteText(PathFor(configDir), json);
    }
}
