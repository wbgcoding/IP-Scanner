using IpScanner.Core.Models;
using IpScanner.Core.Net;

namespace IpScanner.Core.Import;

/// <summary>Which sidebar list an import targets - kept separate from
/// MainWindow's private EntryKind so this parser stays UI-free and testable.</summary>
public enum ImportTargetKind { Network, Pinned, Host }

/// <summary>Parses a plain-text or CSV list of subnets/pinned IPs/internet hosts for
/// the "import from file" feature. One entry per line: "target", "target name" (the
/// same space-separated form NetEntry itself writes), or "target,name" (CSV). Blank
/// lines and lines starting with '#' are comments, not counted at all. A line whose
/// target doesn't validate for the requested kind is skipped and counted, so the
/// caller can show "N imported, M of T lines skipped" before anything is written.</summary>
public static class ListImportParser
{
    public sealed record Result(List<NetEntry> Accepted, int SkippedLines, int TotalLines);

    public static Result Parse(string text, ImportTargetKind kind)
    {
        var accepted = new List<NetEntry>();
        int skipped = 0, total = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            total++;
            // CSV and the native "target name ~color" format both parse fine once
            // commas are normalized to spaces - NetEntry.Parse already handles the rest.
            var entry = NetEntry.Parse(line.Contains(',') ? line.Replace(',', ' ') : line);
            bool valid = kind == ImportTargetKind.Network
                ? AddressSyntax.IsValidSubnetEntry(entry.Target)
                : AddressSyntax.IsValidAddress(entry.Target);
            if (!valid) { skipped++; continue; }
            accepted.Add(entry);
        }
        return new Result(accepted, skipped, total);
    }
}
