using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;

namespace IpScanner.ViewModels.Tools;

/// <summary>One line of the service-check table: the port, the check and one finding (or the outcome).</summary>
public sealed record CheckRow(string Port, string Check, string Finding);

/// <summary>Port-check tool page state: a free-form target (address or
/// hostname), four scan scopes sharing one underlying <see cref="PortScanner"/>
/// call, and streamed results.</summary>
public sealed class PortCheckToolViewModel : ObservableObject
{
    public const string ScopeCommon = "Common";
    public const string ScopeTop1000 = "Top1000";
    public const string ScopeRange = "Range";
    public const string ScopeList = "List";

    private ScanConfig? _config;
    private CancellationTokenSource? _cts;

    /// <summary>The rows shown: the open ports, or every state when <see cref="ShowAllStates"/> is on.</summary>
    public ObservableCollection<PortResult> Results { get; } = new();

    private readonly List<PortResult> _all = new();

    public IReadOnlyList<ScanTypeOption> ScanTypeOptions { get; } = ScanTypes.All.Select(t => new ScanTypeOption(t)).ToList();

    /// <summary>Re-reads which scan types the machine can run, after the packet driver was installed.</summary>
    public void RefreshScanTypes()
    {
        foreach (var option in ScanTypeOptions) option.Refresh();
    }

    private ScanTypeOption _selectedScanType = null!;
    public ScanTypeOption SelectedScanType
    {
        get => _selectedScanType;
        set { if (value is not null && !ReferenceEquals(_selectedScanType, value)) { _selectedScanType = value; Raise(nameof(SelectedScanType)); RaiseMarks(); } }
    }

    private bool _showAllStates;
    public bool ShowAllStates
    {
        get => _showAllStates;
        set { if (_showAllStates != value) { _showAllStates = value; Raise(nameof(ShowAllStates)); RefreshVisible(); } }
    }

    public PortCheckToolViewModel() => _selectedScanType = ScanTypeOptions[0];

    private static bool IsInteresting(PortResult r) => r.State is PortState.Open or PortState.OpenFiltered or PortState.Unfiltered;

    private void RefreshVisible()
    {
        Results.Clear();
        foreach (var r in _all.Where(r => ShowAllStates || IsInteresting(r)).OrderBy(r => r.Port).ThenBy(r => r.Protocol))
            Results.Add(r);
        Raise(nameof(CanSave));
    }

    /// <summary>The open ports as services, for copy and export.</summary>
    public IReadOnlyList<NetService> OpenServices()
        => _all.Where(r => r.IsOpen && r.Service is not null).OrderBy(r => r.Port)
            .Select(r => r.Service! with { Ip = r.Ip ?? ResolvedIp ?? "" }).ToList();

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

    private string? _resolvedIp;
    public string? ResolvedIp
    {
        get => _resolvedIp;
        set { if (_resolvedIp != value) { _resolvedIp = value; Raise(nameof(ResolvedIp)); Raise(nameof(HasResolvedIp)); Raise(nameof(ResolvedDisplay)); } }
    }
    public bool HasResolvedIp => !string.IsNullOrEmpty(ResolvedIp);

    private string? _resolvedFamily;
    /// <summary>"IPv4" or "IPv6" when the target was a name that resolved to a family worth naming, else null.</summary>
    public string? ResolvedFamily
    {
        get => _resolvedFamily;
        private set { if (_resolvedFamily != value) { _resolvedFamily = value; Raise(nameof(ResolvedDisplay)); } }
    }

    public string ResolvedDisplay => !HasResolvedIp ? ""
        : ResolvedFamily is { } family ? Loc.PortCheckResolvedMsg(ResolvedIp!) + " (" + family + ")" : Loc.PortCheckResolvedMsg(ResolvedIp!);

    private bool _outsideSubnet;
    public bool OutsideSubnet
    {
        get => _outsideSubnet;
        set { if (_outsideSubnet != value) { _outsideSubnet = value; Raise(nameof(OutsideSubnet)); } }
    }

    private string _scope = ScopeCommon;
    public string Scope
    {
        get => _scope;
        set { if (_scope != value) { _scope = value; Raise(nameof(Scope)); RaiseMarks(); } }
    }

    private bool _tcp = true;
    public bool Tcp { get => _tcp; set { if (_tcp != value) { _tcp = value; Raise(nameof(Tcp)); } } }

    private bool _udp = true;
    public bool Udp { get => _udp; set { if (_udp != value) { _udp = value; Raise(nameof(Udp)); } } }

    private string _rangeFrom = "1";
    public string RangeFrom { get => _rangeFrom; set { if (_rangeFrom != value) { _rangeFrom = value; Raise(nameof(RangeFrom)); } } }

    private string _rangeTo = "1024";
    public string RangeTo { get => _rangeTo; set { if (_rangeTo != value) { _rangeTo = value; Raise(nameof(RangeTo)); } } }

    private string _portList = "";
    public string PortList { get => _portList; set { if (_portList != value) { _portList = value; Raise(nameof(PortList)); } } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(CanStart)); Raise(nameof(CanRunChecks)); Raise(nameof(CanSave)); } }
    }

    private string _status = "";
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }

    public bool CanStart => !IsBusy && Target.Trim().Length > 0;

    public ObservableCollection<CheckRow> CheckResults { get; } = new();

    private bool _checkSafe = true;
    public bool CheckSafe { get => _checkSafe; set { if (_checkSafe != value) { _checkSafe = value; Raise(nameof(CheckSafe)); } } }

    private bool _checkDiscovery = true;
    public bool CheckDiscovery { get => _checkDiscovery; set { if (_checkDiscovery != value) { _checkDiscovery = value; Raise(nameof(CheckDiscovery)); } } }

    private bool _checkIntrusive;
    public bool CheckIntrusive { get => _checkIntrusive; set { if (_checkIntrusive != value) { _checkIntrusive = value; Raise(nameof(CheckIntrusive)); RaiseMarks(); } } }

    // ---- warning marks and report output ------------------------------------------------------------------

    /// <summary>The chosen scan type sends crafted flag combinations.</summary>
    public bool WarnScanType => ScanTypes.IsAggressive(SelectedScanType.Type);

    /// <summary>The scope fans out over a large port range.</summary>
    public bool WarnRange => Scope is ScopeTop1000 or ScopeRange;

    public bool WarnIntrusive => CheckIntrusive;

    private void RaiseMarks()
    {
        Raise(nameof(WarnScanType));
        Raise(nameof(WarnRange));
        Raise(nameof(WarnIntrusive));
    }

    public IReadOnlyList<FormatOption> FormatOptions { get; } = ReportFormats.All.Select(f => new FormatOption(f, ReportFormats.Name(f))).ToList();

    private ReportFormat _format = ReportFormat.Text;
    public ReportFormat Format { get => _format; set => SetProperty(ref _format, value); }

    private bool _partial;

    /// <summary>A finished or stopped scan with at least one shown row can be saved.</summary>
    public bool CanSave => !IsBusy && HasResolvedIp && Results.Count > 0;

    /// <summary>The shown rows as a report of the one scanned host.</summary>
    public ScanReport? BuildReport()
    {
        if (ResolvedIp is not { Length: > 0 } ip || Results.Count == 0) return null;
        var device = new Device(ip);
        device.MarkListed();
        var rows = Results.ToList();
        var started = DateTime.Now;
        return ReportBuilder.Build([device], new NetworkInfo(), started.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture),
            started.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), null, new ReportExtras
            {
                Targets = Target.Trim(),
                Partial = _partial,
                PortResults = new Dictionary<string, IReadOnlyList<PortResult>> { [ip] = rows },
            });
    }

    /// <summary>The shown rows as plain text for the clipboard; null before a result exists.</summary>
    public string? CopyText() => BuildReport() is { } r ? ReportWriters.Render(r, ReportFormat.Text) : null;

    public async Task<bool> SaveAsync(string path, ReportFormat format)
    {
        var report = BuildReport();
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

    private bool _isChecksBusy;
    public bool IsChecksBusy
    {
        get => _isChecksBusy;
        set { if (_isChecksBusy != value) { _isChecksBusy = value; Raise(nameof(IsChecksBusy)); Raise(nameof(CanRunChecks)); } }
    }

    private string _checksStatus = "";
    public string ChecksStatus { get => _checksStatus; set { if (_checksStatus != value) { _checksStatus = value; Raise(nameof(ChecksStatus)); } } }

    /// <summary>Checks need a finished port scan with a resolved target and at least one open service.</summary>
    public bool CanRunChecks => !IsBusy && !IsChecksBusy && HasResolvedIp && _all.Any(r => r.IsOpen && r.Service is not null);

    private CancellationTokenSource? _checksCts;

    public void StopChecks() => _checksCts?.Cancel();

    /// <summary>Runs the selected check categories against the open services of the last scan. The
    /// DHCP check ignores the target and broadcasts, so it only joins the run when the intrusive
    /// category is on and the target lies inside the user's own networks.</summary>
    public async Task RunChecksAsync()
    {
        if (IsChecksBusy || ResolvedIp is not { Length: > 0 } ip) return;

        var selection = new CheckSelection(CheckSafe, CheckDiscovery, CheckIntrusive && !OutsideSubnet);
        var services = OpenServices().ToList();
        if (selection.Intrusive) services.Add(new NetService("DHCP", 67, NetProtocol.Udp) with { Ip = ip });

        CheckResults.Clear();
        var runner = CheckRunner.Create();
        int total = runner.Plan(services, selection).Count;
        if (total == 0) { ChecksStatus = Loc.PortCheckChecksNoFindings; return; }

        IsChecksBusy = true;
        _checksCts = new CancellationTokenSource();
        var ct = _checksCts.Token;
        int done = 0;
        ChecksStatus = Loc.PortCheckChecksRunning(0, total);
        try
        {
            await runner.RunAsync(ip, services, selection, ct, r => Dispatch(() =>
            {
                done++;
                foreach (var row in ToRows(r)) CheckResults.Add(row);
                ChecksStatus = Loc.PortCheckChecksRunning(done, total);
            }));
            ChecksStatus = CheckResults.Count == 0 ? Loc.PortCheckChecksNoFindings : Loc.PortCheckChecksDone;
        }
        catch (OperationCanceledException)
        {
            ChecksStatus = Loc.WakeStoppedMsg;
        }
        finally
        {
            IsChecksBusy = false;
            _checksCts?.Dispose();
            _checksCts = null;
        }
    }

    internal static IEnumerable<CheckRow> ToRows(CheckResult r)
    {
        string port = r.Service.Port + "/" + (r.Service.Protocol == NetProtocol.Udp ? "UDP" : "TCP");
        string check = CheckName(r.CheckId);
        if (r.Outcome == CheckOutcome.NoAnswer)
        {
            yield return new CheckRow(port, check, Loc.CheckOutcomeNoAnswer);
            yield break;
        }
        if (r.Findings.Count == 0)
        {
            yield return new CheckRow(port, check, Loc.CheckOutcomeNothingFound);
            yield break;
        }
        foreach (var f in r.Findings)
        {
            string label = FindingLabel(f.Key);
            yield return new CheckRow(port, check, f.Value.Length == 0 ? label : label + ": " + f.Value);
        }
    }

    internal static string CheckName(string id) => id switch
    {
        "http-title" => Loc.CheckNameHttpTitle,
        "http-headers" => Loc.CheckNameHttpHeaders,
        "tls-cert" => Loc.CheckNameTlsCert,
        "snmp-sysdescr" => Loc.CheckNameSnmpSysDescr,
        "upnp-device" => Loc.CheckNameUpnpDevice,
        "mdns-services" => Loc.CheckNameMdnsServices,
        "netbios-names" => Loc.CheckNameNetBiosNames,
        "dhcp-discover" => Loc.CheckNameDhcpDiscover,
        _ => id,
    };

    internal static string FindingLabel(string key) => key switch
    {
        "title" => Loc.FindingTitle,
        "server" => Loc.FindingServer,
        "poweredBy" => Loc.FindingPoweredBy,
        "missingHeader" => Loc.FindingMissingHeader,
        "allHeadersSet" => Loc.FindingAllHeadersSet,
        "protocol" => Loc.FindingProtocol,
        "subject" => Loc.FindingSubject,
        "issuer" => Loc.FindingIssuer,
        "validTo" => Loc.FindingValidTo,
        "certExpired" => Loc.FindingCertExpired,
        "sysDescr" => Loc.FindingSysDescr,
        "upnpLocation" => Loc.FindingUpnpLocation,
        "friendlyName" => Loc.FindingFriendlyName,
        "manufacturer" => Loc.FindingManufacturer,
        "modelName" => Loc.FindingModelName,
        "modelNumber" => Loc.FindingModelNumber,
        "mdnsService" => Loc.FindingMdnsService,
        "netbiosName" => Loc.FindingNetbiosName,
        "dhcpServer" => Loc.FindingDhcpServer,
        "dhcpRouter" => Loc.FindingDhcpRouter,
        "dhcpDns" => Loc.FindingDhcpDns,
        "dhcpLease" => Loc.FindingDhcpLease,
        _ => key,
    };

    public void Initialize(ScanConfig config) => _config = config;

    public void Stop() => _cts?.Cancel();

    /// <summary>A scope that fans out over many probes asks first: Range, Top 1000 and a
    /// list that adds up to more than 100 probes (TCP and UDP count separately). The
    /// common-services scope never does. The flag-abuse scan types always ask.</summary>
    internal bool NeedsConfirmation(int probeCount) => ScanTypes.IsAggressive(SelectedScanType.Type) || Scope switch
    {
        ScopeRange or ScopeTop1000 => true,
        ScopeList => PortScanGate.NeedsConfirmation(probeCount),
        _ => false
    };

    /// <param name="confirm">Called with "target is outside the configured networks" after the
    /// host name is resolved and before the first probe, for gated scopes only; returning
    /// false starts nothing. Required, so no caller can run a gated scan without asking.</param>
    public Task StartAsync(Func<bool, bool> confirm) => StartAsync((outside, _) => confirm(outside));

    /// <param name="confirm">Like the single-argument overload, additionally given the number of
    /// distinct ports the scan will probe.</param>
    public async Task StartAsync(Func<bool, int, bool> confirm)
    {
        if (!CanStart) return;

        Results.Clear();
        _all.Clear();
        _partial = false;
        Raise(nameof(CanSave));
        Status = "";
        ResolvedIp = null;
        ResolvedFamily = null;
        OutsideSubnet = false;

        var (spec, error) = BuildSpec();
        if (error is not null || spec is null) { Status = error ?? Loc.PortCheckInvalidList; return; }

        var type = SelectedScanType.Type;
        if (Tcp && spec.Tcp.Count > 0 && ScanTypes.Availability(type) is { Available: false } unavailable)
        {
            Status = string.Format(Loc.PortScanTypeUnavailableFmt, PortDisplay.UnavailableReasonText(unavailable.Reason));
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var target = Target.Trim();
            string? ip = target;
            if (!AddressSyntax.IsValidAddress(target))
            {
                var resolved = await ResolveHostnameAsync(target, ct);
                ip = resolved?.Ip;
                ResolvedFamily = resolved?.Family;
            }
            if (ip is null) { Status = Loc.PortCheckResolveFailed; return; }

            ResolvedIp = ip;
            OutsideSubnet = _config is not null && IsOutsideConfiguredSubnets(ip, _config);

            int total = spec.Tcp.Count + spec.Udp.Count;
            if (NeedsConfirmation(total) && !confirm(OutsideSubnet, total))
            {
                Status = Loc.WakeStoppedMsg;
                return;
            }

            int found = 0, done = 0;
            void OnResult(PortResult r) => Dispatch(() =>
            {
                _all.Add(r);
                if (r.IsOpen) found++;
                if (ShowAllStates || IsInteresting(r)) Results.Add(r);
                Status = Loc.PortCheckProgressMsg(done, total, found);
            });
            var progress = new Progress<int>(d => Dispatch(() => { done = d; Status = Loc.PortCheckProgressMsg(d, total, found); }));

            try
            {
                using (Core.Scanner.PortScanner.UseDeepDetection())
                    await PortScanEngine.RunAsync(ip, spec, new ScanOptions(type, Tcp, Udp), progress, ct, OnResult);
            }
            catch (RawScanException ex)
            {
                Core.Log.Debug(Core.LogArea.Tools, "Port check stopped by the driver: " + ex.Message);
                Status = string.Format(Loc.PortRawScanFailedFmt, ex.Message);
                return;
            }
            catch (InvalidOperationException ex)
            {
                Core.Log.Debug(Core.LogArea.Tools, "Port check refused: " + ex.Message);
                Status = string.Format(Loc.PortScanTypeUnavailableFmt, PortDisplay.UnavailableReasonText(ScanTypes.Availability(type).Reason));
                return;
            }

            RefreshVisible();
            _partial = ct.IsCancellationRequested;
            if (ct.IsCancellationRequested) Status = Loc.WakeStoppedMsg;
            else Status = Results.Count == 0 ? Loc.PortCheckNoResults : Loc.PortCheckProgressMsg(total, total, found);
        }
        catch (OperationCanceledException)
        {
            _partial = true;
            Status = Loc.WakeStoppedMsg;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The ports of the chosen scope, narrowed to the protocols switched on; null spec with
    /// an error text when the range or list is not valid.</summary>
    internal (PortSpec? Spec, string? Error) BuildSpec()
    {
        PortSpec spec;
        switch (Scope)
        {
            case ScopeCommon:
                spec = PortSpec.Common();
                break;
            case ScopeTop1000:
                spec = PortSpec.Top(1000);
                break;
            case ScopeRange:
                if (!int.TryParse(RangeFrom.Trim(), out var from) || !int.TryParse(RangeTo.Trim(), out var to)
                    || from is < 1 or > PortSpec.MaxPort || to is < 1 or > PortSpec.MaxPort || from > to)
                    return (null, Loc.PortCheckInvalidRange);
                spec = PortSpec.Range(from, to);
                break;
            default: // ScopeList
                if (!PortSpec.TryParse(PortList, out spec) || spec.IsEmpty) return (null, Loc.PortCheckInvalidList);
                break;
        }
        return (spec.Only(Tcp, Udp), null);
    }

    internal (IReadOnlyCollection<int> Tcp, IReadOnlyCollection<int> Udp, string? Error) BuildPortSets()
    {
        var (spec, error) = BuildSpec();
        return spec is null
            ? (Array.Empty<int>(), Array.Empty<int>(), error)
            : (spec.Tcp, spec.Udp, null);
    }

    /// <summary>The address a typed name resolved to and, when both families answered or only IPv6 did, which one it is.</summary>
    internal sealed record ResolvedTarget(string Ip, string? Family);

    private static async Task<ResolvedTarget?> ResolveHostnameAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            return PickAddress(addresses, NetworkDetector.ListOwnIpv6().Any(o => !o.IsLinkLocal));
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Tools, "Port check step failed: " + ex.GetType().Name); return null; }
    }

    /// <summary>IPv4 wins when the name has one; an IPv6 answer is used only when there is no IPv4 one and the
    /// machine has an IPv6 address that can reach beyond its own link.</summary>
    internal static ResolvedTarget? PickAddress(IEnumerable<IPAddress> addresses, bool ipv6Reachable)
    {
        var list = addresses.ToList();
        var v4 = list.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var v6 = list.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        if (v4 is not null) return new ResolvedTarget(v4.ToString(), v6 is not null ? "IPv4" : null);
        if (v6 is not null && ipv6Reachable) return new ResolvedTarget(v6.ToString(), "IPv6");
        return null;
    }

    /// <summary>A target counts as own when it lies in the network of any up card, in a configured subnet or is
    /// a pinned IP (IPv4), or, for an IPv6 target, is on-link for one of the machine's own cards or inside a
    /// configured IPv6 prefix. <paramref name="ownIpv4"/> lets a caller that checks many targets build the
    /// IPv4 set once.</summary>
    internal static bool IsOutsideConfiguredSubnets(string ip, ScanConfig config, IEnumerable<OwnIpv6Address>? ownIpv6 = null,
        OwnNetworks? ownIpv4 = null)
    {
        if (Ipv6Address.TryParse(ip, out var target6))
        {
            if (LocalNetwork.IsOnLinkIpv6(ip, ownIpv6 ?? NetworkDetector.ListOwnIpv6())) return false;
            return !config.Subnets.Select(s => NetEntry.Parse(s).Target)
                .Any(t => Ipv6Prefix.TryParse(t, out var prefix) && prefix.Contains(target6));
        }

        return !(ownIpv4 ?? OwnNetworks.Build(config.Subnets, config.PinnedIps)).Contains(ip);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
