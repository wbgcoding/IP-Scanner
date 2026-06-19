namespace IpScanner.Core.Models;

/// <summary>
/// One subnet or pinned-IP entry: target (IP or CIDR) plus optional display
/// name and color. Serialized as "target [name ...] [RRGGBB]" — the color is
/// stored without '#' because the config parser treats '#' as a comment.
/// </summary>
public sealed record NetEntry(string Target, string Name, string Color)
{
    public static NetEntry Parse(string raw)
    {
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new NetEntry("", "", "");
        string color = "";
        int end = parts.Length;
        if (end > 1 && Palette.IsHexColor(parts[^1]))
        {
            color = "#" + parts[^1].TrimStart('#').ToUpperInvariant();
            end--;
        }
        return new NetEntry(parts[0], string.Join(' ', parts[1..end]), color);
    }

    public override string ToString() => string.Join(' ',
        new[] { Target, Name, Color.TrimStart('#') }.Where(s => s.Length > 0));
}
