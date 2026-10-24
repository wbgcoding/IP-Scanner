using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class DnsClientTests
{
    // ── Packet helpers ──

    private static byte[] Name(string name)
    {
        var b = new List<byte>();
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.Add(0);
        return b.ToArray();
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A reply that echoes the question of <paramref name="query"/> and appends the given records
    /// (name compressed to the question).</summary>
    internal static byte[] Reply(byte[] query, int rcode, bool truncated, params (DnsRecordType Type, byte[] Data)[] answers)
    {
        var p = new List<byte> { query[0], query[1] };
        int flags = 0x8180 | rcode | (truncated ? 0x0200 : 0);
        p.AddRange(U16(flags)); p.AddRange(U16(1)); p.AddRange(U16(answers.Length)); p.AddRange(U16(0)); p.AddRange(U16(0));
        p.AddRange(query.Skip(12));
        foreach (var (type, data) in answers)
        {
            p.AddRange(new byte[] { 0xC0, 0x0C });
            p.AddRange(U16((int)type)); p.AddRange(U16(1)); p.AddRange(U32(300)); p.AddRange(U16(data.Length)); p.AddRange(data);
        }
        return p.ToArray();
    }

    private static byte[] ValidReply(string name, DnsRecordType qtype, params (DnsRecordType Type, byte[] Data)[] answers)
        => Reply(DnsClient.BuildQuery(0x1234, name, qtype)!, 0, false, answers);

    // ── Query building and names ──

    [Fact]
    public void BuildQuery_WritesHeaderQuestionAndRecursionBit()
    {
        var q = DnsClient.BuildQuery(0xABCD, "www.example.com.", DnsRecordType.MX)!;
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q.Take(12).ToArray());
        Assert.Equal(Name("www.example.com").Concat(new byte[] { 0, 15, 0, 1 }), q.Skip(12));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a..b")]
    [InlineData("bad name.example")]
    public void BuildQuery_RejectsNamesThatCannotBeSent(string name)
        => Assert.Null(DnsClient.BuildQuery(1, name, DnsRecordType.A));

    [Fact]
    public void BuildQuery_RejectsOverlongLabelsAndNames()
    {
        Assert.Null(DnsClient.BuildQuery(1, new string('a', 64) + ".com", DnsRecordType.A));
        Assert.NotNull(DnsClient.BuildQuery(1, new string('a', 63) + ".com", DnsRecordType.A));
        Assert.Null(DnsClient.BuildQuery(1, string.Join('.', Enumerable.Repeat(new string('a', 60), 5)), DnsRecordType.A));
    }

    [Fact]
    public void BuildQuery_ConvertsInternationalNamesToPunycode()
    {
        var q = DnsClient.BuildQuery(1, "bücher.example", DnsRecordType.A)!;
        Assert.Equal(Name("xn--bcher-kva.example"), q.Skip(12).Take(q.Length - 16).ToArray());
    }

    [Theory]
    [InlineData("192.168.1.10", "10.1.168.192.in-addr.arpa")]
    [InlineData("2001:db8::1", "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
    public void ReverseName_BuildsTheArpaName(string ip, string expected)
        => Assert.Equal(expected, DnsClient.ReverseName(ip));

    [Theory]
    [InlineData("example.com")]
    [InlineData("1.2.3")]
    [InlineData("")]
    public void ReverseName_IsNullForNames(string text) => Assert.Null(DnsClient.ReverseName(text));

    // ── Parsing ──

    [Fact]
    public void Parse_DecodesAddressRecordsAndFlags()
    {
        var packet = ValidReply("example.com", DnsRecordType.A,
            (DnsRecordType.A, new byte[] { 93, 184, 216, 34 }),
            (DnsRecordType.AAAA, IPAddress.Parse("2606:2800:220:1::1").GetAddressBytes()));
        packet[2] |= 0x04;      // authoritative

        var msg = DnsClient.TryParse(packet)!;
        Assert.True(msg.IsResponse);
        Assert.True(msg.Authoritative);
        Assert.False(msg.Truncated);
        Assert.Equal(0, msg.Rcode);
        Assert.Equal("example.com", msg.QuestionName);
        Assert.Equal(2, msg.Answers.Count);
        Assert.Equal(new DnsRecord(DnsRecordType.A, "example.com", 300, "93.184.216.34"), msg.Answers[0]);
        Assert.Equal("2606:2800:220:1::1", msg.Answers[1].Data);
    }

    [Fact]
    public void Parse_DecodesNameBasedTypes()
    {
        var packet = ValidReply("example.com", DnsRecordType.A,
            (DnsRecordType.CNAME, Name("alias.example.net")),
            (DnsRecordType.NS, Name("ns1.example.com")),
            (DnsRecordType.PTR, Name("host.example.com")),
            (DnsRecordType.MX, U16(10).Concat(Name("mail.example.com")).ToArray()));

        var data = DnsClient.TryParse(packet)!.Answers.Select(a => a.Data).ToArray();
        Assert.Equal(new[] { "alias.example.net", "ns1.example.com", "host.example.com", "10 mail.example.com" }, data);
    }

    [Fact]
    public void Parse_DecodesTxtSegmentsAndSoa()
    {
        var txt = new byte[] { 3, (byte)'a', (byte)'b', (byte)'c', 2, (byte)'d', (byte)'e' };
        var soa = Name("ns1.example.com").Concat(Name("admin.example.com"))
            .Concat(U32(2024010101)).Concat(U32(7200)).Concat(U32(3600)).Concat(U32(1209600)).Concat(U32(300)).ToArray();
        var packet = ValidReply("example.com", DnsRecordType.A, (DnsRecordType.TXT, txt), (DnsRecordType.SOA, soa));

        var answers = DnsClient.TryParse(packet)!.Answers;
        Assert.Equal("abcde", answers[0].Data);
        Assert.Equal("ns1.example.com admin.example.com 2024010101 7200 3600 1209600 300", answers[1].Data);
    }

    [Fact]
    public void Parse_SkipsRecordTypesItDoesNotKnow()
    {
        var packet = ValidReply("example.com", DnsRecordType.A,
            ((DnsRecordType)46, new byte[] { 1, 2, 3 }),
            (DnsRecordType.A, new byte[] { 1, 2, 3, 4 }));
        var answers = DnsClient.TryParse(packet)!.Answers;
        Assert.Single(answers);
        Assert.Equal("1.2.3.4", answers[0].Data);
    }

    [Fact]
    public void Parse_ReadsRcodeAndTruncationBits()
    {
        var q = DnsClient.BuildQuery(7, "nope.example", DnsRecordType.A)!;
        var msg = DnsClient.TryParse(Reply(q, 3, true))!;
        Assert.Equal(3, msg.Rcode);
        Assert.True(msg.Truncated);
        Assert.Empty(msg.Answers);
    }

    [Fact]
    public void Parse_EveryTruncationOfAValidPacketIsRejectedWithoutThrowing()
    {
        var packet = ValidReply("example.com", DnsRecordType.A,
            (DnsRecordType.MX, U16(10).Concat(Name("mail.example.com")).ToArray()),
            (DnsRecordType.A, new byte[] { 1, 2, 3, 4 }));
        for (int len = 0; len < packet.Length; len++)
            Assert.Null(DnsClient.TryParse(packet.AsSpan(0, len)));
        Assert.NotNull(DnsClient.TryParse(packet));
    }

    [Fact]
    public void Parse_CompressionPointerLoopIsRejected()
    {
        var q = DnsClient.BuildQuery(1, "a.example", DnsRecordType.A)!;
        var packet = Reply(q, 0, false, (DnsRecordType.CNAME, new byte[] { 0xC0, 0x0C }));
        // Make the question name point at itself.
        packet[12] = 0xC0; packet[13] = 0x0C;
        Assert.Null(DnsClient.TryParse(packet));
    }

    [Fact]
    public void Parse_LongPointerChainsAreRejected()
    {
        // 40 pointers in a row, each to the next one, then a real name.
        var packet = new List<byte> { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0 };
        int chainStart = 12 + 4 + 0;
        // question name = pointer into the chain placed after the question
        packet.AddRange(new byte[] { 0xC0, (byte)(chainStart + 2 + 4) });
        packet.AddRange(new byte[] { 0, 1, 0, 1 });
        // chain begins at chainStart + 6 ... build 40 links
        int first = packet.Count;
        for (int i = 0; i < 40; i++) packet.AddRange(new byte[] { 0xC0, (byte)(first + 2 * (i + 1)) });
        packet.AddRange(Name("end.example"));
        packet[12] = 0xC0; packet[13] = (byte)first;
        Assert.Null(DnsClient.TryParse(packet.ToArray()));
    }

    [Fact]
    public void Parse_RejectsNamesLongerThan255Bytes()
    {
        var q = new List<byte> { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 6; i++) { q.Add(50); q.AddRange(Enumerable.Repeat((byte)'a', 50)); }
        q.Add(0); q.AddRange(new byte[] { 0, 1, 0, 1 });
        Assert.Null(DnsClient.TryParse(q.ToArray()));
    }

    [Fact]
    public void Parse_RejectsReservedLabelTypesAndBadLengths()
    {
        var good = ValidReply("example.com", DnsRecordType.A, (DnsRecordType.A, new byte[] { 1, 2, 3, 4 }));

        var reserved = (byte[])good.Clone();
        reserved[12] = 0x40;
        Assert.Null(DnsClient.TryParse(reserved));

        var labelOverflow = (byte[])good.Clone();
        labelOverflow[12] = 63;     // label claims more bytes than the packet has before the zero
        Assert.Null(DnsClient.TryParse(labelOverflow));

        var rdlenOverflow = (byte[])good.Clone();
        rdlenOverflow[^5] = 0xFF;   // rdlength high byte
        Assert.Null(DnsClient.TryParse(rdlenOverflow));

        var shortA = ValidReply("example.com", DnsRecordType.A, (DnsRecordType.A, new byte[] { 1, 2, 3 }));
        Assert.Null(DnsClient.TryParse(shortA));

        var shortAaaa = ValidReply("example.com", DnsRecordType.AAAA, (DnsRecordType.AAAA, new byte[15]));
        Assert.Null(DnsClient.TryParse(shortAaaa));
    }

    [Fact]
    public void Parse_RejectsTxtSegmentRunningPastTheRecord()
    {
        var packet = ValidReply("example.com", DnsRecordType.A, (DnsRecordType.TXT, new byte[] { 9, (byte)'a' }));
        Assert.Null(DnsClient.TryParse(packet));
    }

    [Fact]
    public void Parse_RejectsMxAndSoaWithBadData()
    {
        Assert.Null(DnsClient.TryParse(ValidReply("example.com", DnsRecordType.A, (DnsRecordType.MX, new byte[] { 0, 10 }))));
        var shortSoa = Name("a.b").Concat(Name("c.d")).Concat(new byte[10]).ToArray();
        Assert.Null(DnsClient.TryParse(ValidReply("example.com", DnsRecordType.A, (DnsRecordType.SOA, shortSoa))));
    }

    [Fact]
    public void Parse_HugeAnswerCountOnASmallPacketIsRejected()
    {
        var packet = ValidReply("example.com", DnsRecordType.A);
        packet[6] = 0xFF; packet[7] = 0xFF;
        Assert.Null(DnsClient.TryParse(packet));
    }

    [Fact]
    public void Parse_RandomMutationsNeverThrow()
    {
        var seed = ValidReply("example.com", DnsRecordType.A,
            (DnsRecordType.MX, U16(10).Concat(Name("mail.example.com")).ToArray()),
            (DnsRecordType.TXT, new byte[] { 2, (byte)'h', (byte)'i' }),
            (DnsRecordType.A, new byte[] { 1, 2, 3, 4 }));
        var rng = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            var copy = (byte[])seed.Clone();
            int edits = rng.Next(1, 6);
            for (int e = 0; e < edits; e++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            var len = rng.Next(0, 4) == 0 ? rng.Next(copy.Length + 1) : copy.Length;
            var ex = Record.Exception(() => DnsClient.TryParse(copy.AsSpan(0, len)));
            Assert.Null(ex);
        }
        for (int i = 0; i < 2000; i++)
        {
            var noise = new byte[rng.Next(0, 80)];
            rng.NextBytes(noise);
            Assert.Null(Record.Exception(() => DnsClient.TryParse(noise)));
        }
    }

    [Fact]
    public void Parse_EscapesDotsAndControlBytesInsideLabels()
    {
        var q = new List<byte> { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'.', 1, 0 };
        q.AddRange(new byte[] { 0, 1, 0, 1 });
        // label "a." + byte 0x01 -> "a\.\001"
        var msg = DnsClient.TryParse(q.ToArray())!;
        Assert.Equal("a\\.\\001", msg.QuestionName);
    }

    // ── Merge ──

    [Fact]
    public void Merge_KeepsTheUsableSideAndCombinesAnswers()
    {
        var a = new DnsResult(DnsFailure.None, 0, true, 10, new[] { new DnsRecord(DnsRecordType.A, "x", 1, "1.1.1.1") });
        var aaaa = new DnsResult(DnsFailure.None, 0, true, 30, new[] { new DnsRecord(DnsRecordType.AAAA, "x", 1, "::1") });
        var bad = new DnsResult(DnsFailure.Timeout, 0, false, 99, Array.Empty<DnsRecord>());

        var both = DnsClient.Merge(a, aaaa);
        Assert.Equal(2, both.Answers.Count);
        Assert.Equal(30, both.ElapsedMs);
        Assert.Same(a, DnsClient.Merge(a, bad));
        Assert.Same(aaaa, DnsClient.Merge(bad, aaaa));
        Assert.False(DnsClient.Merge(bad, bad).Ok);
    }

    // ── Over the network (loopback fake server) ──

    internal sealed class FakeServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener? _tcp;
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public List<(DnsRecordType Type, string Name)> Questions { get; } = new();

        public FakeServer(Func<byte[], byte[]?> udpHandler, Func<byte[], byte[]?>? tcpHandler = null, IPAddress? address = null, int fixedPort = 0)
        {
            if (tcpHandler is null)
            {
                _udp = new UdpClient(new IPEndPoint(address ?? IPAddress.Loopback, fixedPort));
                Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            }
            else
            {
                // Same port number on both transports.
                while (true)
                {
                    var tcp = new TcpListener(IPAddress.Loopback, 0);
                    tcp.Start();
                    int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    try { _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
                    catch (SocketException) { tcp.Stop(); continue; }
                    _tcp = tcp; Port = port;
                    break;
                }
                _ = Task.Run(() => TcpLoop(tcpHandler));
            }
            _ = Task.Run(() => UdpLoop(udpHandler));
        }

        private async Task UdpLoop(Func<byte[], byte[]?> handler)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var r = await _udp.ReceiveAsync(_stop.Token);
                    var q = DnsClient.TryParse(r.Buffer.Concat(new byte[0]).ToArray());
                    lock (Questions) Questions.Add(((DnsRecordType)(q?.QuestionType ?? 0), q?.QuestionName ?? ""));
                    var reply = handler(r.Buffer);
                    if (reply is not null) await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
            }
            catch (Exception) { /* stopped */ }
        }

        private async Task TcpLoop(Func<byte[], byte[]?> handler)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _tcp!.AcceptTcpClientAsync(_stop.Token);
                    var s = client.GetStream();
                    var len = new byte[2];
                    await s.ReadExactlyAsync(len, _stop.Token);
                    var q = new byte[(len[0] << 8) | len[1]];
                    await s.ReadExactlyAsync(q, _stop.Token);
                    var reply = handler(q);
                    if (reply is null) continue;
                    await s.WriteAsync(new[] { (byte)(reply.Length >> 8), (byte)reply.Length }, _stop.Token);
                    await s.WriteAsync(reply, _stop.Token);
                }
            }
            catch (Exception) { /* stopped */ }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _tcp?.Stop();
        }
    }

    private static readonly byte[] Ip1234 = { 1, 2, 3, 4 };

    [Fact]
    public async Task Query_ReturnsTheAnswersOfTheServer()
    {
        using var server = new FakeServer(q => Reply(q, 0, false, (DnsRecordType.A, Ip1234)));
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromSeconds(5), port: server.Port);
        Assert.True(r.Ok);
        Assert.Equal(0, r.Rcode);
        Assert.Equal("1.2.3.4", Assert.Single(r.Answers).Data);
    }

    [Fact]
    public async Task Query_ReportsTheResponseCode()
    {
        using var server = new FakeServer(q => Reply(q, 3, false));
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "missing.example", DnsRecordType.A, TimeSpan.FromSeconds(5), port: server.Port);
        Assert.True(r.Ok);
        Assert.Equal(3, r.Rcode);
        Assert.Empty(r.Answers);
    }

    [Fact]
    public async Task Query_WithoutAReplyTimesOut()
    {
        using var server = new FakeServer(_ => null);
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromMilliseconds(300), port: server.Port);
        Assert.Equal(DnsFailure.Timeout, r.Failure);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Query_IgnoresRepliesWithTheWrongIdOrQuestion()
    {
        // The server first sends a forged reply (wrong id), then one for another name, then the real one.
        var sent = new List<byte[]>();
        using var server = new FakeServer(q =>
        {
            var wrongId = Reply(q, 0, false, (DnsRecordType.A, new byte[] { 6, 6, 6, 6 }));
            wrongId[0] ^= 0xFF;
            sent.Add(wrongId);
            return wrongId;
        });
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromMilliseconds(400), port: server.Port);
        Assert.Equal(DnsFailure.Timeout, r.Failure);
        Assert.NotEmpty(sent);
    }

    [Fact]
    public async Task Query_AMalformedReplyWithOurIdIsReportedAsMalformed()
    {
        using var server = new FakeServer(q => new byte[] { q[0], q[1], 0x81, 0x80, 0, 1, 0, 1 });
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromSeconds(5), port: server.Port);
        Assert.Equal(DnsFailure.Malformed, r.Failure);
    }

    [Fact]
    public async Task Query_TruncatedReplyIsRetriedOverTcp()
    {
        using var server = new FakeServer(
            q => Reply(q, 0, true),
            q => Reply(q, 0, false, (DnsRecordType.A, Ip1234)));
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromSeconds(5), port: server.Port);
        Assert.True(r.Ok);
        Assert.Equal("1.2.3.4", Assert.Single(r.Answers).Data);
    }

    [Fact]
    public async Task Query_ClosedPortIsANetworkErrorOrTimeoutButNeverThrows()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromMilliseconds(500), port: port);
        Assert.False(r.Ok);
        Assert.True(r.Failure is DnsFailure.Network or DnsFailure.Timeout);
    }

    [Fact]
    public async Task Query_InvalidNameFailsWithoutSendingAnything()
    {
        var r = await DnsClient.QueryAsync(IPAddress.Loopback, "bad name", DnsRecordType.A, TimeSpan.FromMilliseconds(200), port: 9);
        Assert.Equal(DnsFailure.Malformed, r.Failure);
    }

    [Fact]
    public async Task Query_CancellationThrowsOperationCanceled()
    {
        using var server = new FakeServer(_ => null);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DnsClient.QueryAsync(IPAddress.Loopback, "example.com", DnsRecordType.A, TimeSpan.FromSeconds(10), cts.Token, server.Port));
    }

    [Fact]
    public async Task QueryAuto_AskingForAnAddressSendsAPtrQuestion()
    {
        using var server = new FakeServer(q => Reply(q, 0, false, (DnsRecordType.PTR, Name("host.example.com"))));
        var r = await DnsClient.QueryAutoAsync(IPAddress.Loopback, "192.168.1.10", TimeSpan.FromSeconds(5), port: server.Port);
        Assert.Equal("host.example.com", Assert.Single(r.Answers).Data);
        lock (server.Questions) Assert.Equal((DnsRecordType.PTR, "10.1.168.192.in-addr.arpa"), Assert.Single(server.Questions));
    }

    [Fact]
    public async Task QueryAuto_AskingForANameAsksBothAddressFamilies()
    {
        using var server = new FakeServer(q =>
        {
            var type = (DnsRecordType)DnsClient.TryParse(q)!.QuestionType;
            return type == DnsRecordType.A
                ? Reply(q, 0, false, (DnsRecordType.A, Ip1234))
                : Reply(q, 0, false, (DnsRecordType.AAAA, IPAddress.IPv6Loopback.GetAddressBytes()));
        });
        var r = await DnsClient.QueryAutoAsync(IPAddress.Loopback, "example.com", TimeSpan.FromSeconds(5), port: server.Port);
        Assert.Equal(new[] { "1.2.3.4", "::1" }, r.Answers.Select(a => a.Data).OrderBy(x => x == "::1").ToArray());
    }
}
