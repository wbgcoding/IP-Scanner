namespace IpScanner.Core.Models;

public sealed class Device
{
    /// <summary>Sentinel for an unresolved MAC/hostname.</summary>
    public const string Unknown = "Unknown";

    public Device(string ip) => Ip = ip;

    public string Ip { get; }
    public string? Mac { get; set; }
    public string? Hostname { get; set; }
    public string? Note { get; set; }
    /// <summary>Code of the discovery probe that found the host (icmp, tcp:445, arp, list, skip, ...).</summary>
    public string? DiscoveryMethod { get; set; }
    /// <summary>TTL of the latest echo reply, the main input of the operating system estimate.</summary>
    public int? Ttl { get; private set; }
    /// <summary>Result of the active fingerprint, when one ran. Null = only the heuristic applies.</summary>
    public Scanner.Os.OsEstimate? OsFingerprint { get; set; }
    /// <summary>Rank (technique index) that produced Hostname/Mac - lower is
    /// better; a later, better-ranked result replaces a worse one.</summary>
    public int HostnameRank { get; set; } = int.MaxValue;
    public int MacRank { get; set; } = int.MaxValue;
    public int GroupId { get; set; }
    // Snapshot of the automatic hostname, kept while a manual override is
    // active so the reset button can restore it instantly.
    public string? AutoHostname { get; set; }
    public int AutoHostnameRank { get; set; } = int.MaxValue;
    /// <summary>Manual group-square color (hex), null = automatic group color.</summary>
    public string? GroupColorOverride { get; set; }
    /// <summary>Mirrors the overrides-store "muted" flag - suppresses
    /// offline coloring, the rogue highlight and the fast offline-recheck burst
    /// for a device that is expected to go offline routinely (e.g. a printer
    /// switched off at night). Still pinged normally; only presentation and the
    /// recheck cadence change.</summary>
    public bool IsMuted { get; set; }
    /// <summary>Set when a second, different MAC answered for this same
    /// IP during the current run (ScanEngine.ApplyEnrichRank) - a same-or-worse-
    /// ranked resolver disagreeing with the already-applied MAC.</summary>
    public bool HasMacConflictThisRun { get; set; }
    public bool FromDb { get; set; }
    /// <summary>When the known-devices database first saw this device online (local time), null when unknown.</summary>
    public DateTimeOffset? FirstSeen { get; set; }
    /// <summary>When the known-devices database last saw this device online (local time), null when unknown.</summary>
    public DateTimeOffset? LastSeen { get; set; }
    public bool Seen { get; private set; }
    /// <summary>True for a host that a list scan only named; nothing was sent to it.</summary>
    public bool IsListed => DiscoveryMethod == "list";
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

    /// <summary>Share of pings that got no reply, of all pings sent so far.</summary>
    public double? LossPercent => FailCount + SuccessCount == 0 ? null : 100.0 * FailCount / (FailCount + SuccessCount);
    /// <summary>Mean absolute difference between consecutive successful pings'
    /// latency - a steady link stays near 0, a flaky one swings up and down.</summary>
    public double? JitterMs => _jitterCount == 0 ? null : _jitterSum / _jitterCount;

    private int _consecutiveFails;
    private double _sumMs;
    private double? _lastMsForJitter;
    private double _jitterSum;
    private int _jitterCount;

    public bool IsOnline { get; private set; }
    public bool WentOffline { get; private set; }
    /// <summary>TickCount64 of the moment the device flipped online → offline
    /// (null while online or never seen). Drives the fast-recheck burst.</summary>
    public long? OfflineSince { get; private set; }

    /// <summary>Sets the displayed ping count to <paramref name="credit"/> without
    /// touching lifetime stats (min/max/avg/success/fail) - call right before
    /// assigning a fresh TargetPings so a new counted phase (discovery ->
    /// analysis, or a reconnect resuming analysis) starts its "N/target"
    /// display at credit/target instead of carrying over pings from the
    /// previous phase. <paramref name="credit"/> is 1 when the ping that just
    /// triggered this phase (the successful discovery attempt, or the probe
    /// that proved a reconnect) counts as the first of the new phase's own
    /// target sequence - the analysis loop that follows only supplies the
    /// remaining target-1, so the real ping count sent per device stays
    /// exactly PingCount, not PingCount+1.</summary>
    public void ResetPingCount(int credit = 0)
    {
        lock (this) CurrentPings = credit;
    }

    /// <summary>Records one ping's result. <paramref name="countsTowardTarget"/> is
    /// false for housekeeping probes (offline recheck, offline-burst follow-up)
    /// that must update online/offline state and stats but are not part of the
    /// displayed "N/target" progress for the device's current counted phase -
    /// otherwise the count can exceed its own target.</summary>
    /// <summary>Marks the host as named by a list scan: shown and exported, but never reported online.</summary>
    public void MarkListed()
    {
        lock (this) { Seen = true; DiscoveryMethod = "list"; }
    }

    public void RecordPing(PingResult r, bool countsTowardTarget = true)
    {
        lock (this)   // recheck and analysis threads may touch the same device
        {
            if (countsTowardTarget) CurrentPings++;
            if (r.Success)
            {
                Seen = true;
                IsOnline = true;
                if (r.Ttl is > 0) Ttl = r.Ttl;
                WentOffline = false;
                OfflineSince = null;
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
                    if (_lastMsForJitter is { } prev) { _jitterSum += Math.Abs(ms - prev); _jitterCount++; }
                    _lastMsForJitter = ms;
                }
            }
            else
            {
                FailCount++;
                LastFailed = true;
                _consecutiveFails++;
                if (Seen && _consecutiveFails >= OfflineAfterFailures)
                {
                    if (IsOnline) OfflineSince = Environment.TickCount64;
                    IsOnline = false;
                    WentOffline = true;
                }
            }
        }
    }
}
