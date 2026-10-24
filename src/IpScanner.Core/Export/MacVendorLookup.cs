using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Export;

/// <summary>Maps a MAC OUI prefix to a vendor name from an embedded oui.txt.</summary>
public sealed class MacVendorLookup
{
    public static MacVendorLookup Instance { get; } = new();

    private readonly Dictionary<string, string> _map = new();

    /// <summary>Number of OUI prefixes loaded from oui.txt. Guards against a stub/truncated database.</summary>
    public int EntryCount => _map.Count;

    /// <summary>The "Regenerated yyyy-MM-dd" date from oui.txt's own header comment,
    /// or null if that line is missing (shown in the calculator footnote).</summary>
    public string? SourceDate { get; }

    private MacVendorLookup()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("oui.txt"));
        if (name is null) return;
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#'))
            {
                if (SourceDate is null && Regex.Match(line, @"Regenerated (\d{4}-\d{2}-\d{2})") is { Success: true } m) SourceDate = m.Groups[1].Value;
                continue;
            }
            var parts = line.Split('\t');
            if (parts.Length == 2) _map[parts[0].ToUpperInvariant()] = parts[1].Trim();
        }
    }

    /// <summary>Longest prefix wins: a MA-S (9 hex) or MA-M (7 hex) assignment overrides its
    /// coarser MA-L (6 hex) parent for the same OUI.</summary>
    private static readonly int[] PrefixLengths = [9, 7, 6];

    public string? Lookup(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).Take(9).ToArray()).ToUpperInvariant();
        foreach (var len in PrefixLengths)
        {
            if (hex.Length >= len && _map.TryGetValue(hex[..len], out var v)) return v;
        }
        return null;
    }
}
