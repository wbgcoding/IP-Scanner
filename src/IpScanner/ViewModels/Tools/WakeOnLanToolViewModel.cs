using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;

namespace IpScanner.ViewModels.Tools;

public sealed record KnownDeviceOption(string Name, string Mac, string? Ip)
{
    public override string ToString() => string.IsNullOrEmpty(Ip) ? $"{Name} ({Mac})" : $"{Name} · {Ip}";
}

public sealed record NetworkCardOption(string Name, string Ip, string? SubnetMask)
{
    public override string ToString() => $"{Name} ({Ip})";
}

public sealed record RecentWakeTarget(string Mac, string Label)
{
    public string Display => string.IsNullOrEmpty(Label) ? Mac : Label;
}

/// <summary>Wake-on-LAN tool page state: MAC entry, a known-device
/// picker, a network-card picker for the directed broadcast, up to five recent
/// targets as quick-select buttons, and the send-then-poll flow itself.</summary>
public sealed class WakeOnLanToolViewModel : ObservableObject
{
    private ScanConfig? _config;
    private Action<List<string>>? _persistRecent;
    private CancellationTokenSource? _cts;

    public ObservableCollection<KnownDeviceOption> KnownDevices { get; } = new();
    public ObservableCollection<NetworkCardOption> NetworkCards { get; } = new();
    public ObservableCollection<RecentWakeTarget> RecentTargets { get; } = new();

    private string _mac = "";
    public string Mac
    {
        get => _mac;
        set { if (_mac != value) { _mac = value; Raise(nameof(Mac)); Raise(nameof(CanSend)); } }
    }

    private string? _targetIp;
    public string? TargetIp
    {
        get => _targetIp;
        set { if (_targetIp != value) { _targetIp = value; Raise(nameof(TargetIp)); } }
    }

    private NetworkCardOption? _selectedCard;
    public NetworkCardOption? SelectedCard
    {
        get => _selectedCard;
        set { if (!Equals(_selectedCard, value)) { _selectedCard = value; Raise(nameof(SelectedCard)); } }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(CanSend)); } }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Raise(nameof(Status)); } }
    }

    public bool CanSend => !IsBusy && Core.Scanner.WakeOnLan.IsValidMac(Mac);
    public bool HasRecentTargets => RecentTargets.Count > 0;

    public bool Initialized { get; private set; }

    public WakeOnLanToolViewModel()
        => RecentTargets.CollectionChanged += (_, _) => Raise(nameof(HasRecentTargets));

    /// <summary>Called once by the tool page on first load - reads the known
    /// devices for the current network and the machine's network cards off the UI
    /// thread, then only touches the bound collections back on it.</summary>
    public async Task InitializeAsync(ScanConfig config, Action<List<string>> persistRecent)
    {
        if (Initialized) return;
        Initialized = true;
        try { await LoadAsync(config, persistRecent); }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Tools, "Loading the Wake on LAN tool failed", ex);
            // A failed first load must not leave half-filled lists behind: the next load starts clean.
            Initialized = false;
            RecentTargets.Clear();
            NetworkCards.Clear();
            KnownDevices.Clear();
            throw;
        }
    }

    private async Task LoadAsync(ScanConfig config, Action<List<string>> persistRecent)
    {
        _config = config;
        _persistRecent = persistRecent;

        foreach (var entry in config.RecentWakeTargets)
        {
            var parts = entry.Split(' ', 2);
            if (parts.Length == 0 || !Core.Scanner.WakeOnLan.IsValidMac(parts[0])) continue;
            RecentTargets.Add(new RecentWakeTarget(parts[0], parts.Length > 1 ? parts[1] : parts[0]));
        }

        var cards = await Task.Run(() => Core.Scanner.NetworkDetector.ListIpv4Cards()).ConfigureAwait(true);
        var localIp = await Task.Run(() => Core.Scanner.NetworkDetector.GetLocalIpFast()).ConfigureAwait(true);
        foreach (var c in cards) NetworkCards.Add(new NetworkCardOption(c.Name, c.Ip, c.SubnetMask));
        SelectedCard = NetworkCards.FirstOrDefault(c => c.Ip == localIp) ?? NetworkCards.FirstOrDefault();

        if (config.KnownDevicesDb)
        {
            var known = await Task.Run(() => LoadKnownDevices(config)).ConfigureAwait(true);
            foreach (var d in known) KnownDevices.Add(d);
        }
    }

    private static List<KnownDeviceOption> LoadKnownDevices(ScanConfig config)
    {
        try
        {
            var info = Core.Scanner.NetworkDetector.DetectFast();
            if (info.Gateway is not { } gatewayIp) return new();
            var db = new KnownDevicesDb(config.DatabasePath);
            if (db.GetNetworkMac(gatewayIp) is not { } netMac) return new();
            return db.Load(netMac)
                .Where(d => Core.Scanner.WakeOnLan.IsValidMac(d.Mac))
                .Select(d => new KnownDeviceOption(string.IsNullOrEmpty(d.Hostname) ? d.Ip : d.Hostname!, d.Mac!, d.Ip))
                .ToList();
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Tools, "Listing network cards failed", ex); return new(); }
    }

    public void PickKnownDevice(KnownDeviceOption device)
    {
        Mac = device.Mac;
        TargetIp = device.Ip;
    }

    public void PickRecentTarget(RecentWakeTarget target)
    {
        Mac = target.Mac;
        // A recycled MAC has no known live IP until re-picked from the known-device list.
        TargetIp = null;
    }

    public void Stop() => _cts?.Cancel();

    public async Task SendAsync()
    {
        if (!CanSend) return;
        IsBusy = true;
        Status = Loc.WakeSendingStatus;
        _cts = new CancellationTokenSource();
        var mac = Mac;
        var ip = TargetIp;
        try
        {
            IPAddress? directed = null;
            if (SelectedCard is { } card && Core.Net.Ipv4.DirectedBroadcast(card.Ip, card.SubnetMask) is { } b)
                IPAddress.TryParse(b, out directed);

            bool sent = await Core.Scanner.WakeOnLan.SendAsync(mac, directed, repeats: 3, ct: _cts.Token);
            if (!sent) { Status = Loc.WakeFailedMsg; return; }

            RememberTarget(mac);

            if (string.IsNullOrEmpty(ip)) { Status = Loc.WakeSentNoIpMsg; return; }

            await PollAsync(ip, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.WakeStoppedMsg;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Pings every 3s for up to 120s, showing a live countdown - stoppable
    /// via the CancellationToken (the Stop button).</summary>
    private async Task PollAsync(string ip, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { Status = Loc.WakeNoAnswerMsg; return; }
            Status = Loc.WakeWaitingMsg(remaining.ToString(@"m\:ss"));

            var result = await Task.Run(() => (Core.Scanner.Ipv6.Ipv6Pinger.IsIpv6(ip) ? Core.Scanner.Ipv6.Ipv6Pinger.Ping(ip, 800) : Core.Scanner.IcmpPinger.Ping(ip, 800)), ct);
            if (result.Success) { Status = Loc.WakeDeviceBackMsg; return; }

            await Task.Delay(3000, ct);
        }
    }

    private void RememberTarget(string mac)
    {
        if (_config is null) return;

        var label = KnownDevices.FirstOrDefault(d => string.Equals(d.Mac, mac, StringComparison.OrdinalIgnoreCase))?.Name ?? mac;
        var updated = new List<string> { $"{mac} {label}" };
        updated.AddRange(_config.RecentWakeTargets.Where(e => !e.StartsWith(mac, StringComparison.OrdinalIgnoreCase)));
        var capped = updated.Take(5).ToList();

        _config.RecentWakeTargets = capped;
        _persistRecent?.Invoke(capped);

        RecentTargets.Clear();
        foreach (var entry in capped)
        {
            var parts = entry.Split(' ', 2);
            RecentTargets.Add(new RecentWakeTarget(parts[0], parts.Length > 1 ? parts[1] : parts[0]));
        }
    }
}
