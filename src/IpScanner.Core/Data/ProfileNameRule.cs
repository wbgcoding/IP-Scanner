using IpScanner.Core.Localization;

namespace IpScanner.Core.Data;

/// <summary>Validation for a profile name typed by the user. The names are stored as a
/// comma/semicolon separated list in the config, so a name containing either separator
/// would come back as two names.</summary>
public static class ProfileNameRule
{
    /// <summary>Returns the error text for <paramref name="name"/> (already trimmed), or null when
    /// it is acceptable. <paramref name="existing"/> holds every name that is taken (other profiles
    /// and the built-in default name).</summary>
    public static string? Validate(string name, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.ProfileNameRequired;
        if (name.IndexOfAny(new[] { ',', ';' }) >= 0) return Loc.ProfileNameInvalidChars;
        if (existing.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return Loc.ProfileNameTaken;
        return null;
    }
}
