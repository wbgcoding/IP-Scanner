using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using IpScanner.Core;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;

namespace IpScanner.ViewModels.Tools;

/// <summary>Ping-and-traceroute tool page state: two independent panes -
/// a continuous ping with a live sparkline, and a hop-by-hop traceroute - each with
/// its own target and its own start/stop.</summary>
public sealed class PingRouteToolViewModel : ObservableObject
{
    public PingToolViewModel Ping { get; } = new();
    public RouteToolViewModel Route { get; } = new();

    public void Stop()
    {
        Ping.Stop();
        Route.Stop();
    }
}

public sealed record PingReplyRow(string Time, string Result, bool TimedOut);

/// <summary>One probe of the session; <see cref="Ms"/> is null when no reply came back.</summary>
internal readonly record struct PingSample(DateTime Time, double? Ms);

/// <summary>A choice of the zoom box: how much of the session the graph shows (0 seconds = all of it).</summary>
public sealed record PingZoom(string Label, int Seconds);

public sealed class PingToolViewModel : ObservableObject
{
    private const int MaxReplyRows = 60;
    /// <summary>Samples kept for the graph and the CSV: a full day at one probe per second.</summary>
    internal const int MaxSessionSamples = 86_400;
    /// <summary>A longer window is thinned to this many points so the graph stays cheap to draw.</summary>
    private const int MaxGraphPoints = 1000;
    private const double DefaultGraphWidth = 500;
    private const double GraphHeight = 60;

    private RingBuffer<PingSample> _session = new();
    private int _outages;
    private bool _lastLost;
    private double _graphWidth = DefaultGraphWidth;
    private CancellationTokenSource? _cts;

    public string[] IntervalOptions { get; } = { "0.5", "1", "2", "5" };
    public int[] PayloadOptions { get; } = { 32, 64, 512, 1400 };

    public PingZoom[] ZoomOptions { get; }

    public PingToolViewModel()
    {
        ZoomOptions = new PingZoom[]
        {
            new(Loc.PingZoom1Min, 60), new(Loc.PingZoom5Min, 300), new(Loc.PingZoom1Hour, 3600), new(Loc.PingZoomAll, 0),
        };
        _zoom = ZoomOptions[0];
    }

    private PingZoom _zoom;
    public PingZoom Zoom
    {
        get => _zoom;
        set { if (value is not null && _zoom != value) { _zoom = value; Raise(nameof(Zoom)); BuildGraph(); } }
    }

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

    private string _interval = "1";
    public string Interval { get => _interval; set { if (_interval != value) { _interval = value; Raise(nameof(Interval)); } } }

    private int _payloadSize = 32;
    public int PayloadSize { get => _payloadSize; set { if (_payloadSize != value) { _payloadSize = value; Raise(nameof(PayloadSize)); } } }

    private string _timeoutMs = "1000";
    public string TimeoutMs { get => _timeoutMs; set { if (_timeoutMs != value) { _timeoutMs = value; Raise(nameof(TimeoutMs)); } } }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { if (_isRunning != value) { _isRunning = value; Raise(nameof(IsRunning)); Raise(nameof(CanStart)); } } }

    private string _status = "";
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }

    public bool CanStart => !IsRunning && Target.Trim().Length > 0;

    /// <summary>Latest replies first, at most 60 (the whole session is in the graph and the CSV).</summary>
    public ObservableCollection<PingReplyRow> Replies { get; } = new();

    public int Sent { get; private set; }
    public int Received { get; private set; }
    /// <summary>Runs of lost probes: a miss right after a reply, or a lost first probe, starts one.</summary>
    public int Outages => _outages;
    public bool CanSave => !_isSaving && _session.Count > 0;

    private bool _isSaving;
    private void SetSaving(bool value) { _isSaving = value; Raise(nameof(CanSave)); }
    public string LossText { get; private set; } = NoLoss;
    public string StatsText { get; private set; } = "";

    public PointCollection GraphPoints { get; private set; } = new();
    public string GraphMax { get; private set; } = "";
    public string GraphMin { get; private set; } = "";
    public IReadOnlyList<GraphSeries.Tick> GraphTicks { get; private set; } = Array.Empty<GraphSeries.Tick>();

    private double _min = double.MaxValue, _max = double.MinValue, _sum, _last;

    public void SetGraphWidth(double width)
    {
        if (width <= 1 || Math.Abs(width - _graphWidth) < 1) return;
        _graphWidth = width;
        BuildGraph();
    }

    internal static bool TryParseTimeout(string text, out int ms)
    {
        ms = 0;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms is >= 100 and <= 10000;
    }

    public void Stop() => _cts?.Cancel();

    public async Task StartAsync()
    {
        if (!CanStart) return;
        if (!TryParseTimeout(TimeoutMs, out var timeoutMs)) { Status = Loc.PingInvalidTimeout; return; }
        var intervalSeconds = double.Parse(Interval, CultureInfo.InvariantCulture);

        Sent = 0; Received = 0; _min = double.MaxValue; _max = double.MinValue; _sum = 0; _last = 0;
        _outages = 0; _lastLost = false; _session = new RingBuffer<PingSample>();
        LossText = NoLoss; StatsText = ""; Status = "";
        Raise(nameof(Sent)); Raise(nameof(Received)); Raise(nameof(LossText)); Raise(nameof(StatsText));
        Raise(nameof(Outages)); Raise(nameof(CanSave));
        Replies.Clear();
        BuildGraph();

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var target = Target.Trim();
        var payload = new byte[PayloadSize];
        using var ping = new Ping();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double? rtt = null;
                try
                {
                    var reply = await ping.SendPingAsync(target, timeoutMs, payload).WaitAsync(ct);
                    if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
                }
                catch (PingException) { /* unresolved host, offline adapter - counts as a miss */ }

                AddSample(rtt);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ContinueWith(_ => { }, TaskScheduler.Default);
                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) { /* Stop() was called */ }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Counts one probe (null = no reply), then refreshes the statistics, the graph and the reply list.</summary>
    internal void AddSample(double? rtt, DateTime? at = null)
    {
        var time = at ?? DateTime.Now;
        Sent++;
        if (rtt is not null) Received++;
        if (rtt is null && !_lastLost) _outages++;
        _lastLost = rtt is null;
        RecordSample(rtt, time);
        Replies.Insert(0, new PingReplyRow(time.ToString("T", CultureInfo.CurrentCulture),
            rtt is { } v ? NumberFormat.Ms(v) : Loc.PingTimedOut, rtt is null));
        while (Replies.Count > MaxReplyRows) Replies.RemoveAt(Replies.Count - 1);
        Raise(nameof(Sent)); Raise(nameof(Received)); Raise(nameof(Outages)); Raise(nameof(CanSave));
        LossText = FormatLoss(); Raise(nameof(LossText));
        StatsText = FormatStatsLine(); Raise(nameof(StatsText));
    }

    private void RecordSample(double? rtt, DateTime time)
    {
        _session.Add(new PingSample(time, rtt), MaxSessionSamples);
        BuildGraph();
        if (rtt is not { } v) return;
        _last = v;
        if (v < _min) _min = v;
        if (v > _max) _max = v;
        _sum += v;
    }

    /// <summary>Shown for the loss before the first probe was sent.</summary>
    internal const string NoLoss = "\u2013";

    internal static string FormatLoss(int sent, int received)
        => sent == 0 ? NoLoss : $"{(sent - received) * 100.0 / sent:0.#}%";

    private string FormatLoss() => FormatLoss(Sent, Received);

    // Same one-decimal number as the scan table (NumberFormat.Ms); the format text carries the unit.
    internal static string FormatStats(int received, double min, double sum, double max, double last)
        => received == 0 ? "" : Loc.PingStatsMsg(
            NumberFormat.MsValue(min),
            NumberFormat.MsValue(sum / received),
            NumberFormat.MsValue(max),
            NumberFormat.MsValue(last));

    /// <summary>The statistics followed by the outage count; the count shows from the first probe on.</summary>
    internal static string FormatStatsLine(int sent, int received, double min, double sum, double max, double last, int outages)
    {
        var stats = FormatStats(received, min, sum, max, last);
        if (sent == 0) return stats;
        var lost = string.Format(Loc.PingOutagesFmt, outages);
        return stats.Length == 0 ? lost : stats + "  |  " + lost;
    }

    private string FormatStatsLine() => FormatStatsLine(Sent, Received, _min, _sum, _max, _last, _outages);

    private void BuildGraph()
    {
        int first = ZoomStart();
        int count = _session.Count - first;
        double secondsPerSample = count > 1
            ? Math.Max(0.001, (_session[_session.Count - 1].Time - _session[first].Time).TotalSeconds / (count - 1))
            : ParseIntervalOrDefault();
        var series = Thin(_session, first, out int perPoint);
        secondsPerSample *= perPoint;
        var tick = TickStep(series.Count * secondsPerSample);
        var r = GraphSeries.Compute(series, _graphWidth, GraphHeight, tick, secondsPerSample);
        GraphPoints = r.Points; GraphMax = r.MaxText; GraphMin = r.MinText; GraphTicks = r.Ticks;
        Raise(nameof(GraphPoints)); Raise(nameof(GraphMax)); Raise(nameof(GraphMin)); Raise(nameof(GraphTicks));
    }

    /// <summary>Index of the first session sample the zoom box shows: those within its span of the newest one, or all.</summary>
    private int ZoomStart()
    {
        if (_session.Count == 0 || Zoom.Seconds <= 0) return 0;
        var cutoff = _session[_session.Count - 1].Time.AddSeconds(-Zoom.Seconds);
        int first = _session.Count;
        while (first > 0 && _session[first - 1].Time >= cutoff) first--;
        return first;
    }

    /// <summary>Thins the samples from <paramref name="first"/> on to at most <see cref="MaxGraphPoints"/> values; each keeps
    /// the slowest reply of its group so a spike never disappears, and is null only when the whole group was lost.</summary>
    internal static List<double?> Thin(IReadOnlyList<PingSample> all, int first, out int perPoint)
    {
        int count = all.Count - first;
        perPoint = Math.Max(1, (count + MaxGraphPoints - 1) / MaxGraphPoints);
        var result = new List<double?>((count + perPoint - 1) / perPoint);
        for (int i = first; i < all.Count; i += perPoint)
        {
            double? worst = null;
            for (int j = i; j < Math.Min(i + perPoint, all.Count); j++)
                if (all[j].Ms is { } v && (worst is null || v > worst)) worst = v;
            result.Add(worst);
        }
        return result;
    }

    private static readonly int[] TickSteps =
        { 2, 5, 15, 30, 60, 300, 900, 1800, 3600, 7200, 14400, 21600, 43200, 86400 };

    /// <summary>A tick step in seconds that leaves about eight labels over the visible span.</summary>
    internal static int TickStep(double spanSeconds)
    {
        foreach (var step in TickSteps) if (spanSeconds / step <= 8) return step;
        return TickSteps[^1];
    }

    /// <summary>The session as CSV: one row per probe with its time, the round trip in ms (empty when lost) and the status.</summary>
    internal string BuildCsv(char delimiter)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("time").Append(delimiter).Append("ms").Append(delimiter).Append("status").Append("\r\n");
        foreach (var smp in _session)
        {
            sb.Append(smp.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(delimiter)
              .Append(smp.Ms is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "").Append(delimiter)
              .Append(smp.Ms is null ? "timeout" : "ok").Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Writes the session CSV (list separator of the user's culture, UTF-8 with BOM) to <paramref name="path"/>.</summary>
    public async Task<bool> SaveAsync(string path)
    {
        if (!CanSave) return false;
        var csv = BuildCsv(CsvExporter.ListSeparator());
        SetSaving(true);
        try
        {
            await Task.Run(() => CsvExporter.WriteTo(path, csv));
            Status = string.Format(Loc.ScanToolSavedFmt, path);
            return true;
        }
        catch (Exception ex)
        {
            Status = string.Format(Loc.ScanToolSaveFailedFmt, ex.Message);
            return false;
        }
        finally { SetSaving(false); }
    }

    private double ParseIntervalOrDefault()
        => double.TryParse(Interval, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0;
}

/// <summary>What one probe of a hop brought back; <see cref="Answered"/> is false for a timeout or an error.</summary>
internal readonly record struct HopProbe(bool Answered, string? Address, double? Ms)
{
    public static readonly HopProbe Lost = new(false, null, null);
}

/// <summary>The network side of the route pane, so the view model can be driven by a fake in tests.</summary>
internal interface IHopProber
{
    /// <summary>The IPv4 address of the target, or null when it does not resolve.</summary>
    Task<IPAddress?> ResolveAsync(string target, CancellationToken ct);

    /// <summary>One echo to <paramref name="target"/> with the given TTL; a router that drops it reports itself.</summary>
    Task<HopProbe> ProbeAsync(IPAddress target, int ttl, int timeoutMs, CancellationToken ct);

    /// <summary>The reverse name of an address, or null when it has none.</summary>
    Task<string?> ReverseNameAsync(string address, CancellationToken ct);
}

/// <summary>The real prober: ICMP echoes with a limited TTL and the system resolver.</summary>
internal sealed class SystemHopProber : IHopProber
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<IPAddress?> ResolveAsync(string target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target, out var direct)) return direct;
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(target, ct);
            return Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Tools, "Route lookup failed: " + ex.GetType().Name); return null; }
    }

    public async Task<HopProbe> ProbeAsync(IPAddress target, int ttl, int timeoutMs, CancellationToken ct)
    {
        // One Ping object per probe: the probes of a round run at the same time and an instance takes one at a time.
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: true);
        try
        {
            var reply = await ping.SendPingAsync(target, timeoutMs, Payload, options).WaitAsync(ct);
            return reply.Status is IPStatus.TtlExpired or IPStatus.Success
                ? new HopProbe(true, reply.Address?.ToString(), reply.RoundtripTime)
                : HopProbe.Lost;
        }
        catch (PingException) { return HopProbe.Lost; }
    }

    public async Task<string?> ReverseNameAsync(string address, CancellationToken ct)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(address, ct);
            return string.IsNullOrEmpty(entry.HostName) ? null : entry.HostName;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Tools, "Reverse name lookup failed: " + ex.GetType().Name); return null; }
    }
}

public sealed class RouteToolViewModel : ObservableObject
{
    private const int MaxHops = 30;
    private const int ProbesPerHop = 3;
    private const int HopTimeoutMs = 2000;

    private readonly IHopProber _prober;
    private readonly TimeSpan _roundInterval;
    private CancellationTokenSource? _cts;

    public RouteToolViewModel() : this(new SystemHopProber(), TimeSpan.FromSeconds(1)) { }

    internal RouteToolViewModel(IHopProber prober, TimeSpan roundInterval)
    {
        _prober = prober;
        _roundInterval = roundInterval;
    }

    /// <summary>One station of the route with the running statistics of every probe sent to it.</summary>
    public sealed class Hop : ObservableObject
    {
        public int Number { get; init; }
        private string? _address;
        public string? Address { get => _address; set { if (_address != value) { _address = value; Raise(nameof(Address)); Raise(nameof(DisplayLine)); } } }
        private string? _name;
        public string? Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(DisplayLine)); } } }

        private double _min = double.MaxValue, _max = double.MinValue, _sum, _last;
        public int Sent { get; private set; }
        public int Received { get; private set; }

        /// <summary>True once the name lookup of this hop was started (it runs once per address).</summary>
        internal bool NameRequested { get; set; }

        public string LossText => PingToolViewModel.FormatLoss(Sent, Received);
        public string LastText => NumberFormat.Ms(Received > 0 ? _last : null);
        public string AvgText => NumberFormat.Ms(Received > 0 ? _sum / Received : null);
        public string BestText => NumberFormat.Ms(Received > 0 ? _min : null);
        public string WorstText => NumberFormat.Ms(Received > 0 ? _max : null);

        /// <summary>Counts one probe (<paramref name="ms"/> null = no answer); an answering router's address is kept
        /// the first time it is seen.</summary>
        public void Record(double? ms, string? address = null)
        {
            Sent++;
            if (address is not null && _address is null) _address = address;
            if (ms is { } v)
            {
                Received++;
                _last = v; _sum += v;
                if (v < _min) _min = v;
                if (v > _max) _max = v;
            }
            Raise(nameof(Address)); Raise(nameof(Sent)); Raise(nameof(LossText)); Raise(nameof(LastText));
            Raise(nameof(AvgText)); Raise(nameof(BestText)); Raise(nameof(WorstText)); Raise(nameof(DisplayLine));
        }

        public string DisplayLine =>
            $"{Number,2}  {Address ?? "*",-16}{(Name is { Length: > 0 } n ? "  " + n : "")}  {LossText,5}  {LastText,9}  {AvgText,9}  {BestText,9}  {WorstText,9}  {Sent,4}";
    }

    public ObservableCollection<Hop> Hops { get; } = new();

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

    private bool _continuous;
    /// <summary>After the first pass, probe every hop again each second until stopped. Clearing it ends a run in progress.</summary>
    public bool Continuous { get => _continuous; set { if (_continuous != value) { _continuous = value; Raise(nameof(Continuous)); } } }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { if (_isRunning != value) { _isRunning = value; Raise(nameof(IsRunning)); Raise(nameof(CanStart)); } } }

    private string _status = "";
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }

    public bool CanStart => !IsRunning && Target.Trim().Length > 0;

    public void Stop() => _cts?.Cancel();

    public async Task StartAsync()
    {
        if (!CanStart) return;

        Hops.Clear();
        Status = "";
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var target = Target.Trim();

        try
        {
            var targetAddress = await _prober.ResolveAsync(target, ct);
            if (targetAddress is null) { Status = Loc.DnsNotFound; return; }

            await FirstPassAsync(targetAddress, ct);
            while (Continuous && !ct.IsCancellationRequested && Hops.Count > 0)
                await ReprobeAsync(targetAddress, ct);
        }
        catch (OperationCanceledException) { /* Stop() was called */ }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The trace itself: three probes per TTL, until the target answers or the hop limit is reached.</summary>
    private async Task FirstPassAsync(IPAddress targetAddress, CancellationToken ct)
    {
        for (int ttl = 1; ttl <= MaxHops && !ct.IsCancellationRequested; ttl++)
        {
            var hop = new Hop { Number = ttl };
            Hops.Add(hop);

            for (int probe = 0; probe < ProbesPerHop && !ct.IsCancellationRequested; probe++)
            {
                var result = await _prober.ProbeAsync(targetAddress, ttl, HopTimeoutMs, ct);
                hop.Record(result.Answered ? result.Ms : null, result.Answered ? result.Address : null);
                StartNameLookup(hop, ct);
            }

            if (hop.Address == targetAddress.ToString()) break;
        }
    }

    /// <summary>One more round: every known hop gets a probe at the same time, then the round waits out the rest of its second.</summary>
    private async Task ReprobeAsync(IPAddress targetAddress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var hops = Hops.ToArray();
        var results = await Task.WhenAll(hops.Select(h => _prober.ProbeAsync(targetAddress, h.Number, HopTimeoutMs, ct)));
        for (int i = 0; i < hops.Length; i++)
        {
            hops[i].Record(results[i].Answered ? results[i].Ms : null, results[i].Answered ? results[i].Address : null);
            StartNameLookup(hops[i], ct);
        }

        var wait = _roundInterval - started.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    /// <summary>Reverse-resolves a hop's name without blocking the probes; started once, when the address is first known.</summary>
    private void StartNameLookup(Hop hop, CancellationToken ct)
    {
        if (hop.NameRequested || hop.Address is not { } address) return;
        hop.NameRequested = true;
        _ = LookupNameAsync(hop, address, ct);
    }

    private async Task LookupNameAsync(Hop hop, string address, CancellationToken ct)
    {
        try
        {
            if (await _prober.ReverseNameAsync(address, ct) is { } name) hop.Name = name;
        }
        catch (OperationCanceledException) { /* Stop() was called */ }
    }
}
