using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace IpScanner.Core.Scanner;

/// <summary>One way of asking a single port for its state. All strategies answer with a
/// <see cref="PortResult"/> or null when the caller stopped.</summary>
internal interface IPortStrategy
{
    NetProtocol Protocol { get; }

    Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct);
}

/// <summary>Full TCP handshake through the OS; works everywhere and also reads a banner.</summary>
internal sealed class ConnectStrategy : IPortStrategy
{
    public NetProtocol Protocol => NetProtocol.Tcp;

    public Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
        => PortScanner.ProbeTcpStateAsync(ip, port, name, ct);
}

/// <summary>UDP datagram probe: a reply is Open, an ICMP port-unreachable is Closed, silence is OpenFiltered.</summary>
internal sealed class UdpStrategy : IPortStrategy
{
    public NetProtocol Protocol => NetProtocol.Udp;

    public Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
        => PortScanner.ProbeUdpStateAsync(ip, port, name, ct);
}

/// <summary>The capture driver failed to send or receive (adapter gone, pcap error). Ends the run, because
/// classifying the remaining ports as filtered would report probes that never went out.</summary>
internal sealed class RawScanException : IOException
{
    public RawScanException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>SYN, ACK, Window, FIN, NULL, Xmas and Maimon probes sent as hand-built packets
/// through the packet driver. Only constructed when <see cref="ScanTypes.Availability"/> says yes.</summary>
internal sealed class RawTcpStrategy : IPortStrategy
{
    private readonly ScanType _type;
    private readonly IRawTransport _transport;
    private readonly IPAddress _target;
    private readonly Func<int> _timeoutMs;

    public RawTcpStrategy(ScanType type, IRawTransport transport, IPAddress target, Func<int>? timeoutMs = null)
    {
        if (!ScanTypes.RequiresDriver(type)) throw new ArgumentOutOfRangeException(nameof(type), type, "not a raw TCP scan type");
        _type = type;
        _transport = transport;
        _target = target;
        _timeoutMs = timeoutMs ?? (() => PortScanner.EffectiveConnectTimeoutMs);
    }

    public NetProtocol Protocol => NetProtocol.Tcp;

    public async Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        // A fresh ephemeral source port per probe keeps replies of parallel probes apart.
        int sourcePort = 40000 + RandomNumberGenerator.GetInt32(20000);
        uint sequence = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        var packet = RawPackets.BuildTcpIp(_transport.LocalAddress, _target, sourcePort, port, sequence, 0,
            TcpPacketBuilder.FlagsFor(_type), 1024, _type == ScanType.Syn ? RawPackets.MssOption : null,
            ipId: RandomNumberGenerator.GetInt32(65536), dontFragment: false);
        byte[]? raw;
        try
        {
            raw = await _transport.ExchangeAsync(packet,
                p => RawPackets.ParseIp(p) is { } r && RawPacketParser.Answers(r, _target, port, sourcePort),
                _timeoutMs(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Debug(LogArea.Ports, "Raw probe failed: " + ex.GetType().Name);
            throw new RawScanException(ex.Message, ex);
        }
        if (ct.IsCancellationRequested) return null;
        var reply = raw is null ? null : RawPackets.ParseIp(raw);
        var (state, reason) = RawScanClassifier.Classify(_type, reply);
        var service = state == PortState.Open ? new NetService(name, port, NetProtocol.Tcp) : null;
        return new PortResult(port, NetProtocol.Tcp, state, reason, service);
    }
}
