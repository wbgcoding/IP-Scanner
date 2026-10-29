using System.Net;
using System.Threading.Channels;

namespace IpScanner.Core.Scanner;

/// <summary>Runs a <see cref="PortSpec"/> against one host through the strategy of the chosen
/// <see cref="ScanType"/> and reports every port with its state. Both the device detail view and
/// the port-check tool drive this; the legacy list-style entry points in <see cref="PortScanner"/>
/// are thin wrappers around it.</summary>
public static class PortScanEngine
{
    /// <summary>Probes every port of <paramref name="spec"/> (the protocols switched on in
    /// <paramref name="options"/>). A stop returns whatever was finished instead of throwing.
    /// <paramref name="progress"/> counts finished probes; <paramref name="onResult"/> fires for
    /// every finished port, whatever its state.</summary>
    /// <exception cref="InvalidOperationException">The scan type needs the packet driver and it is not usable.</exception>
    /// <exception cref="RawScanException">The packet driver failed while a raw scan ran.</exception>
    public static Task<IReadOnlyList<PortResult>> RunAsync(
        string ip, PortSpec spec, ScanOptions options, IProgress<int>? progress, CancellationToken ct,
        Action<PortResult>? onResult = null)
    {
        var tcpType = options.TcpType;
        IRawTransport? transport = null;
        IPortStrategy? tcp = null;
        if (options.Tcp && spec.Tcp.Count > 0)
        {
            if (ScanTypes.RequiresDriver(tcpType))
            {
                var (available, reason) = ScanTypes.Availability(tcpType);
                if (!available) throw new InvalidOperationException("Scan type unavailable: " + reason);
                if (!IPAddress.TryParse(ip, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    throw new InvalidOperationException("Raw scans need an IPv4 target");
                transport = RawTransportProvider.Create(target)
                    ?? throw new InvalidOperationException("Scan type unavailable: transport-missing");
                tcp = new RawTcpStrategy(tcpType, transport, target);
            }
            else tcp = new ConnectStrategy();
        }
        var udp = options.Udp && spec.Udp.Count > 0 ? new UdpStrategy() : null;
        return RunCoreAsync(ip, options.Tcp ? spec.Tcp : Array.Empty<int>(), tcp,
            options.Udp ? spec.Udp : Array.Empty<int>(), udp, transport, progress, ct, onResult);
    }

    /// <summary>The scheduling core, open to tests that bring their own strategies.</summary>
    internal static async Task<IReadOnlyList<PortResult>> RunCoreAsync(
        string ip, IReadOnlyCollection<int> tcpPorts, IPortStrategy? tcp,
        IReadOnlyCollection<int> udpPorts, IPortStrategy? udp, IDisposable? owned,
        IProgress<int>? progress, CancellationToken callerCt, Action<PortResult>? onResult,
        ScanTiming? timing = null, IScanClock? clock = null)
    {
        try
        {
            // An explicit timing brings its own parallelism; otherwise the process-wide setting rules.
            bool explicitTiming = timing is not null;
            timing ??= PortScanner.Timing;
            clock ??= SystemScanClock.Instance;
            int workers = Math.Max(1, (explicitTiming ? timing.Parallelism : PortScanner.MaxConcurrentProbes) / 2);
            // A host that outlasts its timeout keeps what was found so far, like a stop does.
            using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
            if (timing.HostTimeoutMs > 0) hostCts.CancelAfter(timing.HostTimeoutMs);
            var ct = hostCts.Token;
            var throttle = ProbeThrottle.IsNeeded(timing) ? new ProbeThrottle(timing, clock) : null;
            var rtt = new RttEstimator(timing);
            using var gate = new SemaphoreSlim(workers);
            var results = new System.Collections.Concurrent.ConcurrentBag<PortResult>();
            int done = 0;
            RawScanException? driverFault = null;

            // A fixed set of consumers drains a queue of probes, so a full sweep never holds
            // tens of thousands of pending tasks at once.
            var queue = Channel.CreateUnbounded<(IPortStrategy Strategy, int Port)>(
                new UnboundedChannelOptions { SingleWriter = true });
            if (tcp is not null) foreach (var p in tcpPorts) queue.Writer.TryWrite((tcp, p));
            if (udp is not null) foreach (var p in udpPorts) queue.Writer.TryWrite((udp, p));
            queue.Writer.Complete();

            // One port: wait for the delay and rate slot, probe with the measured timeout, and repeat
            // while the port stayed silent and retries are left.
            async Task<PortResult?> ProbeWithPolicyAsync(IPortStrategy strategy, int port, string name)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { if (throttle is not null) await throttle.WaitAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
                    long startedMs = clock.NowMs;
                    PortResult? result;
                    using (timing.AdaptiveRtt ? PortScanner.UseAdaptiveTimeout(rtt.TimeoutMs) : null)
                    using (PortScanner.UseServiceThrottle(throttle))
                        result = await strategy.ProbeAsync(ip, port, name, ct);
                    if (result is null) return null;
                    if (IsAnswered(result)) rtt.Update(clock.NowMs - startedMs);
                    if (attempt >= timing.Retries || !IsSilent(result)) return result;
                }
            }

            async Task Consume()
            {
                try
                {
                    await foreach (var (strategy, port) in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            var name = PortSpec.ServiceNameFor(port, strategy.Protocol);
                            var result = await PortScanner.ProbeWithGatesAsync(gate,
                                () => ProbeWithPolicyAsync(strategy, port, name), ct, returnOnCancel: true);
                            if (result is null) continue;
                            result = result with { Ip = ip };
                            results.Add(result);
                            SessionPortFacts.Record(result);
                            onResult?.Invoke(result);
                        }
                        catch (RawScanException ex)
                        {
                            // The first fault ends the run for every worker.
                            Interlocked.CompareExchange(ref driverFault, ex, null);
                            hostCts.Cancel();
                        }
                        finally { progress?.Report(Interlocked.Increment(ref done)); }
                    }
                }
                catch (OperationCanceledException) { }
            }

            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Consume()));
            if (driverFault is not null && !callerCt.IsCancellationRequested) throw driverFault;
            return results.OrderBy(r => r.Port).ThenBy(r => r.Protocol).ToList();
        }
        finally { owned?.Dispose(); }
    }
    /// <summary>A refusal or reset: the round trip of these is clean (an open port also pays for the banner read).</summary>
    private static bool IsAnswered(PortResult r)
        => r.Reason is PortReason.ConnectRefused or PortReason.Rst or PortReason.RstWindow or PortReason.IcmpUnreachable;

    /// <summary>Nothing came back at all, which a repeat may still turn into an answer.</summary>
    private static bool IsSilent(PortResult r) => r.Reason == PortReason.NoResponse;
}
