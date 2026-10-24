using IpScanner.Core.Localization;

namespace IpScanner.Core.Scanner.Os;

/// <summary>Text for an operating system estimate, shared by the table, the exports and the tooltips.</summary>
public static class OsDisplay
{
    public static string Family(OsFamily f) => f switch
    {
        OsFamily.Windows => Loc.OsFamilyWindows,
        OsFamily.UnixLike => Loc.OsFamilyUnix,
        OsFamily.NetworkGear => Loc.OsFamilyNetwork,
        OsFamily.Printer => Loc.OsFamilyPrinter,
        OsFamily.Media => Loc.OsFamilyMedia,
        _ => "",
    };

    /// <summary>Empty when nothing can be said. Always marked as an estimate.</summary>
    public static string Text(OsEstimate e)
    {
        if (!e.IsKnown) return "";
        if (!e.FromFingerprint) return string.Format(Loc.OsEstimateFmt, Family(e.Family), e.Percent);
        var text = string.Format(Loc.OsFingerprintFmt, e.Match ?? Family(e.Family), e.MatchPercent ?? 0, e.Percent);
        return e.Hops is { } hops ? text + ", " + string.Format(Loc.OsHopsFmt, hops) : text;
    }

    public static string Tip(OsEstimate e)
    {
        if (!e.IsKnown) return Loc.OsTipNone;
        if (e.FromFingerprint) return Loc.OsTipFingerprint;
        return OsFingerprinter.Enabled && !OsFingerprinter.Available
            ? Loc.OsTipHeuristic + "\n" + Loc.OsFingerprintDegradeNote
            : Loc.OsTipHeuristic;
    }

    /// <summary>Export text for a device; open ports come from the report's port map when it has them.</summary>
    public static string ForExport(Models.Device device, IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports)
        => Text(For(device, ports is not null && ports.TryGetValue(device.Ip, out var found) ? found : null));

    /// <summary>The estimate for a device given its scanned services (empty list = ports unknown).</summary>
    public static OsEstimate For(Models.Device device, IEnumerable<NetService>? services)
        => OsEstimator.For(device, services?.Where(s => s.Protocol == NetProtocol.Tcp).Select(s => s.Port));
}
