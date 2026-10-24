using System.IO;
using System.Net;

namespace IpScanner.Core.Scanner;

/// <summary>The production <see cref="IRawTransport"/>: wraps each IPv4 probe in an Ethernet frame for the route
/// to the target, sends it through a capture handle and hands every captured packet to the exchange waiting for it.
/// One reader thread feeds all parallel probes of a run, so a reply is never consumed by the wrong probe.</summary>
internal sealed class WpcapRawTransport : IRawTransport
{
    private sealed class Waiter
    {
        public Func<byte[], bool> IsReply = null!;
        public readonly TaskCompletionSource<byte[]?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IRawLink _link;
    private readonly RawRoute _route;
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = new();
    private readonly Thread _reader;
    private volatile bool _stop;

    public WpcapRawTransport(IRawLink link, RawRoute route)
    {
        _link = link;
        _route = route;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "raw-capture" };
        _reader.Start();
    }

    public IPAddress LocalAddress => _route.SrcIp;

    /// <summary>The capture filter for probes to <paramref name="target"/>: its TCP segments, and ICMP errors addressed to us.</summary>
    internal static string FilterFor(IPAddress target, IPAddress local)
        => $"(tcp and src host {target}) or (icmp and dst host {local})";

    /// <summary>Opens a transport for <paramref name="target"/>, or null when no route or capture handle is available.</summary>
    internal static IRawTransport? Create(IPAddress target,
        Func<IPAddress, RawRoute?>? routeTo = null, Func<string, string?, IRawLink?>? open = null)
    {
        var route = (routeTo ?? Wpcap.RouteTo)(target);
        if (route is null) return null;
        var link = (open ?? Wpcap.Open)(route.DeviceName, FilterFor(target, route.SrcIp));
        return link is null ? null : new WpcapRawTransport(link, route);
    }

    public async Task<byte[]?> ExchangeAsync(byte[] ipPacket, Func<byte[], bool> isReply, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var waiter = new Waiter { IsReply = isReply };
        // Registered before the send so a fast reply cannot slip past.
        lock (_gate)
        {
            if (_stop) throw new ObjectDisposedException(nameof(WpcapRawTransport));
            _waiters.Add(waiter);
        }
        try
        {
            if (!_link.Send(RawPackets.Frame(ipPacket, _route.SrcMac, _route.NextHopMac)))
                throw new IOException("Sending the probe through the capture driver failed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(1, timeoutMs));
            using var reg = timeout.Token.Register(static state => ((Waiter)state!).Done.TrySetResult(null), waiter);
            var reply = await waiter.Done.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reply;
        }
        finally
        {
            lock (_gate) _waiters.Remove(waiter);
        }
    }

    private void ReadLoop()
    {
        while (!_stop)
        {
            byte[]? frame;
            try { frame = _link.Receive(50); }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Ports, "Raw capture stopped: " + ex.GetType().Name);
                break;
            }
            if (frame is null) continue;
            var ip = RawPackets.StripEthernet(frame);
            if (ip is null) continue;
            Waiter[] snapshot;
            lock (_gate) snapshot = _waiters.ToArray();
            foreach (var w in snapshot)
            {
                if (w.Done.Task.IsCompleted) continue;
                bool match;
                try { match = w.IsReply(ip); }
                catch { match = false; }
                if (match) w.Done.TrySetResult(ip);
            }
        }
    }

    public void Dispose()
    {
        Waiter[] pending;
        lock (_gate)
        {
            if (_stop) return;
            _stop = true;
            pending = _waiters.ToArray();
        }
        foreach (var w in pending) w.Done.TrySetResult(null);
        if (Thread.CurrentThread != _reader) _reader.Join(1000);
        _link.Dispose();
    }
}
