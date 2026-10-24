namespace IpScanner.Core.Scanner;

/// <summary>The one size rule for a port selection that asks before it runs. The Tools port check and the
/// command line both use it, so the same selection is gated in both.</summary>
internal static class PortScanGate
{
    /// <summary>Probes (TCP and UDP counted separately) a chosen selection may hold without a confirmation.</summary>
    internal const int MaxUngatedPorts = 100;

    internal static bool NeedsConfirmation(int probeCount) => probeCount > MaxUngatedPorts;
}
