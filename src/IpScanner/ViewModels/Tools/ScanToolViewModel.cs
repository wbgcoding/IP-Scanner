using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.ViewModels.Tools;

public enum ScanToolKind { Discovery, Services, Os, Checks, Export }

/// <summary>One line of a scan tool table: the address plus up to four more cells.</summary>
public sealed record ScanToolRow(string Ip, string C1, string C2, string C3, string C4 = "")
{
    /// <summary>The accessible name of a row: address and first cell.</summary>
    public override string ToString() => (Ip + " " + C1).Trim();
}



public sealed record TimingOption(int Template, string Label)
{
    /// <summary>The fast templates (T4, T5) are noisy; their list item carries the warning mark.</summary>
    public bool IsAggressive => Template >= 4;
}

public sealed record PortScopeOption(string Id, string Label);

public sealed record FormatOption(ReportFormat Format, string Label);

public sealed record PresetOption(string Name, ScanPlan Plan);

/// <summary>What a start needs the user to confirm first. <see cref="Any"/> is false for a plain, careful scan.</summary>
public sealed record ScanToolGate(int Count, bool Large, bool FullRange, bool Intrusive, bool Outside, int PortCount = 0)
{
    public bool Any => Large || FullRange || Intrusive || Outside;
}

/// <summary>Shared state of the scan tool pages: a target with inline validation, a few options, a live result
/// list, cancel, copy and save. The scanning itself is <see cref="ToolScan"/>; this class only orchestrates it.</summary>
public abstract class ScanToolViewModel : ObservableObject
{
    public const string ScopeCommon = "Common";
    public const string ScopeTop1000 = "Top1000";
    public const string ScopeAll = "All";

    /// <summary>Targets at or above this many addresses ask for a confirmation.</summary>
    public const int LargeTargetCount = 1024;

    private readonly object _sync = new();
    private ScanConfig? _config;
    private CliServices? _system;
    private CancellationTokenSource? _cts;
    private bool _live;
    private System.Windows.Threading.Dispatcher? _uiDispatcher;

    /// <summary>Replaces the real scanner services (tests).</summary>
    public CliServices? Services { get; set; }

    /// <summary>Replaces the "outside the configured subnets" test (tests).</summary>
    internal Func<string, bool>? OutsideTest { get; set; }

    /// <summary>Finds the dispatcher that carries results to the UI: the one of the thread that starts the run.
    /// A test replaces it, because a pool thread that once built a WPF object keeps a dispatcher nobody pumps.</summary>
    internal Func<System.Windows.Threading.Dispatcher?> UiDispatcherSource { get; set; } =
        () => System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);

    public abstract ScanToolKind Kind { get; }

    /// <summary>Header texts of the columns after the address column.</summary>
    public abstract IReadOnlyList<string> ColumnHeaders { get; }

    public virtual bool HasTiming => false;
    public virtual bool HasSkipDiscovery => false;
    public virtual bool HasPortScope => false;
    public virtual bool HasChecks => false;
    public virtual bool HasPreset => false;
    public virtual bool HasSavePath => false;

    public ObservableCollection<ScanToolRow> Rows { get; } = [];

    public IReadOnlyList<TimingOption> TimingOptions { get; private set; } = [];
    public IReadOnlyList<PortScopeOption> ScopeOptions { get; private set; } = [];
    public IReadOnlyList<FormatOption> FormatOptions { get; private set; } = [];
    public IReadOnlyList<PresetOption> Presets { get; protected set; } = [];

    protected ScanToolViewModel() => ReloadLabels();

    /// <summary>Rebuilds the translated option lists; the selection stays because it is kept by id.</summary>
    public void ReloadLabels()
    {
        TimingOptions = Enumerable.Range(0, 6).Select(t => new TimingOption(t, $"T{t} {Loc.PortScanTimingName(t)}")).ToList();
        ScopeOptions =
        [
            new(ScopeCommon, Loc.PortCheckScopeCommon),
            new(ScopeTop1000, Loc.PortCheckScopeTop1000),
            new(ScopeAll, Loc.ScanToolPortsAll),
        ];
        FormatOptions = ReportFormats.All.Select(f => new FormatOption(f, ReportFormats.Name(f))).ToList();
        Raise(nameof(TimingOptions));
        Raise(nameof(ScopeOptions));
        Raise(nameof(FormatOptions));
    }

    public virtual void Initialize(ScanConfig config) => _config = config;

    // ---- target -------------------------------------------------------------------------------------------

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (SetProperty(ref _target, value)) { ShowTargetMessage = true; Validate(); } }
    }

    private bool _showTargetMessage;
    /// <summary>False on a fresh page; becomes true once the target was edited or a start was attempted.</summary>
    public bool ShowTargetMessage
    {
        get => _showTargetMessage;
        private set => SetProperty(ref _showTargetMessage, value);
    }

    private string _targetMessage = Loc.ScanToolTargetEmpty;
    public string TargetMessage
    {
        get => _targetMessage;
        private set => SetProperty(ref _targetMessage, value);
    }

    private bool _targetOk;
    public bool TargetOk
    {
        get => _targetOk;
        private set { if (SetProperty(ref _targetOk, value)) Raise(nameof(CanStart)); }
    }

    private TargetSpec? ParseTarget()
    {
        var resolver = Services?.Resolver;
        return TargetSpec.Parse(_target, resolver is null ? null : new TargetSpecOptions { Resolver = resolver });
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(_target))
        {
            TargetMessage = Loc.ScanToolTargetEmpty;
            TargetOk = false;
            return;
        }
        try
        {
            var spec = ParseTarget()!;
            if (!spec.IsValid)
            {
                TargetMessage = string.Format(Loc.ScanToolTargetInvalidFmt, string.Join(", ", spec.Errors.Select(e => e.Token)));
                TargetOk = false;
            }
            else if (!spec.HasTargets)
            {
                TargetMessage = Loc.ScanToolTargetEmpty;
                TargetOk = false;
            }
            else if (spec.RequiresConfirmation)
            {
                TargetMessage = Loc.ScanToolTargetTooMany;
                TargetOk = false;
            }
            else
            {
                TargetMessage = string.Format(Loc.ScanToolTargetCountFmt, spec.EstimatedCount);
                TargetOk = true;
            }
        }
        catch (Exception ex)
        {
            TargetMessage = string.Format(Loc.ScanToolTargetInvalidFmt, ex.Message);
            TargetOk = false;
        }
    }

    // ---- options ------------------------------------------------------------------------------------------

    private int _timing = 3;
    public int Timing
    {
        get => _timing;
        set { if (SetProperty(ref _timing, Math.Clamp(value, 0, 5))) RaiseMarks(); }
    }

    private bool _skipDiscovery;
    public bool SkipDiscovery
    {
        get => _skipDiscovery;
        set => SetProperty(ref _skipDiscovery, value);
    }

    private string _scope = ScopeCommon;
    public string Scope
    {
        get => _scope;
        set { if (SetProperty(ref _scope, value)) RaiseMarks(); }
    }

    private bool _checkSafe = true;
    public bool CheckSafe
    {
        get => _checkSafe;
        set { if (SetProperty(ref _checkSafe, value)) Raise(nameof(CanStart)); }
    }

    private bool _checkDiscovery = true;
    public bool CheckDiscovery
    {
        get => _checkDiscovery;
        set { if (SetProperty(ref _checkDiscovery, value)) Raise(nameof(CanStart)); }
    }

    private bool _checkIntrusive;
    public bool CheckIntrusive
    {
        get => _checkIntrusive;
        set { if (SetProperty(ref _checkIntrusive, value)) { Raise(nameof(CanStart)); RaiseMarks(); } }
    }

    private ReportFormat _format = ReportFormat.Text;
    public ReportFormat Format
    {
        get => _format;
        set => SetProperty(ref _format, value);
    }

    /// <summary>Where the export page writes the report after a run. Set by the view before the start.</summary>
    public string? SavePath { get; set; }

    // ---- warning marks ------------------------------------------------------------------------------------

    /// <summary>The chosen timing is T4 or T5.</summary>
    public virtual bool WarnTiming => HasTiming && Timing >= 4;

    /// <summary>The port selection is large enough to need a confirmation.</summary>
    public virtual bool WarnRange => HasPortScope && NeedsFullRange(BuildPorts());

    public bool WarnIntrusive => HasChecks && CheckIntrusive;

    protected void RaiseMarks()
    {
        Raise(nameof(WarnTiming));
        Raise(nameof(WarnRange));
        Raise(nameof(WarnIntrusive));
    }

    // ---- plan ---------------------------------------------------------------------------------------------

    protected abstract ScanPlan BuildPlan();

    /// <summary>The ports to probe; null when the page only looks for live hosts.</summary>
    protected virtual PortSpec? BuildPorts() => null;

    /// <summary>Top 1000 and the full range ask first; the common services never do.</summary>
    protected virtual bool NeedsFullRange(PortSpec? ports) => ports is not null && Scope != ScopeCommon;

    protected virtual CheckSelection? BuildChecks(bool allowIntrusive) => null;

    protected virtual string BuildCommand(ScanPlan plan, string target) => "";

    protected PortSpec? ScopePorts() => Scope switch
    {
        ScopeAll => PortSpec.All().Only(true, false),
        ScopeTop1000 => PortSpec.Top(1000).Only(true, false),
        _ => PortSpec.Common().Only(true, false),
    };

    // ---- state --------------------------------------------------------------------------------------------

    private string _status = "";
    public string Status
    {
        get => _status;
        protected set => SetProperty(ref _status, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) { Raise(nameof(CanStart)); Raise(nameof(CanSave)); } }
    }

    public virtual bool CanStart => TargetOk && !IsBusy && (!HasChecks || CheckSafe || CheckDiscovery || CheckIntrusive);

    public ScanReport? LastReport { get; private set; }

    public bool CanSave => LastReport is not null && !IsBusy;

    // ---- run ----------------------------------------------------------------------------------------------

    protected CliServices Real => Services ?? (_system ??= CliServices.System());

    /// <summary>True when any target lies outside every own network (cards, configured subnets, pinned IPs).
    /// Each target address is checked exactly, against a set built once for the run.</summary>
    private bool AnyOutside(IReadOnlyList<string> targets)
    {
        if (OutsideTest is { } test) return targets.Select(Ipv4.SubnetPrefix).Distinct().Any(p => test(p + ".1"));
        if (_config is null) return false;
        var own = OwnNetworks.Build(_config.Subnets, _config.PinnedIps);
        var own6 = NetworkDetector.ListOwnIpv6();
        return targets.Any(t => PortCheckToolViewModel.IsOutsideConfiguredSubnets(t, _config, own6, own));
    }

    /// <summary>Runs the scan. <paramref name="confirm"/> is asked once, before anything is sent, when the run
    /// needs a confirmation; returning false stops without scanning.</summary>
    public async Task StartAsync(Func<ScanToolGate, bool> confirm)
    {
        if (!CanStart) { ShowTargetMessage = true; return; }
        IsBusy = true;
        IDisposable? slot = null;
        try
        {
            var spec = ParseTarget()!;
            List<string> targets;
            try { targets = await Task.Run(() => spec.Expand().ToList()); }
            catch (TargetCapExceededException)
            {
                TargetMessage = Loc.ScanToolTargetTooMany;
                TargetOk = false;
                return;
            }
            if (spec.UnresolvedHosts.Count > 0)
            {
                TargetMessage = string.Format(Loc.ScanToolTargetInvalidFmt, string.Join(", ", spec.UnresolvedHosts));
                TargetOk = false;
                return;
            }
            if (targets.Count == 0) return;

            var plan = BuildPlan();
            var ports = plan.PingOnly ? null : BuildPorts();
            bool outside = ports is not null && AnyOutside(targets);
            var gate = new ScanToolGate(targets.Count, targets.Count >= LargeTargetCount, NeedsFullRange(ports),
                HasChecks && CheckIntrusive && ports is not null, outside, ports is null ? 0 : ports.Tcp.Count + ports.Udp.Count);
            if (gate.Any && !confirm(gate))
            {
                Status = Loc.WakeStoppedMsg;
                return;
            }
            if (HasSavePath && !string.IsNullOrEmpty(SavePath))
            {
                try { ReportFile.CheckWritable(SavePath); }
                catch (Exception ex)
                {
                    Status = string.Format(Loc.ScanToolSaveFailedFmt, ex.Message);
                    return;
                }
            }
            slot = ToolScan.TryEnter();
            if (slot is null)
            {
                Status = Loc.ScanToolBusy;
                return;
            }

            var request = new ToolScanRequest
            {
                Targets = targets,
                TargetsText = _target.Trim(),
                Command = BuildCommand(plan, _target.Trim()),
                Plan = plan,
                Ports = ports,
                Options = new ScanOptions(ScanType.Connect, true, false),
                Checks = ports is null ? null : BuildChecks(allowIntrusive: !gate.Outside),
            };
            _cts = new CancellationTokenSource();
            _uiDispatcher = UiDispatcherSource();
            lock (_sync) { _live = true; Rows.Clear(); }
            LastReport = null;
            Raise(nameof(CanSave));
            Status = Loc.ScanToolStageDiscovery;

            var outcome = await ToolScan.RunAsync(request, Real, Events(), _cts.Token);

            lock (_sync)
            {
                _live = false;
                if (RebuildFromReport)
                {
                    Rows.Clear();
                    foreach (var row in RowsFrom(outcome.Report)) Rows.Add(row);
                }
            }
            LastReport = outcome.Report;
            int hosts = outcome.Report.Hosts.Count(h => h.IsOnline);
            int open = outcome.Report.Hosts.Sum(h => h.Ports.Count(p => p.IsOpen));
            Status = string.Format(outcome.Cancelled ? Loc.ScanToolStoppedFmt : Loc.ScanToolDoneFmt, hosts, open);
            if (outcome.Warnings.Count > 0) Status += " | " + outcome.Warnings[0];
            if (!outcome.Cancelled && HasSavePath && !string.IsNullOrEmpty(SavePath))
                await SaveAsync(SavePath, Format);
        }
        finally
        {
            lock (_sync) _live = false;
            slot?.Dispose();
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    public void Stop() => _cts?.Cancel();

    // ---- results ------------------------------------------------------------------------------------------

    protected virtual bool RebuildFromReport => true;

    protected abstract IEnumerable<ScanToolRow> RowsFrom(ScanReport report);

    protected virtual void OnHostFound(Device d) { }
    protected virtual void OnPortFound(string ip, PortResult r) { }
    protected virtual void OnChecksDone(string ip, IReadOnlyList<CheckResult> results) { }
    protected virtual void OnOsDone(string ip, OsEstimate? estimate) { }

    protected void AddRow(ScanToolRow row) => Rows.Add(row);

    protected void UpsertRow(ScanToolRow row)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Ip == row.Ip) { Rows[i] = row; return; }
        Rows.Add(row);
    }

    protected ScanToolRow? RowOf(string ip) => Rows.FirstOrDefault(r => r.Ip == ip);

    private ToolScanEvents Events() => new()
    {
        HostFound = d => Post(() => OnHostFound(d)),
        PortFound = (ip, r) => Post(() => OnPortFound(ip, r)),
        ChecksDone = (ip, list) => Post(() => OnChecksDone(ip, list)),
        OsDone = (ip, e) => Post(() => OnOsDone(ip, e)),
        Stage = s => Post(() => Status = s switch
        {
            ToolScanStage.Discovery => Loc.ScanToolStageDiscovery,
            ToolScanStage.Ports => Loc.ScanToolStagePorts,
            ToolScanStage.Checks => Loc.ScanToolStageChecks,
            _ => Loc.ScanToolStageOs,
        }),
    };

    private void Post(Action action)
    {
        void Run()
        {
            lock (_sync)
            {
                if (_live) action();
            }
        }
        // The dispatcher of the thread that started the run, not Application.Current: without a UI thread the rows are updated in place.
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Run();
        else dispatcher.BeginInvoke(Run);
    }

    // ---- output -------------------------------------------------------------------------------------------

    public async Task<bool> SaveAsync(string path, ReportFormat format)
    {
        var report = LastReport;
        if (report is null) return false;
        try
        {
            await Task.Run(() => ReportFile.WriteTo(path, report, format));
            Status = string.Format(Loc.ScanToolSavedFmt, path);
            return true;
        }
        catch (Exception ex)
        {
            Status = string.Format(Loc.ScanToolSaveFailedFmt, ex.Message);
            return false;
        }
    }

    /// <summary>The result as plain text for the clipboard; null before the first finished run.</summary>
    public string? CopyText() => LastReport is { } r ? ReportWriters.Render(r, ReportFormat.Text) : null;

    // ---- helpers ------------------------------------------------------------------------------------------

    protected static string Latency(ReportHost h)
        => (h.LastMs ?? h.AvgMs) is { } ms ? ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms" : "–";

    protected static string OpenPorts(ReportHost h)
        => string.Join(", ", h.Ports.Where(p => p.IsOpen).Select(p => p.Label));

    protected static IEnumerable<ReportHost> OnlineHosts(ScanReport report)
        => report.Hosts.Where(h => h.IsOnline).OrderBy(h => h.Ip, Core.Net.IpAddressKey.TextComparer);

    protected static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "–" : s;
}
