using System.Collections.ObjectModel;
using System.Linq;
using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

/// <summary>Two-way bindable mirror of ScanConfig for the settings window.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ScanConfig cfg)
    {
        _pingCount = cfg.PingCount;
        _pingIntervalMs = cfg.PingIntervalMs;
        _offlineAfter = cfg.OfflineAfterFailedPings;
        _initPingCount = cfg.InitPingCount;
        HighPressureMode = cfg.HighPressureMode;
        EnableInternetPing = cfg.EnableInternetPing;
        KnownDevicesDb = cfg.KnownDevicesDb;
        FileOutput = cfg.FileOutput;
        ExportCsv = cfg.ExportCsv;
        _outputDirectory = cfg.OutputDirectory;
        _pingThreads = cfg.PingThreads;
        _initPingThreads = cfg.InitPingThreads;
        Subnets = new(cfg.Subnets);
        PinnedIps = new(cfg.PinnedIps);
        InternetHosts = new(cfg.InternetHosts);
    }

    private int _pingCount, _pingIntervalMs, _offlineAfter, _initPingCount, _pingThreads, _initPingThreads;
    private string _outputDirectory;

    public int PingCount { get => _pingCount; set => SetProperty(ref _pingCount, value); }
    public int PingIntervalMs { get => _pingIntervalMs; set => SetProperty(ref _pingIntervalMs, value); }
    public int OfflineAfterFailedPings { get => _offlineAfter; set => SetProperty(ref _offlineAfter, value); }
    public int InitPingCount { get => _initPingCount; set => SetProperty(ref _initPingCount, value); }
    public bool HighPressureMode { get; set; }
    public bool EnableInternetPing { get; set; }
    public bool KnownDevicesDb { get; set; }
    public bool FileOutput { get; set; }
    public bool ExportCsv { get; set; }
    public string OutputDirectory { get => _outputDirectory; set => SetProperty(ref _outputDirectory, value); }
    public int PingThreads { get => _pingThreads; set => SetProperty(ref _pingThreads, value); }
    public int InitPingThreads { get => _initPingThreads; set => SetProperty(ref _initPingThreads, value); }

    public ObservableCollection<string> Subnets { get; }
    public ObservableCollection<string> PinnedIps { get; }
    public ObservableCollection<string> InternetHosts { get; }

    public ScanConfig ToConfig() => new()
    {
        PingCount = PingCount,
        PingIntervalMs = PingIntervalMs,
        OfflineAfterFailedPings = OfflineAfterFailedPings,
        InitPingCount = InitPingCount,
        HighPressureMode = HighPressureMode,
        EnableInternetPing = EnableInternetPing,
        KnownDevicesDb = KnownDevicesDb,
        FileOutput = FileOutput,
        ExportCsv = ExportCsv,
        OutputDirectory = OutputDirectory,
        PingThreads = PingThreads,
        InitPingThreads = InitPingThreads,
        Subnets = Subnets.ToList(),
        PinnedIps = PinnedIps.ToList(),
        InternetHosts = InternetHosts.ToList(),
    };
}
