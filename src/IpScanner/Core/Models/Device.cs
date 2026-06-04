namespace IpScanner.Core.Models;

public sealed class Device
{
    /// <summary>Sentinel for an unresolved MAC/hostname.</summary>
    public const string Unknown = "Unknown";

    public Device(string ip) => Ip = ip;

    public string Ip { get; }
    public string? Mac { get; set; }
    public string? Hostname { get; set; }
    /// <summary>Rank (technique index) that produced Hostname/Mac — lower is
    /// better; a later, better-ranked result replaces a worse one.</summary>
    public int HostnameRank { get; set; } = int.MaxValue;
    public int MacRank { get; set; } = int.MaxValue;
    public int GroupId { get; set; }
    public bool FromDb { get; set; }
    public bool Seen { get; private set; }
    public int CurrentPings { get; private set; }
    public int TargetPings { get; set; } = 10;
    public int OfflineAfterFailures { get; set; } = 5;

    public int SuccessCount { get; private set; }
    public int FailCount { get; private set; }
    /// <summary>True when the most recent ping failed (table shows N/A).</summary>
    public bool LastFailed { get; private set; }
    public double? MinMs { get; private set; }
    public double? MaxMs { get; private set; }
    public double? AvgMs { get; private set; }
    public double? LastMs { get; private set; }

    private int _consecutiveFails;
    private double _sumMs;

    public bool IsOnline { get; private set; }
    public bool WentOffline { get; private set; }

    public void RecordPing(PingResult r)
    {
        CurrentPings++;
        if (r.Success)
        {
            Seen = true;
            IsOnline = true;
            WentOffline = false;
            _consecutiveFails = 0;
            SuccessCount++;
            LastFailed = false;
            if (r.LatencyMs is { } ms)
            {
                LastMs = ms;
                MinMs = MinMs is null ? ms : Math.Min(MinMs.Value, ms);
                MaxMs = MaxMs is null ? ms : Math.Max(MaxMs.Value, ms);
                _sumMs += ms;
                AvgMs = _sumMs / SuccessCount;
            }
        }
        else
        {
            FailCount++;
            LastFailed = true;
            _consecutiveFails++;
            if (Seen && _consecutiveFails >= OfflineAfterFailures)
            {
                IsOnline = false;
                WentOffline = true;
            }
        }
    }
}
