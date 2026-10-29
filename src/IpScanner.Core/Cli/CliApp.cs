using System.Collections.Concurrent;
using System.Globalization;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Discovery;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Core.Cli;

/// <summary>The command line scanner. Both executables call this one runner, so the same arguments
/// always give the same output.</summary>
public static class CliApp
{
    private const int HostParallelism = 4;

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        CliServices? services = null, string program = "IP-Scanner") =>
        RunAsync(args, stdout, stderr, ct, services, program).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
        CancellationToken ct, CliServices? services = null, string program = "IP-Scanner")
    {
        var parsed = CliParser.Parse(args);
        var o = parsed.Options;
        if (parsed.Error is not null) return Usage(stderr, parsed.Error);
        if (o.Help) { stdout.Write(CliHelp.Text(program)); return CliExit.Ok; }
        if (o.Version) { stdout.WriteLine(CliHelp.Version()); return CliExit.Ok; }

        if (services is null)
        {
            // Real runs only: tests inject their own services and transport.
            RawTransportProvider.RegisterDriverTransport();
            services = CliServices.System();
        }
        var run = new Session(o, services, stdout, stderr);
        Loc.SetLanguage("en");
        try
        {
            return await run.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine("Error: " + ex.Message);
            return CliExit.ScanError;
        }
    }

    private static int Usage(TextWriter stderr, string message)
    {
        stderr.WriteLine("Error: " + message);
        stderr.WriteLine("Try --help for the list of options.");
        return CliExit.UsageError;
    }

    private sealed class Session(CliOptions o, CliServices s, TextWriter stdout, TextWriter stderr)
    {
        public async Task<int> ExecuteAsync(CancellationToken ct)
        {
            var spec = TargetSpec.Parse(string.Join(',', o.Targets), new TargetSpecOptions
            {
                Exclude = o.Exclude, IncludeFile = o.IncludeFile, ExcludeFile = o.ExcludeFile,
                Resolver = s.Resolver, ReadLines = s.ReadLines,
                HostFamily = o.Ipv6 ? IpFamily.V6 : IpFamily.V4,
            });
            if (spec.Errors.Count > 0)
                return Usage(stderr, string.Join(" ", spec.Errors.Select(e => $"Invalid target '{e.Token}': {e.Reason}.")));
            if (!spec.HasTargets) return Usage(stderr, "No target given. Name an address, a range or a CIDR block.");
            if (!o.Ipv6 && spec.HasIpv6Targets)
                return Usage(stderr, "An IPv6 target needs the option -6.");
            if (o.Ipv6 && spec.HasIpv4Targets)
                return Usage(stderr, "With -6 only IPv6 targets are scanned. Remove the IPv4 target or leave out -6.");
            if (spec.RequiresConfirmation && !o.AllowLarge)
                return Usage(stderr, $"The targets cover about {spec.EstimatedCount} addresses, more than {TargetSpec.HardCap}. Add --allow-large to scan them.");

            // Ports and scan type.
            PortSpec ports = PortSpec.Common();
            int probes = 0;
            bool tcp = o.TcpType is not null || !o.Udp;
            var type = o.TcpType ?? ScanType.Connect;
            if (o.Mode == CliMode.Scan)
            {
                ports = ScanPlan.FromOptions(o).ResolvePorts().Only(tcp, o.Udp);
                if (ports.IsEmpty) return Usage(stderr, "The port list has no port for the chosen protocols.");
                probes = ports.Tcp.Count + ports.Udp.Count;
                bool chosen = o.Ports is not null || o.TopPorts is not null;
                if (chosen && PortScanGate.NeedsConfirmation(o.TopPorts, probes) && !o.AllowFullScan)
                    return Usage(stderr, $"Scanning {probes} ports takes long and is noisy. Add --allow-full-scan to do it.");
                if (ScanTypes.IsAggressive(type) && !o.AllowAggressive)
                    return Usage(stderr, $"The scan type '{ScanTypes.Label(type)}' sends unusual packets. Add --allow-aggressive to use it.");
            }

            if (o.Ipv6 && o.Mode == CliMode.Scan && tcp && !Capabilities.SupportsIpv6(type))
                return Usage(stderr, $"The scan type '{ScanTypes.Label(type)}' does not support IPv6. Use a connect or UDP scan.");
            if (o.Ipv6 && o.Mode == CliMode.Scan && o.OsDetection)
                return Usage(stderr, "The operating system detection does not support IPv6.");

            var selection = o.Checks is null ? (CheckSelection?)null : CliChecks.Parse(o.Checks);
            if (selection is { Intrusive: true } && !o.AllowIntrusive)
                return Usage(stderr, "Intrusive checks can disturb the services they test. Add --allow-intrusive to run them.");

            if (o.Mode == CliMode.Scan && tcp && ScanTypes.RequiresDriver(type))
            {
                var (available, reason) = s.Availability(type);
                if (!available)
                {
                    stderr.WriteLine($"Error: the scan type '{ScanTypes.Label(type)}' is not available: {reason}.");
                    return CliExit.ScanError;
                }
            }

            if (!o.AcknowledgeRisk)
            {
                var reasons = CliWarning.AggressiveReasons(o, type, probes, selection is { Intrusive: true });
                if (reasons.Count > 0) stderr.WriteLine(CliWarning.Text(reasons));
            }

            string? source = null;
            if (o.Interface is not null)
            {
                var card = s.Cards().FirstOrDefault(c =>
                    string.Equals(c.Ip, o.Interface, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Name, o.Interface, StringComparison.OrdinalIgnoreCase));
                if (card is null) return Usage(stderr, $"No network adapter matches '{o.Interface}'.");
                source = card.Ip;
            }

            IEnumerable<string> targets;
            try { targets = spec.Expand(o.AllowLarge).ToList(); }
            catch (TargetCapExceededException ex) { return Usage(stderr, ex.Message); }
            foreach (var host in spec.UnresolvedHosts)
                stderr.WriteLine($"Warning: '{host}' did not resolve to an {(o.Ipv6 ? "IPv6" : "IPv4")} address.");

            foreach (var (_, path) in o.Outputs)
            {
                if (path == "-") continue;
                try { ReportFile.CheckWritable(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    stderr.WriteLine($"Error: cannot write '{path}': {ex.Message}");
                    return CliExit.ScanError;
                }
            }

            using var settings = ProcessSettings.Apply(o, s, source);
            using var debug = o.Debug ? DebugLog.Attach(stderr) : null;

            var started = s.Now();
            var net = s.Network(source);
            var cfg = BuildConfig(o);
            using var engine = new ScanEngine(s.Ping, s.Enrichers);
            if (s.NameResolver is not null) engine.NameResolver = s.NameResolver;

            var portResults = new ConcurrentDictionary<string, IReadOnlyList<PortResult>>();
            var checkResults = new ConcurrentDictionary<string, IReadOnlyList<CheckResult>>();
            bool cancelled = false, scanFailed = false;
            var targetList = targets.ToList();

            Info(1, $"Scanning {targetList.Count} address(es), mode {cfg.DiscoveryModes}.");
            try
            {
                await engine.ScanAsync(targetList, cfg, ct).ConfigureAwait(false);
                if (o.Mode == CliMode.Scan)
                {
                    var live = engine.Devices.Where(d => d.IsOnline).OrderBy(d => d.Ip, IpAddressKey.TextComparer).ToList();
                    Info(1, $"{live.Count} host(s) up.");
                    await ScanPortsAsync(live, ports, new ScanOptions(type, tcp, o.Udp), portResults, ct).ConfigureAwait(false);
                    if (selection is { } sel) await RunChecksAsync(portResults, sel, net, checkResults, ct).ConfigureAwait(false);
                    if (o.OsDetection) await RunOsAsync(live, portResults, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex)
            {
                // Whatever the scan found so far still goes into the report.
                stderr.WriteLine("Error: the scan stopped early: " + ex.Message);
                scanFailed = true;
            }
            if (ct.IsCancellationRequested) cancelled = true;

            var devices = engine.Devices.ToList();
            var findings = ConflictDetector.Detect(devices.Where(d => d.IsOnline).ToList(), new Dictionary<string, string>());
            var report = ReportBuilder.Build(devices, net, started.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture),
                started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ServicesOf(portResults), new ReportExtras
                {
                    Command = o.CommandLine,
                    Targets = string.Join(',', o.Targets),
                    Partial = cancelled,
                    Technique = o.Mode == CliMode.Scan && tcp ? type : null,
                    PortResults = portResults.ToDictionary(p => p.Key, p => p.Value),
                    Checks = checkResults.ToDictionary(p => p.Key, p => p.Value),
                    Findings = findings,
                });

            var written = Write(report);
            if (cancelled)
            {
                stderr.WriteLine(written == WriteResult.Failed
                    ? "Stopped. The partial result could not be written completely."
                    : "Stopped. The partial result was written.");
                return written == WriteResult.Failed ? CliExit.ScanError : CliExit.Cancelled;
            }
            if (written == WriteResult.Failed || scanFailed) return CliExit.ScanError;
            return written == WriteResult.StdoutClosed ? CliExit.OutputClosed : CliExit.Ok;
        }

        private static ScanConfig BuildConfig(CliOptions o) => new()
        {
            PingCount = 1,
            InitPingCount = 1,
            OfflineRecheckSeconds = 0,
            KnownDevicesDb = false,
            ScanHistory = false,
            EnableInternetPing = false,
            PinnedIps = [],
            DiscoveryModes = o.Mode == CliMode.List ? "list" : ScanPlan.FromOptions(o).DiscoveryModeKeys,
        };

        private async Task ScanPortsAsync(List<Device> live, PortSpec ports, ScanOptions options,
            ConcurrentDictionary<string, IReadOnlyList<PortResult>> into, CancellationToken ct)
        {
            using var gate = new SemaphoreSlim(HostParallelism);
            var tasks = live.Select(async d =>
            {
                try { await gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                var seen = new ConcurrentQueue<PortResult>();
                try
                {
                    var list = await s.PortRunner(d.Ip, ports, options, null, ct, r => seen.Enqueue(r)).ConfigureAwait(false);
                    into[d.Ip] = Finish(d.Ip, list);
                    Info(2, $"{d.Ip}: {into[d.Ip].Count(p => p.IsOpen)} open port(s).");
                }
                catch (OperationCanceledException) { into[d.Ip] = Finish(d.Ip, seen.ToList()); }
                catch (Exception ex)
                {
                    stderr.WriteLine($"Warning: {d.Ip}: port scan failed: {ex.Message}");
                    into[d.Ip] = Finish(d.Ip, seen.ToList());
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private IReadOnlyList<PortResult> Finish(string ip, IEnumerable<PortResult> list)
        {
            var keep = list.Select(r => r with { Ip = ip });
            if (o.OpenOnly) keep = keep.Where(r => r.State is PortState.Open or PortState.OpenFiltered);
            return keep.OrderBy(r => r.Protocol).ThenBy(r => r.Port).ToList();
        }

        private async Task RunChecksAsync(ConcurrentDictionary<string, IReadOnlyList<PortResult>> found, CheckSelection selection,
            NetworkInfo net, ConcurrentDictionary<string, IReadOnlyList<CheckResult>> into, CancellationToken ct)
        {
            foreach (var (ip, results) in found.OrderBy(p => p.Key, IpAddressKey.TextComparer))
            {
                ct.ThrowIfCancellationRequested();
                var services = results.Where(r => r.IsOpen)
                    .Select(r => r.Service ?? new NetService(r.ServiceName, r.Port, r.Protocol)).Select(v => v with { Ip = ip }).ToList();
                if (selection.Intrusive && InLocalSubnet(ip, net))
                    services.Add(new NetService("DHCP", 67, NetProtocol.Udp) { Ip = ip });
                if (services.Count == 0) continue;
                try
                {
                    into[ip] = await s.CheckRunner(ip, services, selection, ct).ConfigureAwait(false);
                    Info(2, $"{ip}: {into[ip].Count} check result(s).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { stderr.WriteLine($"Warning: {ip}: service checks failed: {ex.Message}"); }
            }
        }

        private static bool InLocalSubnet(string ip, NetworkInfo net) => LocalNetwork.Contains(ip, net);

        private async Task RunOsAsync(List<Device> live, ConcurrentDictionary<string, IReadOnlyList<PortResult>> found, CancellationToken ct)
        {
            if (s.OsRunner is null)
            {
                stderr.WriteLine("Warning: OS detection is not available on this machine (packet driver missing).");
                return;
            }
            foreach (var d in live)
            {
                ct.ThrowIfCancellationRequested();
                var open = found.TryGetValue(d.Ip, out var list)
                    ? list.Where(r => r.IsOpen && r.Protocol == NetProtocol.Tcp).Select(r => r.Port).ToList()
                    : [];
                try { d.OsFingerprint = await s.OsRunner(d.Ip, open, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { stderr.WriteLine($"Warning: {d.Ip}: OS detection failed: {ex.Message}"); }
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<NetService>> ServicesOf(
            ConcurrentDictionary<string, IReadOnlyList<PortResult>> found) =>
            found.ToDictionary(p => p.Key,
                p => (IReadOnlyList<NetService>)p.Value.Where(r => r.IsOpen && r.Service is not null)
                    .Select(r => r.Service! with { Ip = p.Key }).ToList());

        private enum WriteResult { Ok, Failed, StdoutClosed }

        /// <summary>The files go first, so a closed stdout (<c>| head</c>) never costs a file.</summary>
        private WriteResult Write(ScanReport report)
        {
            bool ok = true;
            foreach (var (format, path) in o.Outputs)
            {
                if (path == "-") continue;
                try { ReportFile.WriteTo(path, report, format, o.Reason); Info(1, $"Wrote {path}."); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stderr.WriteLine($"Error: cannot write '{path}': {ex.Message}");
                    ok = false;
                }
            }

            var toStdout = o.Outputs.FirstOrDefault(p => p.Value == "-");
            try
            {
                stdout.Write(o.Outputs.ContainsValue("-")
                    ? ReportWriters.Render(report, toStdout.Key, o.Reason)
                    : ReportWriters.Text(report, o.Reason));
                stdout.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return WriteResult.StdoutClosed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stderr.WriteLine("Error: cannot write the report to stdout: " + ex.Message);
                ok = false;
            }
            return ok ? WriteResult.Ok : WriteResult.Failed;
        }

        private void Info(int level, string message)
        {
            if (o.Verbosity >= level) stderr.WriteLine(message);
        }
    }

    /// <summary>Process-wide scanner settings the run changes, put back on dispose.</summary>
    private sealed class ProcessSettings : IDisposable
    {
        private readonly ScanTiming? _timing;
        private readonly bool _detection;
        private readonly int _intensity;
        private readonly string? _source;
        private readonly Func<bool>? _verbose;
        private readonly bool _active;

        private ProcessSettings() { }

        private ProcessSettings(CliOptions o, string? source)
        {
            _active = true;
            _timing = PortScanner.Timing;
            _detection = PortScanner.ServiceDetection;
            _intensity = PortScanner.ServiceProbeIntensity;
            _source = NetworkDetector.SelectedSourceIp;
            _verbose = Log.VerboseProvider;

            var plan = ScanPlan.FromOptions(o);
            var t = ScanTiming.For(plan.Timing);
            if (o.MinRate is { } min) t = t with { MinRate = min };
            if (o.MaxRate is { } max) t = t with { MaxRate = max };
            if (o.MaxRetries is { } retries) t = t with { Retries = retries };
            if (o.HostTimeoutMs is { } ht) t = t with { HostTimeoutMs = ht };
            PortScanner.ApplyTiming(t);
            PortScanner.ServiceDetection = plan.ServiceDetection;
            if (o.VersionIntensity is { } vi) PortScanner.ServiceProbeIntensity = vi;
            if (source is not null) NetworkDetector.SelectedSourceIp = source;
            if (o.Debug) Log.VerboseProvider = () => true;
        }

        public static ProcessSettings Apply(CliOptions o, CliServices s, string? source) =>
            s.ApplyProcessSettings ? new ProcessSettings(o, source) : new ProcessSettings();

        public void Dispose()
        {
            if (!_active) return;
            PortScanner.ApplyTiming(_timing!);
            PortScanner.ServiceDetection = _detection;
            PortScanner.ServiceProbeIntensity = _intensity;
            NetworkDetector.SelectedSourceIp = _source;
            Log.VerboseProvider = _verbose!;
        }
    }

    /// <summary>Prints log entries to stderr for <c>-d</c>.</summary>
    private sealed class DebugLog : IDisposable
    {
        private readonly Action<LogEntry> _handler;

        private DebugLog(TextWriter err)
        {
            _handler = e => { lock (err) err.WriteLine(Log.Format(e)); };
            Log.Added += _handler;
        }

        public static DebugLog Attach(TextWriter err) => new(err);
        public void Dispose() => Log.Added -= _handler;
    }
}
