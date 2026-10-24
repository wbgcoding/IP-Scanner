namespace IpScanner.Core.Scanner;

/// <summary>Asks before a heavy port scan runs. A target inside the user's own networks is
/// confirmed once per session; a target outside them is confirmed every single time.
/// One shared instance serves the device full scan and the Tools port check.</summary>
internal sealed class FullPortScanSafetyGate
{
    internal static FullPortScanSafetyGate Shared { get; } = new();

    /// <summary>A separate instance for intrusive service checks: confirming a full port scan must
    /// not silently also confirm traffic that reaches beyond the one target.</summary>
    internal static FullPortScanSafetyGate SharedChecks { get; } = new();

    private bool _confirmed;

    internal bool Confirm(bool outside, Func<bool> prompt)
    {
        if (!outside && _confirmed) return true;
        bool accepted = prompt();
        if (!outside) _confirmed = accepted;
        return accepted;
    }
}
