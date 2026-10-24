namespace IpScanner.Core.Models;

/// <summary>
/// One subnet or pinned-IP entry: target (IP or CIDR) plus optional display
/// name and color. Serialized as "target [name ...] [~RRGGBB]" - the color is
/// stored without a leading '#' because the config parser treats '#' as a
/// comment; the '~' marker is what actually makes the color token
/// unambiguous (see below).
/// </summary>
public sealed record NetEntry(string Target, string Name, string Color)
{
    /// <summary>
    /// New entries always write "~RRGGBB" - a marker that can never collide
    /// with a free-text name. Older files (pre v2.10) stored a bare "RRGGBB"
    /// token instead, which any name accidentally ending in six hex-safe
    /// characters collided with - e.g. "Drucker 123456" silently lost its
    /// number and got a random color, because "123456" also happens to be
    /// valid hex. That legacy form is still accepted for values
    /// already on disk, but only counts as a color when it contains at least
    /// one A-F letter: a name ending in a plain 6-digit number is far more
    /// common than a real saved color with no letters in it at all, so this
    /// narrows the false-positive without losing colors already saved under
    /// the old format. Re-saving (e.g. via the color picker) upgrades the
    /// entry to the unambiguous marker automatically.
    /// </summary>
    public static NetEntry Parse(string raw)
    {
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new NetEntry("", "", "");
        string color = "";
        int end = parts.Length;
        var last = parts[^1];
        if (end > 1 && last.Length == 7 && last[0] == '~' && Palette.IsHexColor(last[1..]))
        {
            color = "#" + last[1..].ToUpperInvariant();
            end--;
        }
        else if (end > 1 && Palette.IsHexColor(last) && last.Any(c => c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
        {
            color = "#" + last.ToUpperInvariant();
            end--;
        }
        return new NetEntry(parts[0], string.Join(' ', parts[1..end]), color);
    }

    public override string ToString() => string.Join(' ',
        new[] { Target, Name, Color.Length > 0 ? "~" + Color.TrimStart('#') : "" }.Where(s => s.Length > 0));

    /// <summary>True when another entry in the same raw list already uses this
    /// display name (case-insensitive). Names are free-text, not a unique key -
    /// this only drives a non-blocking hint in the sidebar's edit popup, never
    /// a save-blocking error like the target-duplicate check. A blank name
    /// never counts as a collision: most entries have no custom name at all.</summary>
    public static bool HasDuplicateName(IEnumerable<string> rawEntries, string name, string? excludeTarget)
    {
        if (name.Length == 0) return false;
        return rawEntries.Select(Parse).Any(en =>
            en.Target != excludeTarget && en.Name.Length > 0 &&
            string.Equals(en.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>RFC-ish hostname check for a pinned entry that is not a
    /// literal IPv4 address - no whitespace, at most 253 characters total, each
    /// dot-separated label 1-63 characters of letters/digits/hyphens with no
    /// leading or trailing hyphen. Deliberately permissive beyond that; actual
    /// DNS resolution at scan time is the real gatekeeper.</summary>
    public static bool IsValidHostname(string target)
    {
        if (target.Length == 0 || target.Length > 253) return false;
        if (target.Any(char.IsWhiteSpace)) return false;
        var labels = target.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }
}
