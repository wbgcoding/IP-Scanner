using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using IpScanner.Core;
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

public sealed class PingToolViewModel : ObservableObject
{
    private const int MaxSamples = 60;
    private const double DefaultGraphWidth = 500;
    private const double GraphHeight = 60;

    private RingBuffer<double?> _samples = new();
    private double _graphWidth = DefaultGraphWidth;
    private CancellationTokenSource? _cts;

    public string[] IntervalOptions { get; } = { "0.5", "1", "2", "5" };
    public int[] PayloadOptions { get; } = { 32, 64, 512, 1400 };

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

    /// <summary>Latest replies first, at most 60.</summary>
    public ObservableCollection<PingReplyRow> Replies { get; } = new();

    public int Sent { get; private set; }
    public int Received { get; private set; }
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
        LossText = NoLoss; StatsText = ""; Status = "";
        Raise(nameof(Sent)); Raise(nameof(Received)); Raise(nameof(LossText)); Raise(nameof(StatsText));
        Replies.Clear();
        _samples = new RingBuffer<double?>();
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
    internal void AddSample(double? rtt)
    {
        Sent++;
        if (rtt is not null) Received++;
        RecordSample(rtt);
        Replies.Insert(0, new PingReplyRow(DateTime.Now.ToString("T", CultureInfo.CurrentCulture),
            rtt is { } v ? NumberFormat.Ms(v) : Loc.PingTimedOut, rtt is null));
        while (Replies.Count > MaxSamples) Replies.RemoveAt(Replies.Count - 1);
        Raise(nameof(Sent)); Raise(nameof(Received));
        LossText = FormatLoss(); Raise(nameof(LossText));
        StatsText = FormatStats(); Raise(nameof(StatsText));
    }

    private void RecordSample(double? rtt)
    {
        _samples.Add(rtt, MaxSamples);
        BuildGraph();
        if (rtt is not { } v) return;
        _last = v;
        if (v < _min) _min = v;
        if (v > _max) _max = v;
        _sum += v;
    }

    /// <summary>Shown for the loss before the first probe was sent.</summary>
    internal const string NoLoss = "\u2014";

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

    private string FormatStats() => FormatStats(Received, _min, _sum, _max, _last);

    private void BuildGraph()
    {
        var tick = GraphSeries.NiceTickStep(_samples.Count * ParseIntervalOrDefault());
        var r = GraphSeries.Compute(_samples, _graphWidth, GraphHeight, tick, ParseIntervalOrDefault());
        GraphPoints = r.Points; GraphMax = r.MaxText; GraphMin = r.MinText; GraphTicks = r.Ticks;
        Raise(nameof(GraphPoints)); Raise(nameof(GraphMax)); Raise(nameof(GraphMin)); Raise(nameof(GraphTicks));
    }

    private double ParseIntervalOrDefault()
        => double.TryParse(Interval, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0;
}

public sealed class RouteToolViewModel : ObservableObject
{
    private const int MaxHops = 30;
    private const int ProbesPerHop = 3;
    private const int HopTimeoutMs = 2000;

    private CancellationTokenSource? _cts;

    public sealed class Hop : ObservableObject
    {
        public int Number { get; init; }
        private string? _address;
        public string? Address { get => _address; set { if (_address != value) { _address = value; Raise(nameof(Address)); Raise(nameof(DisplayLine)); } } }
        private string? _name;
        public string? Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(DisplayLine)); } } }
        public ObservableCollection<string> Times { get; } = new();
        public Hop() => Times.CollectionChanged += (_, _) =>
        {
            Raise(nameof(DisplayLine));
            Raise(nameof(Time1)); Raise(nameof(Time2)); Raise(nameof(Time3));
        };

        /// <summary>One value per probe, each in its own table column; empty until that probe returned.</summary>
        public string Time1 => TimeAt(0);
        public string Time2 => TimeAt(1);
        public string Time3 => TimeAt(2);
        private string TimeAt(int i) => i < Times.Count ? Times[i] : "";
        public string DisplayLine => $"{Number,2}  {Address ?? "*",-16}{(Name is { Length: > 0 } n ? "  " + n : "")}  {string.Join(" ", Times)}";
    }

    public ObservableCollection<Hop> Hops { get; } = new();

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

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
            var targetAddress = await ResolveAsync(target, ct);
            if (targetAddress is null) { Status = Loc.DnsNotFound; return; }

            using var ping = new Ping();
            var options = new PingOptions { DontFragment = true };
            var payload = new byte[32];

            for (int ttl = 1; ttl <= MaxHops && !ct.IsCancellationRequested; ttl++)
            {
                options.Ttl = ttl;
                var hop = new Hop { Number = ttl };
                Hops.Add(hop);

                string? hopAddress = null;
                for (int probe = 0; probe < ProbesPerHop && !ct.IsCancellationRequested; probe++)
                {
                    PingReply reply;
                    try { reply = await ping.SendPingAsync(targetAddress, HopTimeoutMs, payload, options).WaitAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (PingException) { hop.Times.Add("*"); continue; }

                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        hopAddress ??= reply.Address?.ToString();
                        hop.Times.Add($"{reply.RoundtripTime} ms");
                    }
                    else hop.Times.Add("*");
                }

                hop.Address = hopAddress;
                if (hopAddress is { } addr) _ = ResolveNameAsync(hop, addr, ct);

                if (hopAddress == targetAddress.ToString()) break;
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

    private static async Task<IPAddress?> ResolveAsync(string target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target, out var direct)) return direct;
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(target, ct);
            return Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Tools, "Route lookup failed: " + ex.GetType().Name); return null; }
    }

    /// <summary>Reverse-resolves a hop's name without blocking the next hop's probes.</summary>
    private static async Task ResolveNameAsync(Hop hop, string address, CancellationToken ct)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(address, ct);
            if (!string.IsNullOrEmpty(entry.HostName)) hop.Name = entry.HostName;
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Tools, "Reverse name lookup failed: " + ex.GetType().Name); /* no reverse name - leave blank */ }
    }
}
