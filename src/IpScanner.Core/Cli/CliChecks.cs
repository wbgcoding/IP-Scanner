using IpScanner.Core.Scanner.Checks;

namespace IpScanner.Core.Cli;

/// <summary>Reads the category list of <c>--checks</c>.</summary>
internal static class CliChecks
{
    /// <summary>"safe,discovery" to a selection; null for an empty list or an unknown word.</summary>
    public static CheckSelection? Parse(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        bool safe = false, discovery = false, intrusive = false;
        foreach (var word in list.Split(',', StringSplitOptions.TrimEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "safe": safe = true; break;
                case "discovery": discovery = true; break;
                case "intrusive": intrusive = true; break;
                default: return null;
            }
        }
        return new CheckSelection(safe, discovery, intrusive);
    }
}
