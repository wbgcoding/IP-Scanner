using System;
using System.Collections.ObjectModel;
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

/// <summary>DNS lookup tool page state: one field, direction auto-detected
/// (a valid IPv4 address reverse-resolves, anything else forward-resolves) or forced
/// via <see cref="Mode"/>, plus the machine's own configured DNS servers.</summary>
public sealed class DnsLookupToolViewModel : ObservableObject
{
    public const string ModeAuto = "Auto";
    public const string ModeForward = "Forward";
    public const string ModeReverse = "Reverse";

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cts;

    /// <summary>Cancels an in-flight lookup - called when the Tools window closes
    /// so a request never outlives its own page.</summary>
    public void Stop() => _cts?.Cancel();

    public ObservableCollection<string> Addresses { get; } = new();
    public ObservableCollection<string> Aliases { get; } = new();
    public ObservableCollection<string> DnsServers { get; } = new();

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

    public bool CanStart => !IsBusy && Target.Trim().Length > 0;

    public void LoadDnsServers()
    {
        DnsServers.Clear();
        foreach (var ip in GetConfiguredDnsServers()) DnsServers.Add(ip);
    }

    internal static string[] GetConfiguredDnsServers()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToArray();

    internal static string ResolveDirection(string target, string mode)
        => mode == ModeAuto ? (AddressSyntax.IsValidAddress(target) ? ModeReverse : ModeForward) : mode;

    public async Task StartAsync()
    {
        if (!CanStart) return;

        Addresses.Clear();
        Aliases.Clear();
        CanonicalName = null;
        ShowForwardResults = false;
        Status = "";
        IsBusy = true;

        var target = Target.Trim();
        var direction = ResolveDirection(target, Mode);
        _cts = new CancellationTokenSource(LookupTimeout);
        try
        {
            if (direction == ModeReverse) await ResolveReverseAsync(target, _cts.Token);
            else await ResolveForwardAsync(target, _cts.Token);
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
}
