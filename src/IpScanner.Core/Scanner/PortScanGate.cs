namespace IpScanner.Core.Scanner;

/// <summary>The one size rule for a port selection that asks before it runs. The Tools port check and the
/// command line both use it, so the same selection is gated in both.</summary>
internal static class PortScanGate
{
    /// <summary>Probes (TCP and UDP counted separately) a chosen selection may hold without a confirmation.</summary>
    internal const int MaxUngatedPorts = 100;

    internal static bool NeedsConfirmation(int probeCount) => probeCount > MaxUngatedPorts;

    /// <summary>Ports per protocol a "most common ports" count may ask for without a confirmation: the size
    /// of the default scan, so naming that same count explicitly is never stricter than leaving it out.</summary>
    internal const int MaxUngatedTopPorts = 1000;

    internal static bool NeedsTopConfirmation(int topCount) => topCount > MaxUngatedTopPorts;

    /// <summary>The gate for a command-line selection: a top-N count has its own limit, a port list uses the probe count.</summary>
    internal static bool NeedsConfirmation(int? topCount, int probeCount) =>
        topCount is { } n ? NeedsTopConfirmation(n) : NeedsConfirmation(probeCount);
}
