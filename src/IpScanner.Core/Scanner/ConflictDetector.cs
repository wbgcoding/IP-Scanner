using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>The three conflict shapes worth flagging - a device's MAC
/// changed since it was last seen at this address, the same MAC now answers on
/// more than one address, or more than one MAC answered for the same address
/// during a single run.</summary>
public enum ConflictKind { MacChanged, MacOnMultipleIps, MultipleMacsForOneIp }

/// <summary><paramref name="Detail"/> carries the other value involved - the old
/// MAC for <see cref="ConflictKind.MacChanged"/>, the other IP(s) for
/// <see cref="ConflictKind.MacOnMultipleIps"/>, empty for
/// <see cref="ConflictKind.MultipleMacsForOneIp"/> (the device's own Mac is
/// already just "the most recent one seen", nothing more specific to show).</summary>
public sealed record ConflictFinding(string Ip, string Mac, ConflictKind Kind, string? Detail = null);

/// <summary>Detection only, no side effects and no I/O - callers decide what to
/// do with the findings (a detail-view hint, a sidebar list).</summary>
public static class ConflictDetector
{
    /// <summary>True for a locally-administered (randomized/private) MAC - the
    /// second-least-significant bit of the first octet. Many phones and laptops
    /// rotate this kind of MAC on every reconnect, which would otherwise report
    /// a "MAC changed" finding on nearly every scan for the same device; that is
    /// expected behavior, not a conflict, so it is suppressed at the source.</summary>
    public static bool IsLocallyAdministered(string mac)
    {
        var clean = mac.Replace(":", "").Replace("-", "");
        if (clean.Length < 2) return false;
        if (!byte.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out var firstOctet))
            return false;
        return (firstOctet & 0b0000_0010) != 0;
    }

    /// <summary>Compares this run's live devices against what was known before
    /// (<paramref name="previousMacByIp"/>, typically the known-devices DB's last
    /// snapshot for this network, read BEFORE it gets overwritten - an empty
    /// dictionary is fine and simply skips the MacChanged case, e.g. when the DB
    /// is disabled or this is the first-ever scan of a network).</summary>
    public static List<ConflictFinding> Detect(
        IReadOnlyList<Device> liveDevices, IReadOnlyDictionary<string, string> previousMacByIp)
    {
        var findings = new List<ConflictFinding>();

        foreach (var d in liveDevices)
        {
            if (d.Mac is not { } mac || mac == Device.Unknown) continue;

            // (a) same IP, MAC differs from what was stored for it last time.
            if (previousMacByIp.TryGetValue(d.Ip, out var oldMac) && oldMac.Length > 0
                && !string.Equals(oldMac, mac, StringComparison.OrdinalIgnoreCase)
                && !IsLocallyAdministered(oldMac))
            {
                findings.Add(new ConflictFinding(d.Ip, mac, ConflictKind.MacChanged, Detail: oldMac));
            }

            // (c) more than one MAC answered for this address within this run
            // (ScanEngine.ApplyEnrichRank sets this when a later resolver disagrees
            // with the already-applied MAC).
            if (d.HasMacConflictThisRun)
                findings.Add(new ConflictFinding(d.Ip, mac, ConflictKind.MultipleMacsForOneIp));
        }

        // (b) the same MAC claiming more than one address in this run.
        foreach (var group in liveDevices
                     .Where(d => d.Mac is { } m && m != Device.Unknown)
                     .GroupBy(d => d.Mac!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var ips = group.Select(d => d.Ip).ToList();
            foreach (var d in group)
                findings.Add(new ConflictFinding(d.Ip, group.Key, ConflictKind.MacOnMultipleIps,
                    Detail: string.Join(", ", ips.Where(ip => ip != d.Ip))));
        }

        return findings;
    }
}
