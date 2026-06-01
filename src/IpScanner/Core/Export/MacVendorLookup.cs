using System.IO;
using System.Reflection;

namespace IpScanner.Core.Export;

/// <summary>Maps a MAC OUI prefix to a vendor name from an embedded oui.txt.</summary>
public sealed class MacVendorLookup
{
    public static MacVendorLookup Instance { get; } = new();

    private readonly Dictionary<string, string> _map = new();

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
            var parts = line.Split('\t');
            if (parts.Length == 2) _map[parts[0].ToUpperInvariant()] = parts[1].Trim();
        }
    }

    public string? Lookup(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var prefix = new string(mac.Where(Uri.IsHexDigit).Take(6).ToArray()).ToUpperInvariant();
        return prefix.Length == 6 && _map.TryGetValue(prefix, out var v) ? v : null;
    }
}
