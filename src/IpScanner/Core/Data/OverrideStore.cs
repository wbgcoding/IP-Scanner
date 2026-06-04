using System.IO;

namespace IpScanner.Core.Data;

/// <summary>
/// Manual hostname/group overrides, keyed by MAC (preferred, stable across
/// IP changes) or IP as fallback. Stored as a small tab-separated file next
/// to the database; loaded once at startup, saved on every change.
/// </summary>
public sealed class OverrideStore
{
    public sealed record Entry(string? Hostname, int? Group);

    private readonly Dictionary<string, Entry> _entries = new();
    private string? _path;

    public void Load(string path)
    {
        _path = path;
        _entries.Clear();
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split('\t');
                if (p.Length < 3 || p[0].Length == 0) continue;
                var host = p[1].Length > 0 ? p[1] : null;
                int? group = int.TryParse(p[2], out var g) && g >= 0 ? g : null;
                if (host is not null || group is not null)
                    _entries[p[0]] = new Entry(host, group);
            }
        }
        catch { /* unreadable -> start empty */ }
    }

    public Entry? Get(string? mac, string ip) =>
        mac is not null && _entries.TryGetValue(Key(mac), out var byMac) ? byMac
        : _entries.TryGetValue(ip, out var byIp) ? byIp : null;

    public void SetHostname(string? mac, string ip, string? hostname) =>
        Update(mac, ip, e => e with { Hostname = hostname });

    public void SetGroup(string? mac, string ip, int? group) =>
        Update(mac, ip, e => e with { Group = group });

    private void Update(string? mac, string ip, Func<Entry, Entry> change)
    {
        var next = change(Get(mac, ip) ?? new Entry(null, null));
        // The device may have an older entry under its IP from before its MAC
        // was known — drop both representations, then store the current one.
        _entries.Remove(ip);
        if (mac is not null) _entries.Remove(Key(mac));
        if (next.Hostname is not null || next.Group is not null)
            _entries[mac is not null ? Key(mac) : ip] = next;
        Save();
    }

    private static string Key(string mac) => mac.ToUpperInvariant();

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllLines(_path, _entries.Select(kv =>
                $"{kv.Key}\t{kv.Value.Hostname ?? ""}\t{kv.Value.Group?.ToString() ?? ""}"));
        }
        catch { /* best-effort */ }
    }
}
