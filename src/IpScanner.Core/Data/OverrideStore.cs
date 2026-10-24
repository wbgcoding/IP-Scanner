using System.IO;
using System.Text;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Data;

/// <summary>
/// Manual device overrides and notes, keyed by MAC (preferred, stable across
/// IP changes) or IP as fallback. The whole file body is sealed
/// (AesGcm, same per-installation key as <see cref="KnownDevicesDb"/>) and
/// written atomically as <c>overrides.dat</c>; a legacy plain
/// <c>overrides.conf</c> next to it is migrated once, then deleted.
/// </summary>
public sealed class OverrideStore
{
    private const int FormatVersion = 2;
    private const string FormatHeader = "# overrides-v2";

    private sealed class NewerFormatException : Exception { }
    public const int MaxNoteLength = 2000;

    /// <summary>NoHostnameRetry/Muted default false so every
    /// pre-existing (3- or 4-column) entry line still parses the same way it
    /// always did.</summary>
    public sealed record Entry(string? Hostname, string? Color, bool NoHostnameRetry = false,
        bool Muted = false, string? Note = null);

    private readonly Dictionary<string, Entry> _entries = new();
    private string? _path;
    private LocalCrypto? _crypto;
    // Set when the file could not be read or moved aside: saving would rotate the good copy away.
    private bool _readOnly;
    private bool _newerFormat;

    /// <summary>False when the key file exists but can't be unprotected (different
    /// Windows account) - overrides then behave as empty/read-only instead of throwing.</summary>
    public bool IsUsable => _crypto is not null;

    /// <summary>What went wrong during the last <see cref="Load"/>, for the startup notice.</summary>
    public LoadProblem LoadProblem { get; private set; }

    public void Load(string path)
    {
        _path = path;
        _entries.Clear();
        _readOnly = false;
        _newerFormat = false;
        LoadProblem = LoadProblem.None;
        var dir = Path.GetDirectoryName(path);
        _crypto = LocalCrypto.TryLoad(Path.Combine(dir ?? AppContext.BaseDirectory, KnownDevicesDb.KeyFileName));
        if (_crypto is null) { LoadProblem = LoadProblem.KeyUnavailable; return; }

        if (File.Exists(path)) { LoadSealed(path); return; }

        var legacy = Path.ChangeExtension(path, ".conf");
        if (!File.Exists(legacy)) return;
        try
        {
            ParseInto(File.ReadAllText(legacy));
            if (Save()) File.Delete(legacy);   // drop the plain original only once the sealed copy is on disk
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Reading the legacy override file failed", ex); _entries.Clear(); /* unreadable legacy file -> start empty, file stays */ }
    }

    /// <summary>Reads the sealed file. If it cannot be read (tampered, truncated, unknown format,
    /// IO error) it is moved aside first so a later save cannot overwrite it, then the last
    /// backup is tried.</summary>
    private void LoadSealed(string path)
    {
        if (TryReadSealed(path)) return;
        if (_newerFormat)
        {
            // Written by a newer build: keep the file and its backup exactly as they are.
            _readOnly = true;
            LoadProblem = LoadProblem.NewerFormat;
            return;
        }
        var movedAside = AtomicFile.MoveAside(path) is not null;
        _readOnly = !movedAside;
        var bak = path + ".bak";
        if (File.Exists(bak) && TryReadSealed(bak))
        {
            LoadProblem = LoadProblem.RestoredFromBackup;
            if (movedAside) Save();   // the restored content becomes the live file again
        }
        else
        {
            LoadProblem = LoadProblem.Unreadable;
        }
    }

    private bool TryReadSealed(string file)
    {
        try
        {
            var body = _crypto!.Open(File.ReadAllBytes(file));
            if (body is null) return false;
            _entries.Clear();
            ParseInto(body);
            return true;
        }
        catch (NewerFormatException)
        {
            _entries.Clear();
            _newerFormat = true;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn(LogArea.Config, "Reading the override file failed", ex);
            _entries.Clear();   // unreadable or unsupported format: the caller decides what happens next
            return false;
        }
    }

    private void ParseInto(string body)
    {
        var header = body.Split('\n', 2)[0].TrimEnd('\r');
        if (header.StartsWith("# overrides-v", StringComparison.Ordinal) && header != FormatHeader)
        {
            if (int.TryParse(header["# overrides-v".Length..], out var version) && version > FormatVersion)
                throw new NewerFormatException();
            throw new InvalidDataException($"Unsupported overrides format: {header}");
        }
        foreach (var line in body.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length < 3 || p[0].Length == 0) continue;
            var host = p[1].Length > 0 ? p[1] : null;
            var color = Core.Palette.IsHexColor(p[2]) ? p[2].ToUpperInvariant() : null;
            var noRetry = p.Length > 3 && p[3] == "1";
            var muted = p.Length > 4 && p[4] == "1";
            var note = p.Length > 5 ? DecodeNote(p[5]) : null;
            if (host is not null || color is not null || noRetry || muted || note is not null)
                _entries[p[0]] = new Entry(host, color, noRetry, muted, note);
        }
    }

    public Entry? Get(string? mac, string ip) =>
        mac is not null && _entries.TryGetValue(Key(mac), out var byMac) ? byMac
        : _entries.TryGetValue(ip, out var byIp) ? byIp : null;

    public void SetHostname(string? mac, string ip, string? hostname) =>
        Update(mac, ip, e => e with { Hostname = hostname });

    public void SetColor(string? mac, string ip, string? color) =>
        Update(mac, ip, e => e with { Color = color });

    public void SetNote(string? mac, string ip, string? note)
    {
        if (note is { Length: > MaxNoteLength })
            throw new ArgumentOutOfRangeException(nameof(note), $"Notes cannot exceed {MaxNoteLength} characters.");
        var clean = note is null ? null : NetworkText.Sanitize(note, MaxNoteLength, allowLineBreaks: true);
        Update(mac, ip, e => e with { Note = !string.IsNullOrEmpty(clean) ? clean : null });
    }

    public void PromoteToMac(string mac, string ip)
    {
        var macKey = Key(mac);
        if (!_entries.Remove(ip, out var byIp)) return;
        if (_entries.TryGetValue(macKey, out var byMac))
        {
            _entries[macKey] = new Entry(
                byMac.Hostname ?? byIp.Hostname,
                byMac.Color ?? byIp.Color,
                byMac.NoHostnameRetry || byIp.NoHostnameRetry,
                byMac.Muted || byIp.Muted,
                byMac.Note ?? byIp.Note);
        }
        else
        {
            _entries[macKey] = byIp;
        }
        Save();
    }

    /// <summary>"Stop looking up this name" - the background retry pass
    /// and the manual "resolve again" entry both leave a device alone once set.</summary>
    public void SetNoHostnameRetry(string? mac, string ip, bool value) =>
        Update(mac, ip, e => e with { NoHostnameRetry = value });

    /// <summary>"Mute device" - suppresses offline coloring, the rogue
    /// highlight and the fast offline-recheck burst for a device expected to go
    /// offline routinely.</summary>
    public void SetMuted(string? mac, string ip, bool value) =>
        Update(mac, ip, e => e with { Muted = value });

    private void Update(string? mac, string ip, Func<Entry, Entry> change)
    {
        var next = change(Get(mac, ip) ?? new Entry(null, null));
        // The device may have an older entry under its IP from before its MAC
        // was known - drop both representations, then store the current one.
        _entries.Remove(ip);
        if (mac is not null) _entries.Remove(Key(mac));
        if (next.Hostname is not null || next.Color is not null || next.NoHostnameRetry || next.Muted || next.Note is not null)
            _entries[mac is not null ? Key(mac) : ip] = next;
        Save();
    }

    private static string Key(string mac) => mac.ToUpperInvariant();

    private static string? DecodeNote(string value)
    {
        if (!value.StartsWith("b64:", StringComparison.Ordinal)) return null;
        try
        {
            var note = Encoding.UTF8.GetString(Convert.FromBase64String(value[4..]));
            return note.Length > 0 ? note : null;
        }
        catch (FormatException) { return null; }
    }

    private bool Save()
    {
        if (_path is null || _crypto is null || _readOnly) return false;
        try
        {
            // Notes are base64 inside the already sealed file so tabs and line breaks
            // cannot change the row structure. Older 3- to 5-column rows still parse.
            var body = string.Join('\n', new[] { FormatHeader }.Concat(_entries.Select(kv =>
                $"{kv.Key}\t{kv.Value.Hostname ?? ""}\t{kv.Value.Color ?? "N/A"}\t{(kv.Value.NoHostnameRetry ? "1" : "0")}\t{(kv.Value.Muted ? "1" : "0")}\tb64:{Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Value.Note ?? ""))}")));
            AtomicFile.Write(_path, _crypto.Seal(body));
            return true;
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Saving the overrides failed", ex); return false; /* best-effort: the in-memory state stays authoritative */ }
    }
}
