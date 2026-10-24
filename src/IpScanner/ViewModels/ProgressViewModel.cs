using System.Globalization;
using IpScanner.Core;
using IpScanner.Core.Localization;

namespace IpScanner.ViewModels;

public sealed class ProgressViewModel : ObservableObject
{
    private int _online, _offline, _deviceTotal;
    private long _success, _failed, _skipped, _pingTotal;

    // Remaining-time estimate. Smoothed over the last three
    // measurements so the number does not jump around from one tick to the next.
    private DateTime? _scanStart;
    private readonly List<double> _recentRemainingSeconds = new();
    private string _etaText = "";

    public void SetDevices(int online, int offline, int total)
    {
        _online = online; _offline = offline; _deviceTotal = Math.Max(1, total);
        Raise(nameof(OnlineFraction)); Raise(nameof(OfflineFraction));
        Raise(nameof(OnlineCount)); Raise(nameof(OfflineCount));
        Raise(nameof(OnlineText)); Raise(nameof(OfflineText));
        Raise(nameof(DeviceCountText));
    }

    /// <summary>Back to the empty start state: the counters describe a table that no longer exists.</summary>
    public void Reset()
    {
        SetDevices(0, 0, 0);
        _deviceTotal = 0;
        SetPings(0, 0, 0, 0);
        _pingTotal = 0;
        Raise(nameof(DeviceCountText)); Raise(nameof(PingCountText));
        EndScan();
    }

    /// <summary>Call once when a new scan begins so the ETA measures
    /// elapsed time from the right point instead of from app startup.</summary>
    public void StartScan()
    {
        _scanStart = DateTime.UtcNow;
        _recentRemainingSeconds.Clear();
        _etaText = "";
        Raise(nameof(EtaText));
    }

    /// <summary>Call when a scan finishes or is cancelled: a remaining-time
    /// estimate no longer applies, so the text is cleared and stays empty.</summary>
    public void EndScan()
    {
        _scanStart = null;
        _recentRemainingSeconds.Clear();
        if (_etaText.Length == 0) return;
        _etaText = "";
        Raise(nameof(EtaText));
    }

    public void SetPings(long success, long failed, long skipped, long total)
    {
        _success = success; _failed = failed; _skipped = skipped; _pingTotal = Math.Max(1, total);
        Raise(nameof(SuccessFraction)); Raise(nameof(FailedFraction)); Raise(nameof(SkippedFraction));
        Raise(nameof(SuccessText)); Raise(nameof(FailedText)); Raise(nameof(SkippedText));
        Raise(nameof(PingCountText)); Raise(nameof(PingCountTip));
        UpdateEta();
    }

    private void UpdateEta()
    {
        string next = "";
        if (!InfinitePings && _scanStart is { } start)
        {
            double fraction = EtaFraction(_success, _failed, _skipped, _pingTotal);
            var remaining = ComputeRemaining(fraction, DateTime.UtcNow - start);
            if (remaining is { } r)
            {
                _recentRemainingSeconds.Add(r.TotalSeconds);
                if (_recentRemainingSeconds.Count > 3) _recentRemainingSeconds.RemoveAt(0);
                double smoothed = _recentRemainingSeconds.Average();
                next = FormatEta(TimeSpan.FromSeconds(smoothed));
            }
        }
        if (next == _etaText) return;
        _etaText = next;
        Raise(nameof(EtaText));
    }

    /// <summary>Progress of the pings that really take time. Skipped pings finish at once, so they
    /// are left out of both sides: counting them would make a mostly skipped scan look nearly done.</summary>
    internal static double EtaFraction(long success, long failed, long skipped, long total)
        => (double)(success + failed) / Math.Max(1, total - skipped);

    /// <summary>Below 5 % the estimate is too noisy to be worth showing.</summary>
    internal static TimeSpan? ComputeRemaining(double fraction, TimeSpan elapsed)
    {
        if (fraction < 0.05) return null;
        double totalEstimateSeconds = elapsed.TotalSeconds / fraction;
        double remainingSeconds = Math.Max(0, totalEstimateSeconds - elapsed.TotalSeconds);
        return TimeSpan.FromSeconds(remainingSeconds);
    }

    internal static string FormatEta(TimeSpan remaining)
    {
        var c = CultureInfo.CurrentCulture;
        int totalSeconds = (int)Math.Round(remaining.TotalSeconds);
        if (totalSeconds < 1) return "";
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return minutes > 0
            ? string.Format(Loc.ProgressEtaMinSecFmt, minutes.ToString(c), seconds.ToString("00", c))
            : string.Format(Loc.ProgressEtaSecFmt, seconds.ToString(c));
    }

    /// <summary>Remaining-time estimate shown next to the device
    /// counter; empty before 5 % progress or while InfinitePings is on.</summary>
    public string EtaText => _etaText;

    public double OnlineFraction  => (double)_online  / Math.Max(1, _deviceTotal);
    public double OfflineFraction => (double)_offline / Math.Max(1, _deviceTotal);
    public int OnlineCount  => _online;
    public int OfflineCount => _offline;

    // Compact (k/M) legend texts, e.g. "Online 14", "Offline 1,2k".
    public string OnlineText  => $"{Loc.Online} {NumberFormat.Short(_online)}";
    public string OfflineText => $"{Loc.Offline} {NumberFormat.Short(_offline)}";
    public string DeviceCountText => $"{NumberFormat.Short(_online + _offline)} / {NumberFormat.Short(_deviceTotal)}";

    public double SuccessFraction => (double)_success / Math.Max(1, _pingTotal);
    public double FailedFraction  => (double)_failed  / Math.Max(1, _pingTotal);
    public double SkippedFraction => (double)_skipped / Math.Max(1, _pingTotal);
    public string SuccessText => $"{Loc.Success} {NumberFormat.Short(_success)}";
    public string FailedText  => $"{Loc.Fail} {NumberFormat.Short(_failed)}";
    public string SkippedText => $"{Loc.Skipped} {NumberFormat.Short(_skipped)}";

    /// <summary>Endless run: the ping total shows ∞ instead of a number.</summary>
    public bool InfinitePings { get; set; }

    /// <summary>Re-raises the Loc-backed legend texts after a language switch.</summary>
    public void RefreshLanguage()
    {
        Raise(nameof(OnlineText)); Raise(nameof(OfflineText));
        Raise(nameof(SuccessText)); Raise(nameof(FailedText)); Raise(nameof(SkippedText));
        Raise(nameof(PingCountTip));
        UpdateEta();   // EtaText caches a Loc-formatted string, not a live binding
    }

    public string? PingCountTip => _skipped > 0 ? Loc.PingCountSkippedTip(_skipped) : null;

    public string PingCountText =>
        $"{NumberFormat.Short(_success + _failed + _skipped)} / {(InfinitePings ? "∞" : NumberFormat.Short(_pingTotal))}";
}
