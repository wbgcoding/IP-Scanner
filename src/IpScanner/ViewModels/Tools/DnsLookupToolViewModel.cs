using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Localization;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels.Tools;

/// <summary>One line of the record table.</summary>
public sealed record DnsRecordRow(string Type, string Name, string Value, string Ttl);

/// <summary>One server of the compare table. <see cref="Tip"/> is only set for a differing answer.</summary>
public sealed record DnsCompareRow(string Server, string Time, string Code, string Answers, bool IsFastest, bool IsMismatch)
{
    public string? Tip => IsMismatch ? Loc.DnsAnswerDiffers : null;
}

/// <summary>An entry of the type combo; a null <see cref="Type"/> is "Automatic".</summary>
public sealed record DnsTypeOption(string Label, DnsRecordType? Type);

/// <summary>DNS lookup tool page state: one field, direction auto-detected
/// (a valid IPv4 address reverse-resolves, anything else forward-resolves) or forced
/// via <see cref="Mode"/>. With the system server and the automatic type the operating system
/// resolves; any other choice asks the server directly. Compare mode sends the same question
/// to several servers at once.</summary>
public sealed class DnsLookupToolViewModel : ObservableObject
{
    public const string ModeAuto = "Auto";
    public const string ModeForward = "Forward";
    public const string ModeReverse = "Reverse";
    public const int MaxCompareServers = 6;

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cts;

    // Test seams: the server port, the per-query timeout and the list of system servers.
    internal int Port { get; set; } = 53;
    internal TimeSpan QueryTimeout { get; set; } = DnsClient.DefaultTimeout;
    internal Func<IReadOnlyList<IPAddress>> SystemServerProvider { get; set; } = GetSystemServerAddresses;

    /// <summary>Cancels an in-flight lookup - called when the Tools window closes
    /// so a request never outlives its own page.</summary>
    public void Stop() => _cts?.Cancel();

    public DnsLookupToolViewModel()
    {
        TypeOptions.Add(new DnsTypeOption(Loc.DnsModeAuto, null));
        foreach (var t in DnsClient.AllTypes) TypeOptions.Add(new DnsTypeOption(t.ToString(), t));
        _selectedType = TypeOptions[0];
        ServerChoices.Add(Loc.DnsServerSystem);
        _server = Loc.DnsServerSystem;
    }

    public ObservableCollection<string> Addresses { get; } = new();
    public ObservableCollection<string> Aliases { get; } = new();
    public ObservableCollection<string> DnsServers { get; } = new();
    public ObservableCollection<DnsRecordRow> Records { get; } = new();
    public ObservableCollection<DnsCompareRow> CompareRows { get; } = new();
    public ObservableCollection<string> ServerChoices { get; } = new();
    public ObservableCollection<DnsTypeOption> TypeOptions { get; } = new();
    public ObservableCollection<string> CompareServers { get; } = new();

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

    private string _mode = ModeAuto;
    public string Mode { get => _mode; set { if (_mode != value) { _mode = value; Raise(nameof(Mode)); } } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(CanStart)); } }
    }

    private string _status = "";
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }

    private string? _canonicalName;
    public string? CanonicalName { get => _canonicalName; set { if (_canonicalName != value) { _canonicalName = value; Raise(nameof(CanonicalName)); Raise(nameof(ShowReverseResults)); } } }

    private bool _showForwardResults;
    public bool ShowForwardResults { get => _showForwardResults; set { if (_showForwardResults != value) { _showForwardResults = value; Raise(nameof(ShowForwardResults)); } } }
    public bool ShowReverseResults => CanonicalName is { Length: > 0 };

    private bool _showRecordResults;
    public bool ShowRecordResults { get => _showRecordResults; private set { if (_showRecordResults != value) { _showRecordResults = value; Raise(nameof(ShowRecordResults)); } } }

    private bool _showCompareResults;
    public bool ShowCompareResults { get => _showCompareResults; private set { if (_showCompareResults != value) { _showCompareResults = value; Raise(nameof(ShowCompareResults)); } } }

    // ── Server and type ──

    private string _server;
    private bool _useSystem = true;
    private IPAddress? _explicitServer;
    private string _comboError = "";
    private string _addError = "";

    /// <summary>The text of the editable server combo: the "system" entry or an IP address.</summary>
    public string Server
    {
        get => _server;
        set
        {
            value ??= "";
            if (_server == value) return;
            _server = value;
            _useSystem = value.Trim().Length == 0 || string.Equals(value.Trim(), Loc.DnsServerSystem, StringComparison.OrdinalIgnoreCase);
            _explicitServer = null;
            _comboError = "";
            if (!_useSystem)
            {
                if (TryParseServer(value, out var ip)) _explicitServer = ip;
                else _comboError = Loc.DnsServerInvalid;
            }
            Raise(nameof(Server));
            RaiseServerState();
        }
    }

    private DnsTypeOption _selectedType;
    public DnsTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is null || _selectedType == value) return;
            _selectedType = value;
            Raise(nameof(SelectedType));
            Raise(nameof(UsesSystemResolver));
        }
    }

    private bool _compareMode;
    public bool CompareMode
    {
        get => _compareMode;
        set
        {
            if (_compareMode == value) return;
            _compareMode = value;
            if (value && CompareServers.Count == 0)
                foreach (var ip in SystemServerProvider().Take(MaxCompareServers)) CompareServers.Add(ip.ToString());
            Raise(nameof(CompareMode));
            RaiseServerState();
        }
    }

    private string _newServer = "";
    public string NewServer
    {
        get => _newServer;
        set { if (_newServer != value) { _newServer = value ?? ""; _addError = ""; Raise(nameof(NewServer)); Raise(nameof(ServerError)); Raise(nameof(HasServerError)); } }
    }

    public string ServerError => _compareMode ? _addError : _comboError;
    public bool HasServerError => ServerError.Length > 0;

    /// <summary>True when the operating system's resolver answers (system server, automatic type).</summary>
    public bool UsesSystemResolver => !_compareMode && _useSystem && _selectedType.Type is null;

    public bool CanStart
        => !IsBusy && Target.Trim().Length > 0
           && (_compareMode ? CompareServers.Count > 0 : _useSystem || _explicitServer is not null);

    private void RaiseServerState()
    {
        Raise(nameof(ServerError));
        Raise(nameof(HasServerError));
        Raise(nameof(UsesSystemResolver));
        Raise(nameof(CanStart));
    }

    /// <summary>Adds <see cref="NewServer"/> to the compare list; a bad or surplus entry sets the error text instead.</summary>
    public bool AddCompareServer()
    {
        if (!TryParseServer(NewServer, out var ip)) { SetAddError(Loc.DnsServerInvalid); return false; }
        var text = ip.ToString();
        if (CompareServers.Contains(text)) { NewServer = ""; return true; }
        if (CompareServers.Count >= MaxCompareServers) { SetAddError(Loc.DnsCompareMax); return false; }
        CompareServers.Add(text);
        NewServer = "";
        Raise(nameof(CanStart));
        return true;
    }

    public void RemoveCompareServer(string server)
    {
        if (CompareServers.Remove(server)) Raise(nameof(CanStart));
    }

    private void SetAddError(string text)
    {
        _addError = text;
        Raise(nameof(ServerError));
        Raise(nameof(HasServerError));
    }

    /// <summary>An IPv4 (dotted quad) or IPv6 address that can serve as a DNS server.</summary>
    internal static bool TryParseServer(string? text, out IPAddress ip)
    {
        ip = IPAddress.None;
        text = text?.Trim() ?? "";
        if (text.Length == 0 || text.Contains('%') || !IPAddress.TryParse(text, out var parsed)) return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            if (text.Count(c => c == '.') != 3 || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.Broadcast)) return false;
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || parsed.Equals(IPAddress.IPv6Any)) return false;
        ip = parsed;
        return true;
    }

    public void LoadDnsServers()
    {
        DnsServers.Clear();
        foreach (var ip in GetConfiguredDnsServers()) DnsServers.Add(ip);

        while (ServerChoices.Count > 1) ServerChoices.RemoveAt(1);
        foreach (var ip in SystemServerProvider()) ServerChoices.Add(ip.ToString());
    }

    /// <summary>Re-reads the texts that live in collections after a language switch.</summary>
    public void RefreshLanguage()
    {
        ServerChoices[0] = Loc.DnsServerSystem;
        if (_useSystem) { _server = Loc.DnsServerSystem; Raise(nameof(Server)); }

        var current = _selectedType;
        TypeOptions[0] = new DnsTypeOption(Loc.DnsModeAuto, null);
        _selectedType = current.Type is null ? TypeOptions[0] : current;
        Raise(nameof(SelectedType));
        if (_comboError.Length > 0) { _comboError = Loc.DnsServerInvalid; Raise(nameof(ServerError)); }
    }

    internal static string[] GetConfiguredDnsServers()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToArray();

    /// <summary>The servers of the active adapters in adapter order, IPv4 and IPv6, without scoped
    /// (link-local, site-local) addresses a plain socket cannot reach.</summary>
    internal static IReadOnlyList<IPAddress> GetSystemServerAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                            || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal))
                .DistinctBy(a => a.ToString())
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    internal static string ResolveDirection(string target, string mode)
        => mode == ModeAuto ? (AddressSyntax.IsValidAddress(target) ? ModeReverse : ModeForward) : mode;

    // ── Running ──

    private void ClearResults()
    {
        Addresses.Clear();
        Aliases.Clear();
        Records.Clear();
        CompareRows.Clear();
        CanonicalName = null;
        ShowForwardResults = false;
        ShowRecordResults = false;
        ShowCompareResults = false;
        Status = "";
    }

    public async Task StartAsync()
    {
        if (!CanStart) return;

        ClearResults();
        IsBusy = true;

        var target = Target.Trim();
        var viaSystem = UsesSystemResolver;
        _cts = viaSystem ? new CancellationTokenSource(LookupTimeout) : new CancellationTokenSource();
        try
        {
            if (viaSystem)
            {
                var direction = ResolveDirection(target, Mode);
                if (direction == ModeReverse) await ResolveReverseAsync(target, _cts.Token);
                else await ResolveForwardAsync(target, _cts.Token);
            }
            else if (!IsAskable(target, _selectedType.Type)) Status = Loc.DnsBadName;
            else if (_compareMode) await CompareAsync(target, _cts.Token);
            else await QueryServerAsync(target, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.DnsTimeout;
        }
        catch (SocketException)
        {
            Status = Loc.DnsNotFound;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ResolveForwardAsync(string host, CancellationToken ct)
    {
        var result = await Dns.GetHostAddressesAsync(host, ct);
        if (result.Length == 0) { Status = Loc.DnsNotFound; return; }
        foreach (var a in result) Addresses.Add(a.ToString());
        ShowForwardResults = true;
    }

    private async Task ResolveReverseAsync(string ip, CancellationToken ct)
    {
        var entry = await Dns.GetHostEntryAsync(ip, ct);
        if (string.IsNullOrEmpty(entry.HostName)) { Status = Loc.DnsNotFound; return; }
        CanonicalName = HostnameResolver.Normalize(entry.HostName);
        foreach (var alias in entry.Aliases) Aliases.Add(alias);
    }

    private static bool IsAskable(string target, DnsRecordType? type)
        => (type is null || type == DnsRecordType.PTR) && DnsClient.ReverseName(target) is not null
           || DnsClient.IsValidName(target);

    private Task<DnsResult> AskAsync(IPAddress server, string target, DnsRecordType? type, CancellationToken ct)
        => type is null
            ? DnsClient.QueryAutoAsync(server, target, QueryTimeout, ct, Port)
            : DnsClient.QueryAsync(server, type == DnsRecordType.PTR ? DnsClient.ReverseName(target) ?? target : target,
                type.Value, QueryTimeout, ct, Port);

    private async Task QueryServerAsync(string target, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> servers = _useSystem
            ? SystemServerProvider()
            : _explicitServer is { } one ? new[] { one } : Array.Empty<IPAddress>();
        if (servers.Count == 0) { Status = Loc.DnsNoSystemServer; return; }

        // Several system servers act as fallbacks: the first one that answers wins.
        DnsResult? result = null;
        IPAddress? used = null;
        foreach (var server in servers)
        {
            used = server;
            result = await AskAsync(server, target, _selectedType.Type, ct);
            if (result.Ok) break;
        }

        foreach (var r in result!.Answers)
            Records.Add(new DnsRecordRow(r.Type.ToString(), r.Name, r.Data, r.Ttl.ToString(CultureInfo.InvariantCulture)));
        ShowRecordResults = Records.Count > 0;
        Status = StatusFor(used!.ToString(), result);
    }

    private async Task CompareAsync(string target, CancellationToken ct)
    {
        var servers = CompareServers.Select(s => IPAddress.Parse(s)).ToList();
        var results = await Task.WhenAll(servers.Select(s => AskAsync(s, target, _selectedType.Type, ct)));
        var names = servers.Select(s => s.ToString()).ToList();
        var (rows, fastest) = BuildCompareRows(names, results);
        foreach (var row in rows) CompareRows.Add(row);
        ShowCompareResults = true;
        Status = fastest >= 0
            ? StatusFor(names[fastest], results[fastest])
            : string.Format(Loc.DnsNoReplyFmt, string.Join(", ", names), SecondsText());
    }

    private string SecondsText() => QueryTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    private string StatusFor(string server, DnsResult result) => result.Failure switch
    {
        DnsFailure.None => string.Format(Loc.DnsReplyFmt, server, result.ElapsedMs.ToString(CultureInfo.InvariantCulture), RcodeName(result.Rcode)),
        DnsFailure.Timeout => string.Format(Loc.DnsNoReplyFmt, server, SecondsText()),
        DnsFailure.Malformed => string.Format(Loc.DnsBadReplyFmt, server),
        _ => string.Format(Loc.DnsFailedFmt, server),
    };

    internal static string RcodeName(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => "RCODE " + rcode.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Builds the compare table. The fastest row is the replying server with the lowest time; a row
    /// differs when its answer set (ignoring TTL and order) is not the one most replying servers gave, the
    /// first server deciding a tie. Servers that did not reply are never marked as differing.</summary>
    internal static (List<DnsCompareRow> Rows, int Fastest) BuildCompareRows(IReadOnlyList<string> servers, IReadOnlyList<DnsResult> results)
    {
        var keys = new string?[results.Count];
        for (var i = 0; i < results.Count; i++)
            if (results[i].Ok)
                keys[i] = results[i].Rcode.ToString(CultureInfo.InvariantCulture) + "|" + string.Join("\n",
                    results[i].Answers.Select(a => $"{a.Type} {a.Name} {a.Data}".ToLowerInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal));

        var replying = Enumerable.Range(0, results.Count).Where(i => keys[i] is not null).ToList();
        string? majority = null;
        if (replying.Count > 1)
        {
            var top = replying.GroupBy(i => keys[i]).Max(g => g.Count());
            majority = keys[replying.First(i => replying.Count(j => keys[j] == keys[i]) == top)];
        }

        var fastest = -1;
        foreach (var i in replying)
            if (fastest < 0 || results[i].ElapsedMs < results[fastest].ElapsedMs) fastest = i;

        var rows = new List<DnsCompareRow>();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var ok = r.Ok;
            var code = ok ? RcodeName(r.Rcode) : r.Failure == DnsFailure.Timeout ? Loc.DnsCodeTimeout : Loc.DnsCodeError;
            var answers = r.Answers.Count > 0 ? string.Join("; ", r.Answers.Select(a => a.Data)) : "–";
            rows.Add(new DnsCompareRow(servers[i], ok ? r.ElapsedMs.ToString(CultureInfo.InvariantCulture) : "–", code, answers,
                i == fastest, majority is not null && keys[i] is not null && keys[i] != majority));
        }
        return (rows, fastest);
    }

    /// <summary>The visible result as tab-separated text for the clipboard; empty when there is none.</summary>
    public string CopyText()
    {
        IEnumerable<string> lines;
        if (ShowCompareResults) lines = CompareRows.Select(r => string.Join("\t", r.Server, r.Time, r.Code, r.Answers));
        else if (ShowRecordResults) lines = Records.Select(r => string.Join("\t", r.Type, r.Name, r.Value, r.Ttl));
        else if (ShowReverseResults) lines = new[] { CanonicalName ?? "" }.Concat(Aliases);
        else lines = Addresses;
        return string.Join(Environment.NewLine, lines);
    }
}
