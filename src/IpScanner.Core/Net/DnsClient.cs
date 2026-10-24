using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IpScanner.Core.Net;

/// <summary>Record types the client can ask for and decode.</summary>
public enum DnsRecordType : ushort
{
    A = 1, NS = 2, CNAME = 5, SOA = 6, PTR = 12, MX = 15, TXT = 16, AAAA = 28,
}

/// <summary>One decoded answer record. <c>Data</c> is the readable value (address, name, "pref host", text).</summary>
public sealed record DnsRecord(DnsRecordType Type, string Name, uint Ttl, string Data);

/// <summary>Why a query produced no usable reply.</summary>
public enum DnsFailure { None, Timeout, Network, Malformed }

/// <summary>Outcome of one query. With <c>Failure == None</c> the server answered and <c>Rcode</c>,
/// <c>Authoritative</c> and <c>Answers</c> are valid.</summary>
public sealed record DnsResult(DnsFailure Failure, int Rcode, bool Authoritative, long ElapsedMs, IReadOnlyList<DnsRecord> Answers)
{
    public bool Ok => Failure == DnsFailure.None;
}

/// <summary>A decoded reply packet.</summary>
internal sealed record DnsMessage(
    ushort Id, bool IsResponse, bool Truncated, bool Authoritative, int Rcode,
    string QuestionName, ushort QuestionType, IReadOnlyList<DnsRecord> Answers);

/// <summary>
/// Minimal DNS client (RFC 1035): one question per query, UDP with a TCP retry when the reply is truncated.
/// Replies come from the network and are untrusted: the parser checks every offset and never throws.
/// </summary>
public static class DnsClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public const int MaxNameLength = 255;
    private const int MaxLabelLength = 63;
    private const int MaxPointerJumps = 16;
    private const int HeaderLength = 12;

    public static readonly IReadOnlyList<DnsRecordType> AllTypes = new[]
    {
        DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX,
        DnsRecordType.TXT, DnsRecordType.NS, DnsRecordType.PTR, DnsRecordType.SOA,
    };

    // ── Querying ──

    /// <summary>Asks <paramref name="server"/> for the records of one type. Returns a failure result for timeouts,
    /// network errors and unusable replies; only cancellation through <paramref name="ct"/> throws.</summary>
    public static async Task<DnsResult> QueryAsync(IPAddress server, string name, DnsRecordType type,
        TimeSpan? timeout = null, CancellationToken ct = default, int port = 53)
    {
        var sw = Stopwatch.StartNew();
        var query = TryBuildQuery(name, type, out var id);
        if (query is null) return Failed(DnsFailure.Malformed, sw);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            var msg = await ExchangeUdpAsync(server, port, query, id, name, type, cts.Token).ConfigureAwait(false);
            if (msg is { Truncated: true })
                msg = await ExchangeTcpAsync(server, port, query, id, name, type, cts.Token).ConfigureAwait(false);
            return msg is null
                ? Failed(DnsFailure.Malformed, sw)
                : new DnsResult(DnsFailure.None, msg.Rcode, msg.Authoritative, sw.ElapsedMilliseconds, msg.Answers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(DnsFailure.Timeout, sw);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return Failed(DnsFailure.Network, sw);
        }
    }

    /// <summary>The "automatic" question: an IP address is looked up in reverse (PTR), a name asks for both
    /// A and AAAA and the answers are merged.</summary>
    public static async Task<DnsResult> QueryAutoAsync(IPAddress server, string target,
        TimeSpan? timeout = null, CancellationToken ct = default, int port = 53)
    {
        target = target.Trim();
        if (ReverseName(target) is { } reverse)
            return await QueryAsync(server, reverse, DnsRecordType.PTR, timeout, ct, port).ConfigureAwait(false);

        var a = QueryAsync(server, target, DnsRecordType.A, timeout, ct, port);
        var aaaa = QueryAsync(server, target, DnsRecordType.AAAA, timeout, ct, port);
        await Task.WhenAll(a, aaaa).ConfigureAwait(false);
        return Merge(a.Result, aaaa.Result);
    }

    internal static DnsResult Merge(DnsResult first, DnsResult second)
    {
        if (!first.Ok && !second.Ok) return first;
        if (!first.Ok) return second;
        if (!second.Ok) return first;
        // A name that exists but has no record of one family answers NOERROR with nothing; keep the informative code.
        var rcode = first.Rcode != 0 ? first.Rcode : second.Rcode;
        return new DnsResult(DnsFailure.None, rcode, first.Authoritative && second.Authoritative,
            Math.Max(first.ElapsedMs, second.ElapsedMs), first.Answers.Concat(second.Answers).ToList());
    }

    private static DnsResult Failed(DnsFailure failure, Stopwatch sw)
        => new(failure, 0, false, sw.ElapsedMilliseconds, Array.Empty<DnsRecord>());

    private static async Task<DnsMessage?> ExchangeUdpAsync(IPAddress server, int port, byte[] query, ushort id,
        string name, DnsRecordType type, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Connected: the OS drops datagrams from any other source.
        await socket.ConnectAsync(new IPEndPoint(server, port), ct).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, ct).ConfigureAwait(false);
        var buffer = new byte[4096];
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            var msg = TryParse(buffer.AsSpan(0, n));
            if (msg is null)
            {
                // Garbage is ignored unless it still carries our id (a broken reply from the real server).
                if (n >= 2 && ((buffer[0] << 8) | buffer[1]) == id) return null;
                continue;
            }
            if (Matches(msg, id, name, type)) return msg;
        }
    }

    private static async Task<DnsMessage?> ExchangeTcpAsync(IPAddress server, int port, byte[] query, ushort id,
        string name, DnsRecordType type, CancellationToken ct)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server, port, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        var framed = new byte[query.Length + 2];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)query.Length;
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);

        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        int length = (lengthBytes[0] << 8) | lengthBytes[1];
        if (length < HeaderLength) return null;
        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, ct).ConfigureAwait(false);
        var msg = TryParse(packet);
        return msg is not null && Matches(msg, id, name, type) ? msg : null;
    }

    private static bool Matches(DnsMessage msg, ushort id, string name, DnsRecordType type)
        => msg.IsResponse && msg.Id == id && msg.QuestionType == (ushort)type
           && string.Equals(msg.QuestionName, EncodableName(name), StringComparison.OrdinalIgnoreCase);

    // ── Names ──

    /// <summary>The name as it goes on the wire: trimmed, no trailing dot, IDN labels in punycode. Null when it
    /// cannot be sent (empty, empty label, label over 63 or name over 255 bytes).</summary>
    internal static string? EncodableName(string name)
    {
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > MaxNameLength) return null;
        try
        {
            if (name.Any(c => c > 0x7F)) name = new IdnMapping().GetAscii(name);
        }
        catch (ArgumentException) { return null; }
        if (name.Length > MaxNameLength - 2) return null;
        foreach (var label in name.Split('.'))
            if (label.Length is 0 or > MaxLabelLength || label.Any(c => c is < (char)0x21 or > (char)0x7E)) return null;
        return name;
    }

    /// <summary>"d.c.b.a.in-addr.arpa" or the nibble-reversed ip6.arpa name for an IP address text; null when
    /// the text is no IP address.</summary>
    public static string? ReverseName(string text)
    {
        text = text.Trim();
        if (Ipv6Address.TryParse(text, out var v6)) return v6.ReverseName();
        if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') == 3)
        {
            var b = ip.GetAddressBytes();
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        }
        return null;
    }

    // ── Building ──

    internal static byte[]? TryBuildQuery(string name, DnsRecordType type, out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(0x10000);
        return BuildQuery(id, name, type);
    }

    internal static byte[]? BuildQuery(ushort id, string name, DnsRecordType type)
    {
        var wire = EncodableName(name);
        if (wire is null) return null;
        var packet = new List<byte>(HeaderLength + wire.Length + 6)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00,                 // flags: standard query, recursion desired
            0x00, 0x01,                 // one question
            0, 0, 0, 0, 0, 0,
        };
        foreach (var label in wire.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.Add(0);
        packet.Add((byte)((ushort)type >> 8)); packet.Add((byte)(ushort)type);
        packet.Add(0); packet.Add(1);   // class IN
        return packet.ToArray();
    }

    // ── Parsing ──

    /// <summary>Decodes a reply. Null for anything that is not a well-formed message; never throws.</summary>
    internal static DnsMessage? TryParse(ReadOnlySpan<byte> p)
    {
        try
        {
            if (p.Length < HeaderLength) return null;
            ushort id = (ushort)((p[0] << 8) | p[1]);
            int flags = (p[2] << 8) | p[3];
            int qd = (p[4] << 8) | p[5];
            int an = (p[6] << 8) | p[7];
            if (qd != 1) return null;   // we only ever send one question; a reply echoes it

            int pos = HeaderLength;
            if (!TryReadName(p, ref pos, out var qname)) return null;
            if (pos + 4 > p.Length) return null;
            ushort qtype = (ushort)((p[pos] << 8) | p[pos + 1]);
            pos += 4;

            var answers = new List<DnsRecord>();
            for (int i = 0; i < an; i++)
            {
                if (!TryReadName(p, ref pos, out var rname)) return null;
                if (pos + 10 > p.Length) return null;
                ushort rtype = (ushort)((p[pos] << 8) | p[pos + 1]);
                uint ttl = ((uint)p[pos + 4] << 24) | ((uint)p[pos + 5] << 16) | ((uint)p[pos + 6] << 8) | p[pos + 7];
                int rdlen = (p[pos + 8] << 8) | p[pos + 9];
                pos += 10;
                if (rdlen > p.Length - pos) return null;
                int end = pos + rdlen;
                if (IsKnown(rtype))
                {
                    if (!TryReadData((DnsRecordType)rtype, p, pos, end, out var data)) return null;
                    answers.Add(new DnsRecord((DnsRecordType)rtype, rname, ttl, data));
                }
                pos = end;
            }
            return new DnsMessage(id, (flags & 0x8000) != 0, (flags & 0x0200) != 0, (flags & 0x0400) != 0,
                flags & 0x000F, qname, qtype, answers);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsKnown(ushort type) => Enum.IsDefined(typeof(DnsRecordType), type);

    private static bool TryReadData(DnsRecordType type, ReadOnlySpan<byte> p, int start, int end, out string data)
    {
        data = "";
        int pos = start;
        switch (type)
        {
            case DnsRecordType.A:
                if (end - start != 4) return false;
                data = new IPAddress(p.Slice(start, 4)).ToString();
                return true;
            case DnsRecordType.AAAA:
                if (end - start != 16) return false;
                data = new IPAddress(p.Slice(start, 16)).ToString();
                return true;
            case DnsRecordType.NS:
            case DnsRecordType.CNAME:
            case DnsRecordType.PTR:
                if (!TryReadName(p, ref pos, out data) || pos > end) return false;
                return true;
            case DnsRecordType.MX:
            {
                if (end - start < 3) return false;
                int pref = (p[pos] << 8) | p[pos + 1];
                pos += 2;
                if (!TryReadName(p, ref pos, out var host) || pos > end) return false;
                data = pref.ToString(CultureInfo.InvariantCulture) + " " + host;
                return true;
            }
            case DnsRecordType.TXT:
            {
                var sb = new StringBuilder();
                while (pos < end)
                {
                    int len = p[pos++];
                    if (len > end - pos) return false;
                    sb.Append(Printable(Encoding.UTF8.GetString(p.Slice(pos, len))));
                    pos += len;
                }
                data = sb.ToString();
                return true;
            }
            case DnsRecordType.SOA:
            {
                if (!TryReadName(p, ref pos, out var mname) || pos > end) return false;
                if (!TryReadName(p, ref pos, out var rname) || pos > end) return false;
                if (end - pos != 20) return false;
                var nums = new uint[5];
                for (int i = 0; i < 5; i++, pos += 4)
                    nums[i] = ((uint)p[pos] << 24) | ((uint)p[pos + 1] << 16) | ((uint)p[pos + 2] << 8) | p[pos + 3];
                data = $"{mname} {rname} " + string.Join(' ', nums.Select(n => n.ToString(CultureInfo.InvariantCulture)));
                return true;
            }
        }
        return false;
    }

    private static string Printable(string s)
        => s.Any(char.IsControl) ? new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()) : s;

    /// <summary>Reads a possibly compressed name starting at <paramref name="pos"/>; on success <paramref name="pos"/>
    /// is just past the name as it appears at that spot (after the first pointer, not after its target).</summary>
    private static bool TryReadName(ReadOnlySpan<byte> p, ref int pos, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        int cur = pos, jumps = 0, wireLength = 1;
        int resume = -1;
        while (true)
        {
            if (cur >= p.Length) return false;
            int len = p[cur];
            if (len == 0)
            {
                cur++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (cur + 1 >= p.Length) return false;
                if (++jumps > MaxPointerJumps) return false;
                int target = ((len & 0x3F) << 8) | p[cur + 1];
                if (resume < 0) resume = cur + 2;
                if (target >= p.Length) return false;
                cur = target;
                continue;
            }
            if ((len & 0xC0) != 0) return false;    // reserved label types
            if (cur + 1 + len > p.Length) return false;
            wireLength += len + 1;
            if (wireLength > MaxNameLength) return false;
            if (sb.Length > 0) sb.Append('.');
            for (int i = 0; i < len; i++)
            {
                byte c = p[cur + 1 + i];
                if (c == '.' || c == '\\') sb.Append('\\').Append((char)c);
                else if (c < 0x21 || c > 0x7E) sb.Append('\\').Append(c.ToString("D3", CultureInfo.InvariantCulture));
                else sb.Append((char)c);
            }
            cur += 1 + len;
        }
        pos = resume >= 0 ? resume : cur;
        name = sb.Length == 0 ? "." : sb.ToString();
        return true;
    }
}
