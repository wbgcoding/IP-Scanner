using IpScanner.Core;
using IpScanner.Core.Localization;

namespace IpScanner.ViewModels;

public sealed class ProgressViewModel : ObservableObject
{
    private int _online, _offline, _unknown, _deviceTotal;
    private long _success, _failed, _skipped, _pingTotal;
    private string _phase = Loc.PhaseReady;

    public string Phase { get => _phase; set => SetProperty(ref _phase, value); }

    public void SetDevices(int online, int offline, int unknown, int total)
    {
        _online = online; _offline = offline; _unknown = unknown; _deviceTotal = Math.Max(1, total);
        Raise(nameof(OnlineFraction)); Raise(nameof(OfflineFraction)); Raise(nameof(UnknownFraction));
        Raise(nameof(OnlineCount)); Raise(nameof(OfflineCount)); Raise(nameof(UnknownCount));
        Raise(nameof(OnlineText)); Raise(nameof(OfflineText)); Raise(nameof(UnknownText));
        Raise(nameof(DeviceCountText));
    }

    public void SetPings(long success, long failed, long skipped, long total)
    {
        _success = success; _failed = failed; _skipped = skipped; _pingTotal = Math.Max(1, total);
        Raise(nameof(SuccessFraction)); Raise(nameof(FailedFraction)); Raise(nameof(SkippedFraction));
        Raise(nameof(SuccessText)); Raise(nameof(FailedText)); Raise(nameof(SkippedText));
        Raise(nameof(PingCountText));
    }

    public double OnlineFraction  => (double)_online  / _deviceTotal;
    public double OfflineFraction => (double)_offline / _deviceTotal;
    public double UnknownFraction => (double)_unknown / _deviceTotal;
    public int OnlineCount  => _online;
    public int OfflineCount => _offline;
    public int UnknownCount => _unknown;

    // Compact (k/M) legend texts, e.g. "Online 14", "Offline 1,2k".
    public string OnlineText  => $"{Loc.Online} {NumberFormat.Short(_online)}";
    public string OfflineText => $"{Loc.Offline} {NumberFormat.Short(_offline)}";
    public string UnknownText => $"{Loc.Unknown} {NumberFormat.Short(_unknown)}";
    public string DeviceCountText => $"{NumberFormat.Short(_online + _offline + _unknown)} / {NumberFormat.Short(_deviceTotal)}";

    public double SuccessFraction => (double)_success / _pingTotal;
    public double FailedFraction  => (double)_failed  / _pingTotal;
    public double SkippedFraction => (double)_skipped / _pingTotal;
    public string SuccessText => $"{Loc.Success} {NumberFormat.Short(_success)}";
    public string FailedText  => $"{Loc.Fail} {NumberFormat.Short(_failed)}";
    public string SkippedText => $"{Loc.Skipped} {NumberFormat.Short(_skipped)}";
    public string PingCountText => $"{NumberFormat.Short(_success + _failed + _skipped)} / {NumberFormat.Short(_pingTotal)}";
}
