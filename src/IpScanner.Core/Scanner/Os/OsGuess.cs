namespace IpScanner.Core.Scanner.Os;

public enum OsFamily { Unknown, Windows, UnixLike, NetworkGear, Printer, Media }

/// <summary>What the heuristic looks at: the reply TTL and the open TCP ports.</summary>
public sealed record OsInputs(int? Ttl, IReadOnlyCollection<int> OpenTcpPorts);

/// <summary>An operating system guess. Always an estimate: Percent never reaches 100.
/// A fingerprint result carries the closest signature name, how well the observed values fit it (percent of the
/// compared weight) and, when the reply TTL matches that signature's start value, the hops the reply travelled.</summary>
public sealed record OsEstimate(OsFamily Family, int Percent, bool FromFingerprint = false,
                                string? Match = null, int? MatchPercent = null, int? Hops = null)
{
    public static readonly OsEstimate None = new(OsFamily.Unknown, 0);
    public bool IsKnown => Family != OsFamily.Unknown;
}

/// <summary>Always-available guess from the reply TTL, the TCP window of a raw reply and the open port profile.</summary>
public static class OsHeuristic
{
    internal const int MinScore = 25;
    internal const int MaxPercent = 90;

    /// <summary>The TTL a device most likely started with; the hop count is unknown, so round up to a common start value.</summary>
    public static int? InitialTtl(int? ttl) => ttl switch
    {
        null or <= 0 => null,
        <= 32 => null,
        <= 64 => 64,
        <= 128 => 128,
        _ => 255,
    };

    public static OsEstimate Estimate(OsInputs input)
    {
        var score = new Dictionary<OsFamily, int>();
        void Add(OsFamily f, int pts) => score[f] = score.GetValueOrDefault(f) + pts;
        var ports = new HashSet<int>(input.OpenTcpPorts);

        switch (InitialTtl(input.Ttl))
        {
            case 128: Add(OsFamily.Windows, 40); break;
            case 64:
                Add(OsFamily.UnixLike, 40);
                Add(OsFamily.Printer, 15);
                Add(OsFamily.Media, 15);
                break;
            case 255:
                Add(OsFamily.NetworkGear, 40);
                Add(OsFamily.Printer, 15);
                break;
        }

        if (ports.Contains(445)) Add(OsFamily.Windows, 25);
        if (ports.Contains(3389)) Add(OsFamily.Windows, 25);
        if (ports.Contains(135) || ports.Contains(139)) Add(OsFamily.Windows, 10);
        if (ports.Contains(22) && !ports.Contains(445)) Add(OsFamily.UnixLike, 25);
        if (ports.Contains(9100) || ports.Contains(515) || ports.Contains(631)) Add(OsFamily.Printer, 50);
        if (ports.Contains(554) || ports.Contains(8009) || ports.Contains(8008)) Add(OsFamily.Media, 40);

        if (score.Count == 0) return OsEstimate.None;
        var ranked = score.OrderByDescending(kv => kv.Value).ThenBy(kv => (int)kv.Key).ToList();
        var top = ranked[0];
        if (top.Value < MinScore) return OsEstimate.None;
        int second = ranked.Count > 1 ? ranked[1].Value : 0;
        int percent = (int)Math.Round(100.0 * top.Value / (top.Value + second / 2.0 + 30));
        return new OsEstimate(top.Key, Math.Min(MaxPercent, percent));
    }
}

/// <summary>Picks the best available answer for a device: a fingerprint when one ran, else the heuristic.</summary>
public static class OsEstimator
{
    public static OsEstimate For(Models.Device device, IEnumerable<int>? openTcpPorts)
    {
        if (device.OsFingerprint is { IsKnown: true } fp) return fp;
        return OsHeuristic.Estimate(new OsInputs(device.Ttl, (openTcpPorts ?? Array.Empty<int>()).ToArray()));
    }
}
