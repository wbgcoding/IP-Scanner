using IpScanner.Core.Scanner;

namespace IpScanner.Core.Cli;

/// <summary>The one-time notice a run with aggressive settings prints on stderr.</summary>
internal static class CliWarning
{
    public const string SuppressFlag = "--i-know-what-i-am-doing";

    /// <summary>The reasons this run counts as aggressive; empty for a careful run.</summary>
    public static IReadOnlyList<string> AggressiveReasons(CliOptions o, ScanType type, int probes, bool intrusive)
    {
        var reasons = new List<string>();
        if (o.Mode != CliMode.Scan) return reasons;
        if (ScanTypes.IsAggressive(type)) reasons.Add($"the '{ScanTypes.Label(type)}' scan sends unusual packets");
        if (intrusive) reasons.Add("intrusive checks can disturb the services they test");
        if ((o.Timing ?? ScanTiming.Normal) >= ScanTiming.Aggressive) reasons.Add("fast timing (T4/T5) is easy to notice and can overload weak devices");
        if ((o.Ports is not null || o.TopPorts is not null) && PortScanGate.NeedsConfirmation(probes))
            reasons.Add($"{probes} ports per host take long and are noisy");
        return reasons;
    }

    public static string Text(IReadOnlyList<string> reasons) =>
        "Warning: this run is aggressive (" + string.Join("; ", reasons) + "). " + CliHelp.LegalNote() + " "
        + $"Add {SuppressFlag} to hide this notice.";
}
