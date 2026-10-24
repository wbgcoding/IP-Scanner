using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Core.Cli;

/// <summary>Scans the open ports of one host (same contract as <see cref="PortScanEngine.RunAsync"/>).</summary>
public delegate Task<IReadOnlyList<PortResult>> CliPortRunner(
    string ip, PortSpec spec, ScanOptions options, IProgress<int>? progress, CancellationToken ct, Action<PortResult>? onResult);

/// <summary>Runs the service checks of one host.</summary>
public delegate Task<IReadOnlyList<CheckResult>> CliCheckRunner(
    string ip, IReadOnlyList<NetService> services, CheckSelection selection, CancellationToken ct);

/// <summary>Estimates the operating system of one host from its open TCP ports.</summary>
public delegate Task<OsEstimate?> CliOsRunner(string ip, IReadOnlyCollection<int> openPorts, CancellationToken ct);

/// <summary>Everything the command line runner needs from the machine. The default set talks to the real
/// network; tests hand in fakes, so the GUI exe and the CLI exe share one runner and one output path.</summary>
public sealed class CliServices
{
    public required Func<string, int, PingResult> Ping { get; init; }
    public IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? Enrichers { get; init; }
    public Func<string, CancellationToken, Task<string?>>? NameResolver { get; init; }
    public required CliPortRunner PortRunner { get; init; }
    public required CliCheckRunner CheckRunner { get; init; }

    /// <summary>Null when the OS fingerprint cannot run on this machine.</summary>
    public CliOsRunner? OsRunner { get; init; }

    /// <summary>The local network for the report header; the argument is the chosen source address, if any.</summary>
    public required Func<string?, NetworkInfo> Network { get; init; }
    public required Func<IReadOnlyList<NetworkDetector.NetworkCard>> Cards { get; init; }
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Func<string, IReadOnlyList<string>>? Resolver { get; init; }
    public Func<string, IEnumerable<string>>? ReadLines { get; init; }
    public Func<ScanType, (bool Available, string? Reason)> Availability { get; init; } = ScanTypes.Availability;

    /// <summary>True for the real set: the runner then applies timing, service detection and the chosen
    /// adapter to the process-wide scanner settings and puts them back afterwards.</summary>
    public bool ApplyProcessSettings { get; init; }

    public static CliServices System()
    {
        return new CliServices
        {
            Ping = (ip, timeout) => SystemScan.Ping(ip, timeout, NetworkDetector.SelectedSourceIp),
            Enrichers = SystemScan.Enrichers(1500),
            PortRunner = PortScanEngine.RunAsync,
            CheckRunner = (ip, services, selection, ct) => Scanner.Checks.CheckRunner.Create().RunAsync(ip, services, selection, ct),
            OsRunner = OsFingerprinter.Available
                ? (ip, ports, ct) => OsFingerprinter.EstimateAsync(OsFingerprinter.Net, ip, ports, OsFingerprinter.TimeoutMs, ct)
                : null,
            Network = NetworkDetector.DetectFast,
            Cards = () => NetworkDetector.ListIpv4Cards(),
            ApplyProcessSettings = true,
        };
    }
}
