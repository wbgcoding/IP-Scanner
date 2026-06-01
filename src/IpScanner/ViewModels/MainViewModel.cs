using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Func<string, int, PingResult> _pingFunc;
    private readonly Func<NetworkInfo> _detectNetwork;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cts;

    public MainViewModel(Func<string, int, PingResult> pingFunc,
                         Func<NetworkInfo> detectNetwork,
                         Action<Action> dispatch)
    {
        _pingFunc = pingFunc;
        _detectNetwork = detectNetwork;
        _dispatch = dispatch;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<NetworkInfoViewModel> Networks { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ScanConfig Config { get; set; } = new();

    private readonly Dictionary<string, DeviceViewModel> _byIp = new();
    private readonly object _byIpLock = new();

    public async Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null)
    {
        _cts = new CancellationTokenSource();
        var info = _detectNetwork();
        var prefixes = subnetOverride ?? BuildPrefixes(info, Config.Subnets);

        _dispatch(() =>
        {
            Networks.Clear();
            Networks.Add(new NetworkInfoViewModel(1, info, GroupColorPalette.ColorForIndex(0)));
            Progress.Phase = "Discovery";
        });

        var engine = new ScanEngine(_pingFunc);
        engine.DeviceUpdated += OnDeviceUpdated;
        engine.Progress.Changed += () => _dispatch(UpdateProgress);

        await engine.ScanAsync(prefixes, Config, _cts.Token);

        _dispatch(() =>
        {
            UpdateProgress();
            Progress.Phase = "Bereit";
        });
    }

    public void Stop() => _cts?.Cancel();

    private static IReadOnlyList<string> BuildPrefixes(NetworkInfo info, List<string> extra)
    {
        var list = new List<string>();
        if (info.Ip is not null) list.Add(Ipv4.SubnetPrefix(info.Ip));
        foreach (var s in extra)
        {
            var prefix = Ipv4.SubnetPrefix(s.Split('/')[0]);
            if (!list.Contains(prefix)) list.Add(prefix);
        }
        return list;
    }

    private void OnDeviceUpdated(Device d) => _dispatch(() =>
    {
        lock (_byIpLock)
        {
            if (_byIp.TryGetValue(d.Ip, out var existing)) { existing.Refresh(); return; }
            var vm = new DeviceViewModel(d);
            _byIp[d.Ip] = vm;
            Devices.Add(vm);
        }
    });

    private void UpdateProgress()
    {
        int online, total;
        lock (_byIpLock)
        {
            online = Devices.Count(d => d.IsOnline);
            total = Devices.Count;
        }
        Progress.SetDevices(online, total - online, 0, Math.Max(total, 1));
    }
}
