using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.ViewModels.Tools;

/// <summary>Finds the live hosts of a target: ping only, no ports.</summary>
public sealed class HostDiscoveryToolViewModel : ScanToolViewModel
{
    public override ScanToolKind Kind => ScanToolKind.Discovery;
    public override IReadOnlyList<string> ColumnHeaders => [Loc.Hostname, Loc.ScanToolColLatency, Loc.Mac, Loc.Vendor];

    protected override ScanPlan BuildPlan() => new(3, false, true, "", false, false);

    protected override void OnHostFound(Device d)
        => UpsertRow(new ScanToolRow(d.Ip, OrDash(d.Hostname), (d.LastMs ?? d.AvgMs) is { } ms ? $"{ms:0.#} ms" : "–", OrDash(d.Mac), "–"));

    protected override IEnumerable<ScanToolRow> RowsFrom(ScanReport report)
        => OnlineHosts(report).Select(h => new ScanToolRow(h.Ip, OrDash(h.Hostname), Latency(h), OrDash(h.Mac), OrDash(h.Vendor)));
}

/// <summary>Lists the open ports of every live host with the detected service and version.</summary>
public sealed class ServiceDetectionToolViewModel : ScanToolViewModel
{
    public override ScanToolKind Kind => ScanToolKind.Services;
    public override IReadOnlyList<string> ColumnHeaders => [Loc.Port, Loc.Service, Loc.AboutVersionLabel];
    public override bool HasTiming => true;
    public override bool HasSkipDiscovery => true;
    public override bool HasPortScope => true;

    protected override ScanPlan BuildPlan() => new(Timing, SkipDiscovery, false, "", true, false);
    protected override PortSpec? BuildPorts() => ScopePorts();

    protected override void OnPortFound(string ip, PortResult r)
    {
        if (r.IsOpen) AddRow(PortRow(ip, r));
    }

    internal static ScanToolRow PortRow(string ip, PortResult r)
        => new(ip, r.PortLabel, OrDash(r.Service?.DisplayName ?? r.ServiceName), OrDash(r.Service?.VersionShort));

    protected override IEnumerable<ScanToolRow> RowsFrom(ScanReport report)
        => OnlineHosts(report).SelectMany(h => h.Ports.Where(p => p.IsOpen).OrderBy(p => p.Protocol).ThenBy(p => p.Port)
            .Select(p => new ScanToolRow(h.Ip, p.Label, OrDash(p.Service), OrDash(p.Version))));
}

/// <summary>Estimates the operating system of every live host.</summary>
public sealed class OsEstimateToolViewModel : ScanToolViewModel
{
    public override ScanToolKind Kind => ScanToolKind.Os;
    public override IReadOnlyList<string> ColumnHeaders => [Loc.Hostname, Loc.OsColumnHeader, Loc.OpenPortsHeader];
    public override bool HasTiming => true;
    public override bool HasSkipDiscovery => true;

    /// <summary>Shown when the packet driver is missing: the estimate then rests on TTL and open ports only.</summary>
    public string? Note => Real.OsRunner is null ? Loc.OsFingerprintDegradeNote : null;

    protected override ScanPlan BuildPlan() => new(Timing, SkipDiscovery, false, "", false, true);
    protected override PortSpec? BuildPorts() => PortSpec.Common().Only(true, false);

    protected override void OnHostFound(Device d)
        => UpsertRow(new ScanToolRow(d.Ip, OrDash(d.Hostname), "–", "–"));

    protected override void OnPortFound(string ip, PortResult r)
    {
        if (!r.IsOpen) return;
        var row = RowOf(ip) ?? new ScanToolRow(ip, "–", "–", "–");
        UpsertRow(row with { C3 = row.C3 == "–" ? r.PortLabel : row.C3 + ", " + r.PortLabel });
    }

    protected override void OnOsDone(string ip, OsEstimate? estimate)
    {
        var row = RowOf(ip) ?? new ScanToolRow(ip, "–", "–", "–");
        UpsertRow(row with { C2 = OrDash(estimate is null ? null : OsDisplay.Text(estimate)) });
    }

    protected override IEnumerable<ScanToolRow> RowsFrom(ScanReport report)
        => OnlineHosts(report).Select(h => new ScanToolRow(h.Ip, OrDash(h.Hostname),
            OrDash(h.Os is { } o ? OsDisplay.Text(new OsEstimate(o.Family, o.Percent, o.FromFingerprint, o.Match, o.MatchPercent, o.Hops)) : null),
            OrDash(OpenPorts(h))));
}

/// <summary>Runs the service checks on the open ports of every live host.</summary>
public sealed class ChecksToolViewModel : ScanToolViewModel
{
    public override ScanToolKind Kind => ScanToolKind.Checks;
    public override IReadOnlyList<string> ColumnHeaders => [Loc.Port, Loc.PortCheckColCheck, Loc.PortCheckColFinding];
    public override bool HasTiming => true;
    public override bool HasSkipDiscovery => true;
    public override bool HasPortScope => true;
    public override bool HasChecks => true;

    protected override bool RebuildFromReport => false;

    protected override ScanPlan BuildPlan() => new(Timing, SkipDiscovery, false, "", true, false);
    protected override PortSpec? BuildPorts() => ScopePorts();

    protected override CheckSelection? BuildChecks(bool allowIntrusive)
        => new CheckSelection(CheckSafe, CheckDiscovery, CheckIntrusive && allowIntrusive);

    protected override void OnChecksDone(string ip, IReadOnlyList<CheckResult> results)
    {
        foreach (var result in results)
            foreach (var row in PortCheckToolViewModel.ToRows(result)) AddRow(new ScanToolRow(ip, row.Port, row.Check, row.Finding));
    }

    protected override IEnumerable<ScanToolRow> RowsFrom(ScanReport report) => [];
}

/// <summary>Runs a saved scan preset against a target and writes the report in the chosen format.</summary>
public sealed class ScanExportToolViewModel : ScanToolViewModel
{
    private int _presetIndex;

    public override ScanToolKind Kind => ScanToolKind.Export;
    public override IReadOnlyList<string> ColumnHeaders => [Loc.Hostname, Loc.ScanToolColLatency, Loc.OpenPortsHeader];
    public override bool HasPreset => true;
    public override bool HasSavePath => true;

    public override void Initialize(ScanConfig config)
    {
        base.Initialize(config);
        var list = ScanPresets.All(config)
            .Select(p => ScanPresets.PlanOf(p) is { } plan ? new PresetOption(p.Name, plan) : null)
            .OfType<PresetOption>().ToList();
        var current = ScanPlan.FromConfig(config);
        int match = list.FindIndex(p => p.Plan == current);
        if (match < 0)
        {
            // The saved settings fit no preset: they come first and stay the careful default.
            list.Insert(0, new PresetOption(Loc.PresetCustom, current));
            match = 0;
        }
        Presets = list;
        Raise(nameof(Presets));
        PresetIndex = match;
    }

    public int PresetIndex
    {
        get => _presetIndex;
        set { if (SetProperty(ref _presetIndex, value)) RaiseMarks(); }
    }

    private ScanPlan? Plan => Presets.Count > 0 ? Presets[System.Math.Clamp(_presetIndex, 0, Presets.Count - 1)].Plan : null;

    public override bool WarnTiming => Plan is { Timing: >= 4 };
    public override bool WarnRange => Plan is not null && NeedsFullRange(BuildPorts());

    protected override ScanPlan BuildPlan() => Plan ?? new ScanPlan(3, false, false, "", false, false);
    protected override PortSpec? BuildPorts() => Plan is { PingOnly: false } p ? p.ResolvePorts().Only(true, false) : null;

    protected override bool NeedsFullRange(PortSpec? ports)
        => ports is not null && Plan is { } p && p.Ports is not ("" or "fast")
            && PortScanGate.NeedsConfirmation(ports.Tcp.Count + ports.Udp.Count);

    protected override string BuildCommand(ScanPlan plan, string target) => ScanPresets.Command(plan, target);

    protected override void OnHostFound(Device d)
        => UpsertRow(new ScanToolRow(d.Ip, OrDash(d.Hostname), (d.LastMs ?? d.AvgMs) is { } ms ? $"{ms:0.#} ms" : "–", "–"));

    protected override void OnPortFound(string ip, PortResult r)
    {
        if (!r.IsOpen) return;
        var row = RowOf(ip) ?? new ScanToolRow(ip, "–", "–", "–");
        UpsertRow(row with { C3 = row.C3 == "–" ? r.PortLabel : row.C3 + ", " + r.PortLabel });
    }

    protected override IEnumerable<ScanToolRow> RowsFrom(ScanReport report)
        => OnlineHosts(report).Select(h => new ScanToolRow(h.Ip, OrDash(h.Hostname), Latency(h), OrDash(OpenPorts(h))));
}
