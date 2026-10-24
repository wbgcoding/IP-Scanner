using System.Collections.Concurrent;
using System.Globalization;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Core.Cli;

/// <summary>What a tool page asks the scanner to do: the same pipeline as the command line, with live callbacks.</summary>
public sealed class ToolScanRequest
{
    /// <summary>The expanded target addresses.</summary>
    public required IReadOnlyList<string> Targets { get; init; }

    public string TargetsText { get; init; } = "";
    public string Command { get; init; } = "";
    public required ScanPlan Plan { get; init; }

    /// <summary>The ports to probe on every live host; ignored when the plan is ping only.</summary>
    public PortSpec? Ports { get; init; }

    public ScanOptions Options { get; init; } = new();

    /// <summary>Null skips the service checks.</summary>
    public CheckSelection? Checks { get; init; }
}

/// <summary>Live progress of a tool scan. Callbacks arrive on worker threads.</summary>
public sealed class ToolScanEvents
{
    public Action<Device>? HostFound { get; init; }
    public Action<string, PortResult>? PortFound { get; init; }
    public Action<string, IReadOnlyList<CheckResult>>? ChecksDone { get; init; }
    public Action<string, OsEstimate?>? OsDone { get; init; }
    public Action<ToolScanStage>? Stage { get; init; }
    public Action<string>? Warning { get; init; }
}

public enum ToolScanStage { Discovery, Ports, Checks, Os }

public sealed record ToolScanOutcome(ScanReport Report, bool Cancelled, IReadOnlyList<string> Warnings);

/// <summary>Runs one scan for a GUI tool page. It mirrors the command line pipeline (discovery, ports,
/// checks, OS estimate) so both produce the same report, but reports every result as it arrives.</summary>
public static class ToolScan
{
    private const int HostParallelism = 4;
    private static int _busy;

    /// <summary>True while a scan that changes the process-wide scanner settings is running.</summary>
    public static bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Takes the process-wide slot for a real scan. Null when another scan holds it.</summary>
    public static IDisposable? TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0 ? new Slot() : null;

    private sealed class Slot : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Volatile.Write(ref _busy, 0);
        }
    }

    public static async Task<ToolScanOutcome> RunAsync(ToolScanRequest req, CliServices s, ToolScanEvents events, CancellationToken ct)
    {
        var warnings = new ConcurrentQueue<string>();
        void Warn(string text)
        {
            warnings.Enqueue(text);
            events.Warning?.Invoke(text);
        }

        using var settings = Settings.Apply(req.Plan, s);
        var started = s.Now();
        var net = s.Network(null);
        var cfg = new ScanConfig
        {
            PingCount = 1,
            InitPingCount = 1,
            OfflineRecheckSeconds = 0,
            KnownDevicesDb = false,
            ScanHistory = false,
            EnableInternetPing = false,
            PinnedIps = [],
            DiscoveryModes = req.Plan.DiscoveryModeKeys,
        };

        using var engine = new ScanEngine(s.Ping, s.Enrichers);
        if (s.NameResolver is not null) engine.NameResolver = s.NameResolver;
        var announced = new ConcurrentDictionary<string, byte>();
        engine.DeviceUpdated += d =>
        {
            if (d.IsOnline && announced.TryAdd(d.Ip, 0)) events.HostFound?.Invoke(d);
        };

        var portResults = new ConcurrentDictionary<string, IReadOnlyList<PortResult>>();
        var checkResults = new ConcurrentDictionary<string, IReadOnlyList<CheckResult>>();
        bool cancelled = false;
        try
        {
            events.Stage?.Invoke(ToolScanStage.Discovery);
            await engine.ScanAsync(req.Targets, cfg, ct).ConfigureAwait(false);
            if (!req.Plan.PingOnly && req.Ports is { } ports)
            {
                var live = engine.Devices.Where(d => d.IsOnline).OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer).ToList();
                events.Stage?.Invoke(ToolScanStage.Ports);
                await ScanPortsAsync(s, live, ports, req.Options, portResults, events, Warn, ct).ConfigureAwait(false);
                if (req.Checks is { } selection)
                {
                    events.Stage?.Invoke(ToolScanStage.Checks);
                    await RunChecksAsync(s, portResults, selection, net, checkResults, events, Warn, ct).ConfigureAwait(false);
                }
                if (req.Plan.OsDetection)
                {
                    events.Stage?.Invoke(ToolScanStage.Os);
                    await RunOsAsync(s, live, portResults, events, Warn, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { Warn(ex.Message); }
        if (ct.IsCancellationRequested) cancelled = true;

        var devices = engine.Devices.ToList();
        var findings = ConflictDetector.Detect(devices.Where(d => d.IsOnline).ToList(), new Dictionary<string, string>());
        var report = ReportBuilder.Build(devices, net, started.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture),
            started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ServicesOf(portResults), new ReportExtras
            {
                Command = req.Command,
                Targets = req.TargetsText,
                Partial = cancelled,
                Technique = req.Options.Tcp ? req.Options.TcpType : null,
                PortResults = portResults.ToDictionary(p => p.Key, p => p.Value),
                Checks = checkResults.ToDictionary(p => p.Key, p => p.Value),
                Findings = findings,
            });
        return new ToolScanOutcome(report, cancelled, warnings.ToList());
    }

    private static async Task ScanPortsAsync(CliServices s, List<Device> live, PortSpec ports, ScanOptions options,
        ConcurrentDictionary<string, IReadOnlyList<PortResult>> into, ToolScanEvents events, Action<string> warn, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(HostParallelism);
        var tasks = live.Select(async d =>
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var seen = new ConcurrentQueue<PortResult>();
            try
            {
                var list = await s.PortRunner(d.Ip, ports, options, null, ct, r =>
                {
                    var one = r with { Ip = d.Ip };
                    seen.Enqueue(one);
                    events.PortFound?.Invoke(d.Ip, one);
                }).ConfigureAwait(false);
                into[d.Ip] = Finish(d.Ip, list);
            }
            catch (OperationCanceledException) { into[d.Ip] = Finish(d.Ip, seen); }
            catch (Exception ex)
            {
                warn($"{d.Ip}: {ex.Message}");
                into[d.Ip] = Finish(d.Ip, seen);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static IReadOnlyList<PortResult> Finish(string ip, IEnumerable<PortResult> list) =>
        list.Select(r => r with { Ip = ip }).OrderBy(r => r.Protocol).ThenBy(r => r.Port).ToList();

    private static async Task RunChecksAsync(CliServices s, ConcurrentDictionary<string, IReadOnlyList<PortResult>> found,
        CheckSelection selection, NetworkInfo net, ConcurrentDictionary<string, IReadOnlyList<CheckResult>> into,
        ToolScanEvents events, Action<string> warn, CancellationToken ct)
    {
        foreach (var (ip, results) in found.OrderBy(p => p.Key, Net.IpAddressKey.TextComparer))
        {
            ct.ThrowIfCancellationRequested();
            var services = results.Where(r => r.IsOpen)
                .Select(r => r.Service ?? new NetService(r.ServiceName, r.Port, r.Protocol)).Select(v => v with { Ip = ip }).ToList();
            if (selection.Intrusive && InLocalSubnet(ip, net))
                services.Add(new NetService("DHCP", 67, NetProtocol.Udp) { Ip = ip });
            if (services.Count == 0) continue;
            try
            {
                var list = await s.CheckRunner(ip, services, selection, ct).ConfigureAwait(false);
                into[ip] = list;
                events.ChecksDone?.Invoke(ip, list);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warn($"{ip}: {ex.Message}"); }
        }
    }

    private static bool InLocalSubnet(string ip, NetworkInfo net) => LocalNetwork.Contains(ip, net);

    private static async Task RunOsAsync(CliServices s, List<Device> live, ConcurrentDictionary<string, IReadOnlyList<PortResult>> found,
        ToolScanEvents events, Action<string> warn, CancellationToken ct)
    {
        if (s.OsRunner is null) return;
        foreach (var d in live)
        {
            ct.ThrowIfCancellationRequested();
            var open = found.TryGetValue(d.Ip, out var list)
                ? list.Where(r => r.IsOpen && r.Protocol == NetProtocol.Tcp).Select(r => r.Port).ToList()
                : [];
            try
            {
                d.OsFingerprint = await s.OsRunner(d.Ip, open, ct).ConfigureAwait(false);
                events.OsDone?.Invoke(d.Ip, d.OsFingerprint);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warn($"{d.Ip}: {ex.Message}"); }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<NetService>> ServicesOf(
        ConcurrentDictionary<string, IReadOnlyList<PortResult>> found) =>
        found.ToDictionary(p => p.Key,
            p => (IReadOnlyList<NetService>)p.Value.Where(r => r.IsOpen && r.Service is not null)
                .Select(r => r.Service! with { Ip = p.Key }).ToList());

    /// <summary>Process-wide scanner settings the run changes, put back on dispose.</summary>
    private sealed class Settings : IDisposable
    {
        private readonly ScanTiming? _timing;
        private readonly bool _detection;
        private readonly bool _active;

        private Settings() { }

        private Settings(ScanPlan plan)
        {
            _active = true;
            _timing = PortScanner.Timing;
            _detection = PortScanner.ServiceDetection;
            PortScanner.ApplyTiming(ScanTiming.For(plan.Timing));
            PortScanner.ServiceDetection = plan.ServiceDetection;
        }

        public static Settings Apply(ScanPlan plan, CliServices s) => s.ApplyProcessSettings ? new Settings(plan) : new Settings();

        public void Dispose()
        {
            if (!_active) return;
            PortScanner.ApplyTiming(_timing!);
            PortScanner.ServiceDetection = _detection;
        }
    }
}
