using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace IpScanner.Core.Scanner.Checks;

internal sealed record HttpReply(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Everything a check may do on the network. Checks only talk through this seam, so each one is
/// unit-tested with recorded responses and the real implementation stays the single place that opens sockets.</summary>
internal interface ICheckIo
{
    /// <summary>One GET without redirects, certificate errors ignored (a probe, not a trust decision);
    /// the body is capped. Null when nothing could be fetched.</summary>
    Task<HttpReply?> HttpGetAsync(string ip, int port, bool tls, string path, CancellationToken ct);

    /// <summary>A TCP connection, plain or over TLS; null when it could not be made.</summary>
    Task<IProbeSession?> OpenTcpAsync(string ip, int port, bool tls, CancellationToken ct);

    /// <summary>One datagram to ip:port and the first datagram that comes back from there; null on silence.</summary>
    Task<byte[]?> UdpRequestAsync(string ip, int port, byte[] request, int timeoutMs, CancellationToken ct);

    /// <summary>Broadcasts one DHCP discover on the local segment and collects every reply that arrives
    /// within <paramref name="listenMs"/>. Null when the client port could not be opened.</summary>
    Task<IReadOnlyList<byte[]>?> DhcpBroadcastAsync(byte[] request, int listenMs, CancellationToken ct);
}

internal sealed class NetworkCheckIo : ICheckIo
{
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxDatagram = 4096;
    private const int MaxDhcpReplies = 16;
    private const int HttpTimeoutMs = 6000;

    public async Task<HttpReply?> HttpGetAsync(string ip, int port, bool tls, string path, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(PortScanner.EffectiveConnectTimeoutMs),
            ConnectCallback = ConnectAsync,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{(tls ? "https" : "http")}://{ip}:{port}{path}");
        request.Headers.TryAddWithoutValidation("User-Agent", "IP-Scanner");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,*/*;q=0.5");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers.Concat(response.Content.Headers))
                headers[h.Key] = string.Join(", ", h.Value);
            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            return new HttpReply((int)response.StatusCode, headers, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException)
        {
            return null;
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[MaxBodyBytes];
        int total = 0;
        try
        {
            while (total < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { /* keep what arrived */ }
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (NetworkDetector.SourceFor(context.DnsEndPoint.Host) is { } srcAddr)
                socket.Bind(new IPEndPoint(srcAddr, 0));
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task<IProbeSession?> OpenTcpAsync(string ip, int port, bool tls, CancellationToken ct)
        => TcpProbeSession.OpenAsync(ip, port, tls, ct);

    public async Task<byte[]?> UdpRequestAsync(string ip, int port, byte[] request, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        using var udp = new UdpClient(addr.AddressFamily);
        try
        {
            if (NetworkDetector.SourceFor(ip) is { } srcAddr)
                udp.Client.Bind(new IPEndPoint(srcAddr, 0));
            udp.Connect(addr, port); // a connected socket only ever accepts datagrams from that one peer
            await udp.SendAsync(request, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return reply.Buffer.Length > MaxDatagram ? reply.Buffer[..MaxDatagram] : reply.Buffer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return null; }
    }

    public async Task<IReadOnlyList<byte[]>?> DhcpBroadcastAsync(byte[] request, int listenMs, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            var local = NetworkDetector.SelectedSourceIp is { Length: > 0 } src && IPAddress.TryParse(src, out var srcAddr)
                ? srcAddr : IPAddress.Any;
            socket.Bind(new IPEndPoint(local, 68));
        }
        catch (SocketException) { return null; } // the client port is taken by another program
        var replies = new List<byte[]>();
        try
        {
            await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, 67), ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(listenMs);
            var buf = new byte[MaxDatagram];
            while (replies.Count < MaxDhcpReplies)
            {
                var got = await socket.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token).ConfigureAwait(false);
                replies.Add(buf[..got.ReceivedBytes]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { /* the listen window ended */ }
        return replies;
    }
}
