namespace IpScanner.Core.Scanner;

/// <summary>Every pacing knob of a port scan in one value. The six templates mirror the familiar
/// paranoid-to-insane scale; template 3 reproduces the fixed constants the scanner always used. Any
/// field can be overridden on its own with a <c>with</c> expression.</summary>
/// <param name="Template">0 (paranoid) to 5 (insane); informational once fields are overridden.</param>
/// <param name="Parallelism">Concurrent probes.</param>
/// <param name="InitialRttMs">TCP connect timeout, and the starting point of the adaptive estimate.</param>
/// <param name="MinRttMs">Lower clamp of the adaptive timeout.</param>
/// <param name="MaxRttMs">Upper clamp of the adaptive timeout.</param>
/// <param name="BannerTimeoutMs">Wait for a banner once a port answered.</param>
/// <param name="UdpTimeoutMs">Wait for a UDP reply.</param>
/// <param name="Retries">Extra attempts for a probe that got no answer at all.</param>
/// <param name="ScanDelayMs">Minimum gap between two probe starts, 0 for none.</param>
/// <param name="HostTimeoutMs">A host still unfinished after this long keeps the results found so far; 0 for none.</param>
/// <param name="MinRate">Probes per second the scan should at least reach; shortens the scan delay to fit, 0 for none.</param>
/// <param name="MaxRate">Hard cap on probe starts per second, 0 for none.</param>
/// <param name="AdaptiveRtt">Shrink and grow the timeout from the measured round trips.</param>
internal sealed record ScanTiming(
    int Template, int Parallelism, int InitialRttMs, int MinRttMs, int MaxRttMs,
    int BannerTimeoutMs, int UdpTimeoutMs, int Retries, int ScanDelayMs, int HostTimeoutMs,
    int MinRate, int MaxRate, bool AdaptiveRtt)
{
    public const int Paranoid = 0, Sneaky = 1, Polite = 2, Normal = 3, Aggressive = 4, Insane = 5;
    public const int TemplateCount = 6;

    // Index = template.
    private static readonly int[] ParallelismOf = { 1, 1, 5, 40, 100, 300 };
    private static readonly int[] InitialRttOf = { 5000, 3000, 1500, 500, 400, 250 };
    private static readonly int[] BannerOf = { 5000, 3000, 1500, 600, 500, 300 };
    private static readonly int[] UdpOf = { 5000, 4000, 2000, 800, 600, 400 };
    private static readonly int[] RetriesOf = { 2, 2, 1, 0, 0, 0 };
    private static readonly int[] DelayOf = { 5000, 1000, 100, 0, 0, 0 };
    private static readonly int[] HostTimeoutOf = { 0, 0, 0, 0, 900_000, 300_000 };
    private static readonly int[] MaxRateOf = { 0, 0, 0, 0, 1500, 5000 };

    public static ScanTiming For(int template)
    {
        int t = Math.Clamp(template, Paranoid, Insane);
        int initial = InitialRttOf[t];
        return new ScanTiming(t, ParallelismOf[t], initial, MinFor(initial), MaxFor(initial), BannerOf[t], UdpOf[t],
            RetriesOf[t], DelayOf[t], HostTimeoutOf[t], 0, MaxRateOf[t], t >= Aggressive);
    }

    /// <summary>The scan as it always ran before the templates existed.</summary>
    public static ScanTiming Normal3 => For(Normal);

    private static int MinFor(int initialRttMs) => Math.Max(20, initialRttMs / 5);

    private static int MaxFor(int initialRttMs) => Math.Min(30_000, initialRttMs * 4);

    /// <summary>The template with the individually configured values laid over it. The UDP wait follows
    /// the connect timeout, as the settings always did; a value of 0 or less keeps the template's own.</summary>
    public static ScanTiming FromSettings(int template, int parallelism, int connectMs, int bannerMs,
        int retries, int scanDelayMs, int maxRate)
    {
        var t = For(template);
        int initial = connectMs > 0 ? connectMs : t.InitialRttMs;
        return t with
        {
            Parallelism = parallelism > 0 ? parallelism : t.Parallelism,
            InitialRttMs = initial,
            MinRttMs = MinFor(initial),
            MaxRttMs = MaxFor(initial),
            BannerTimeoutMs = bannerMs > 0 ? bannerMs : t.BannerTimeoutMs,
            UdpTimeoutMs = initial,
            Retries = Math.Max(0, retries),
            ScanDelayMs = Math.Max(0, scanDelayMs),
            MaxRate = Math.Max(0, maxRate),
        };
    }

    /// <summary>The gap between probe starts after the minimum rate has been honored.</summary>
    public int EffectiveScanDelayMs
        => MinRate > 0 && ScanDelayMs > 0 ? Math.Min(ScanDelayMs, Math.Max(1, 1000 / MinRate)) : ScanDelayMs;
}
