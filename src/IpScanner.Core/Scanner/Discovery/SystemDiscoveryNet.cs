using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IpScanner.Core.Scanner.Discovery;

/// <summary>The real network behind the discovery probes.</summary>
public sealed class SystemDiscoveryNet : IDiscoveryNet
{
    public static SystemDiscoveryNet Instance { get; } = new();

    public async Task<TcpOutcome> TcpConnectAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return TcpOutcome.NoAnswer;
        using var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeoutMs);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addr, port), limit.Token).ConfigureAwait(false);
            return TcpOutcome.Open;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return TcpOutcome.Refused; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { return TcpOutcome.NoAnswer; }
    }

    public async Task<bool> UdpExchangeAsync(string ip, int port, byte[] payload, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        using var socket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeoutMs);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addr, port), limit.Token).ConfigureAwait(false);
            await socket.SendAsync(payload, SocketFlags.None, limit.Token).ConfigureAwait(false);
            var buf = new byte[512];
            await socket.ReceiveAsync(buf, SocketFlags.None, limit.Token).ConfigureAwait(false);
            return true;
        }
        // A closed UDP port answers with ICMP port unreachable, which Windows reports as a connection reset.
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { return false; }
    }

    public async Task<bool> IcmpRawAsync(string ip, byte type, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        byte replyType = (byte)(type + 1);
        int bodyLen = type == 13 ? 12 : 4;
        ushort id = (ushort)(Environment.ProcessId & 0xFFFF);
        ushort seq = (ushort)Random.Shared.Next(1, 65535);
        var msg = new byte[8 + bodyLen];
        msg[0] = type;
        msg[4] = (byte)(id >> 8); msg[5] = (byte)id;
        msg[6] = (byte)(seq >> 8); msg[7] = (byte)seq;
        ushort sum = RawPackets.Checksum(msg);
        msg[2] = (byte)(sum >> 8); msg[3] = (byte)sum;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeoutMs);
            await socket.SendToAsync(msg, SocketFlags.None, new IPEndPoint(addr, 0), limit.Token).ConfigureAwait(false);
            var buf = new byte[256];
            while (!limit.IsCancellationRequested)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var r = await socket.ReceiveFromAsync(buf, SocketFlags.None, from, limit.Token).ConfigureAwait(false);
                if (!Equals(((IPEndPoint)r.RemoteEndPoint).Address, addr)) continue;
                int ihl = (buf[0] & 0x0F) * 4;
                if (r.ReceivedBytes < ihl + 8 || buf[ihl] != replyType) continue;
                if (((buf[ihl + 4] << 8) | buf[ihl + 5]) != id) continue;
                return true;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        return false;
    }

    public string? ArpResolve(string ip) => ArpHelper.SendArpOnly(ip);

    public bool IsOnLink(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        foreach (var (local, mask) in LocalSubnets())
            if (Wpcap.SameSubnet(local, addr, mask)) return true;
        return false;
    }

    private static readonly object SubnetGate = new();
    private static long _subnetsAt;
    private static (IPAddress, IPAddress)[] _subnets = Array.Empty<(IPAddress, IPAddress)>();

    private static (IPAddress, IPAddress)[] LocalSubnets()
    {
        lock (SubnetGate)
        {
            if (_subnetsAt != 0 && Environment.TickCount64 - _subnetsAt < 30_000) return _subnets;
            try
            {
                _subnets = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                    .Select(u => (u.Address, u.IPv4Mask!)).ToArray();
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { _subnets = Array.Empty<(IPAddress, IPAddress)>(); }
            _subnetsAt = Environment.TickCount64;
            return _subnets;
        }
    }

    public IRawLink? OpenLink(string deviceName, string? bpf) => Wpcap.Open(deviceName, bpf);

    public RawRoute? RouteTo(IPAddress target) => Wpcap.RouteTo(target);
}
