using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Service identification from recorded responses: a fake device answers each probe by name,
/// so no network is involved.</summary>
[Collection("PortScannerGate")]
public class ServiceProbeTests
{
    private static readonly TlsInfo FakeTls = new("Tls13", "CN=router.lan", "CN=router.lan", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>A device: what it says unprompted, and what it answers to a probe payload, in plain and in TLS.</summary>
    private sealed class FakeDevice
    {
        public byte[] Banner = Array.Empty<byte>();
        public Func<byte[], byte[]?> Plain = _ => null;
        public Func<byte[], byte[]?>? Tls;
        public bool Closed;
        public readonly List<string> Log = new();

        public async Task<IProbeSession?> Open(bool tls, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (Closed || tls && Tls is null) return null;
            Log.Add(tls ? "tls" : "plain");
            return new FakeSession(this, tls);
        }
    }

    private sealed class FakeSession : IProbeSession
    {
        private readonly FakeDevice _d;
        private readonly bool _tls;
        private byte[]? _answer;
        private bool _written;

        public FakeSession(FakeDevice d, bool tls) { _d = d; _tls = tls; }
        public TlsInfo? Tls => _tls ? FakeTlsInfo : null;
        private static TlsInfo FakeTlsInfo => FakeTls;

        public Task WriteAsync(byte[] data, CancellationToken ct)
        {
            _written = true;
            _d.Log.Add("send:" + Convert.ToHexString(data));
            _answer = (_tls ? _d.Tls! : _d.Plain)(data);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct)
            => Task.FromResult(_written ? _answer ?? Array.Empty<byte>() : _tls ? Array.Empty<byte>() : _d.Banner);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task<ServiceDetection?> Run(FakeDevice d, int port, int intensity = 7, CancellationToken ct = default)
        => ServiceProbeRunner.RunAsync(d.Open, ServiceProbeDb.Default, port, intensity, 100, ct);

    [Fact]
    public async Task Budget_StopsASilentPortAfterTheListenPlusOneProbe()
    {
        var plain = 0;
        async Task<IProbeSession?> Slow(bool tls, CancellationToken ct)
        {
            await Task.Delay(120, ct);
            if (tls) return null;
            plain++;
            return new FakeSession(new FakeDevice(), false);
        }
        await ServiceProbeRunner.RunAsync(Slow, ServiceProbeDb.Default, 8081, 7, 100, default, budgetMs: 200);
        Assert.InRange(plain, 1, 2);
        plain = 0;
        await ServiceProbeRunner.RunAsync(Slow, ServiceProbeDb.Default, 8081, 7, 100, default);
        Assert.True(plain > 2, "without a budget every probe of the port runs");
    }

    [Fact]
    public void BudgetFor_IsTwoBannerTimeoutsOnlyForRoutineScans()
    {
        Assert.Equal(1200, ServiceProbeRunner.BudgetFor(7, 600));
        Assert.Equal(0, ServiceProbeRunner.BudgetFor(8, 600));
        using (PortScanner.UseDeepDetection())
            Assert.Equal(0, ServiceProbeRunner.BudgetFor(7, 600));
        Assert.Equal(1200, ServiceProbeRunner.BudgetFor(7, 600));
    }

    private static NetService Apply(string name, int port, ServiceDetection d, string? version = null)
        => ServiceProbeRunner.Apply(new NetService(name, port, NetProtocol.Tcp, version), d);

    [Fact]
    public void DatabaseLoads()
    {
        var db = ServiceProbeDb.Default;
        Assert.True(db.Probes.Count >= 7);
        Assert.Contains(db.Probes, p => p.Name == "NULL" && p.Rarity == 0);
        Assert.All(db.Probes, p => Assert.True(p.Rarity is >= 0 and <= 9));
    }

    [Fact]
    public async Task SshBannerGivesProductAndVersion()
    {
        var d = new FakeDevice { Banner = Latin("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13\r\n") };
        var det = await Run(d, 22);
        var svc = Apply("SSH", 22, det!);
        Assert.Equal("SSH", svc.Name);
        Assert.Equal("OpenSSH 9.6p1 (protocol 2.0)", svc.Version);
        Assert.False(svc.IsProbable);
    }

    [Fact]
    public async Task SshOnAnOddPortIsNamedFromTheBanner()
    {
        var d = new FakeDevice { Banner = Latin("SSH-2.0-dropbear_2022.83\r\n") };
        var det = await Run(d, 2222);
        var svc = ServiceProbeRunner.Apply(new NetService("", 2222, NetProtocol.Tcp), det!);
        Assert.Equal("SSH", svc.Name);
        Assert.Contains("Dropbear 2022.83", svc.Version);
    }

    [Fact]
    public async Task SmtpBannerGivesService()
    {
        var d = new FakeDevice { Banner = Latin("220 mail.example.lan ESMTP Postfix (Debian)\r\n") };
        var svc = Apply("", 2525, (await Run(d, 2525))!);
        Assert.Equal("SMTP", svc.Name);
        Assert.Equal("Postfix", svc.Version);
    }

    [Fact]
    public async Task MySqlGreetingGivesProductAndVersion()
    {
        var greeting = new List<byte> { 0x4a, 0x00, 0x00, 0x00, 0x0a };
        greeting.AddRange(Latin("8.0.36-0ubuntu0.22.04.1"));
        greeting.AddRange(new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var d = new FakeDevice { Banner = greeting.ToArray() };
        var svc = Apply("", 3306, (await Run(d, 3306))!);
        Assert.Equal("MySQL", svc.Name);
        Assert.StartsWith("MySQL 8.0.36", svc.Version);
    }

    [Fact]
    public async Task HttpServerHeaderGivesProductAndVersion()
    {
        var d = new FakeDevice
        {
            Plain = p => Encoding.Latin1.GetString(p).StartsWith("GET /")
                ? Latin("HTTP/1.1 200 OK\r\nDate: x\r\nServer: nginx/1.24.0\r\nContent-Length: 0\r\n\r\n") : null,
        };
        var det = await Run(d, 8080);
        var svc = ServiceProbeRunner.Apply(new NetService("HTTP-Alt", 8080, NetProtocol.Tcp), det!);
        Assert.Equal("HTTP-Alt", svc.Name);
        Assert.Equal("nginx 1.24.0", svc.Version);
        Assert.Null(svc.Tls);
    }

    [Fact]
    public async Task HttpsOverTlsFindsTheServerAndKeepsTheCertificateFacts()
    {
        // Plain probes get a TLS alert back; inside TLS the same GET is answered by Apache.
        var d = new FakeDevice
        {
            Plain = _ => new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x46 },
            Tls = p => Encoding.Latin1.GetString(p).StartsWith("GET /")
                ? Latin("HTTP/1.1 200 OK\r\nServer: Apache/2.4.58 (Unix)\r\n\r\n") : null,
        };
        var det = await Run(d, 8443);
        Assert.True(det!.OverTls);
        var svc = ServiceProbeRunner.Apply(new NetService("", 8443, NetProtocol.Tcp), det);
        Assert.Equal("HTTPS", svc.Name);
        Assert.Equal("Apache httpd 2.4.58 (Unix)", svc.Version);
        Assert.Equal("CN=router.lan", svc.Tls!.Subject);
        Assert.Equal("Tls13", svc.Tls.Protocol);
    }

    [Fact]
    public async Task KnownHttpsPortKeepsItsName()
    {
        var d = new FakeDevice
        {
            Plain = _ => new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x46 },
            Tls = _ => Latin("HTTP/1.1 200 OK\r\nServer: Microsoft-IIS/10.0\r\n\r\n"),
        };
        var svc = Apply("HTTPS", 443, (await Run(d, 443))!);
        Assert.Equal("HTTPS", svc.Name);
        Assert.Equal("Microsoft IIS 10.0", svc.Version);
    }

    [Fact]
    public async Task RdpAnswerToTheNegotiationRequestIsRecognized()
    {
        var d = new FakeDevice
        {
            Plain = p => p.Length > 0 && p[0] == 0x03
                ? new byte[] { 0x03, 0x00, 0x00, 0x13, 0x0e, 0xd0, 0x00, 0x00, 0x12, 0x34, 0x00, 0x02, 0x1f, 0x08, 0x00, 0x02, 0x00, 0x00, 0x00 }
                : null,
        };
        var svc = Apply("", 3390, (await Run(d, 3390))!);
        Assert.Equal("RDP", svc.Name);
    }

    [Fact]
    public async Task SmbNegotiateResponseIsRecognized()
    {
        var d = new FakeDevice
        {
            Plain = p => p.Length > 8 && p[4] == 0xfe && p[5] == 0x53
                ? new byte[] { 0x00, 0x00, 0x00, 0x80, 0xfe, 0x53, 0x4d, 0x42, 0x40, 0x00, 0x00, 0x00 }
                : null,
        };
        var svc = Apply("", 4450, (await Run(d, 4450))!);
        Assert.Equal("SMB", svc.Name);
        Assert.Equal("dialect 2 or 3", svc.Version);
    }

    [Fact]
    public async Task UnknownServiceGetsTheSoftProtocolNotAWrongName()
    {
        // A bare IMAP style greeting that matches no known product: protocol only, marked Probable.
        var d = new FakeDevice { Banner = Latin("* OK service ready\r\n") };
        var det = await Run(d, 9999);
        Assert.True(det!.Match!.Soft);
        var svc = ServiceProbeRunner.Apply(new NetService("", 9999, NetProtocol.Tcp), det);
        Assert.Equal("IMAP", svc.Name);
        Assert.True(svc.IsProbable);
        Assert.Null(svc.Version);
    }

    [Fact]
    public async Task SoftMatchNeverRenamesAKnownPort()
    {
        var d = new FakeDevice { Banner = Latin("* OK service ready\r\n") };
        var svc = Apply("HTTP", 80, (await Run(d, 80))!);
        Assert.Equal("HTTP", svc.Name);
        Assert.False(svc.IsProbable);
    }

    [Fact]
    public async Task SilentServiceStaysUnnamed()
    {
        var d = new FakeDevice();
        var det = await Run(d, 7777);
        Assert.Null(det!.Match);
        var svc = ServiceProbeRunner.Apply(new NetService("", 7777, NetProtocol.Tcp), det);
        Assert.Equal("", svc.Name);
    }

    [Fact]
    public async Task JunkResponseIsCappedAndSanitized()
    {
        var junk = new byte[10 * 1024];
        new Random(5).NextBytes(junk);
        junk[0] = (byte)'S'; junk[1] = (byte)'S'; junk[2] = (byte)'H'; junk[3] = (byte)'-';
        var d = new FakeDevice { Banner = junk };
        var task = Run(d, 22);
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(5000)));
        var svc = Apply("SSH", 22, (await task)!);
        Assert.Equal("SSH", svc.Name);
        Assert.True((svc.Version ?? "").Length <= 100);
        Assert.DoesNotContain(svc.Version ?? "", ch => char.IsControl(ch));
    }

    [Fact]
    public async Task IntensityZeroRunsOnlyTheListenProbe()
    {
        var d = new FakeDevice { Plain = _ => Latin("HTTP/1.1 200 OK\r\nServer: nginx\r\n\r\n") };
        var det = await Run(d, 80, intensity: 0);
        Assert.Null(det!.Match);
        Assert.DoesNotContain(d.Log, l => l.StartsWith("send:"));
        Assert.DoesNotContain("tls", d.Log);
    }

    [Fact]
    public void PortSpecificProbesRunOnlyOnTheirPorts()
    {
        var db = ServiceProbeDb.Default;
        Assert.Contains(db.Plan(6379, 7), p => p.Name == "RedisPing");
        Assert.DoesNotContain(db.Plan(80, 7), p => p.Name == "RedisPing");
        Assert.DoesNotContain(db.Plan(6379, 2), p => p.Name == "RedisPing");
        Assert.Equal("NULL", db.Plan(6379, 7)[0].Name);
        // Rare generic probes only at a high intensity.
        Assert.DoesNotContain(db.Plan(80, 4), p => p.Name == "RDPNegotiate");
        Assert.Contains(db.Plan(80, 5), p => p.Name == "RDPNegotiate");
    }

    [Theory]
    [InlineData(9100)]
    [InlineData(9103)]
    [InlineData(515)]
    public void PlanForRawPrintPortsSendsNoPayload(int port)
    {
        var plan = ServiceProbeDb.Default.Plan(port, 9);
        Assert.NotEmpty(plan);
        Assert.All(plan, p => Assert.Empty(p.Payload));
        Assert.All(ServiceProbeDb.Default.Plan(port, 9, overTls: true), p => Assert.Empty(p.Payload));
    }

    [Fact]
    public void PlanForTheIppPortAllowsOnlyThePlainHttpRequest()
    {
        var sending = ServiceProbeDb.Default.Plan(631, 9).Where(p => p.Payload.Length > 0).Select(p => p.Name);
        Assert.Equal(new[] { "GetRequest" }, sending);
    }

    [Theory]
    [InlineData(9100)]
    [InlineData(515)]
    public async Task SilentRawPrintPortGetsNoBytesAndNoTlsHello(int port)
    {
        var d = new FakeDevice();
        var result = await Run(d, port, intensity: 9);
        Assert.NotNull(result);
        Assert.DoesNotContain(d.Log, l => l.StartsWith("send:"));
        Assert.DoesNotContain("tls", d.Log);
    }

    [Fact]
    public async Task RedisProbeSentOnlyToRedisPorts()
    {
        var d = new FakeDevice { Plain = p => Encoding.Latin1.GetString(p) == "PING\r\n" ? Latin("-NOAUTH Authentication required.\r\n") : null };
        var det = await Run(d, 6379);
        Assert.Equal("Redis", det!.Match!.Service);
        var other = new FakeDevice { Plain = p => Encoding.Latin1.GetString(p) == "PING\r\n" ? Latin("+PONG\r\n") : null };
        Assert.Null((await Run(other, 5000))!.Match);
        Assert.DoesNotContain(other.Log, l => l == "send:" + Convert.ToHexString(Latin("PING\r\n")));
    }

    [Fact]
    public async Task ClosedPortYieldsNothingWithoutFurtherProbes()
    {
        var d = new FakeDevice { Closed = true };
        var det = await Run(d, 22);
        Assert.Null(det!.Match);
        Assert.Empty(d.Log);
    }

    [Fact]
    public async Task CallerStopReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await Run(new FakeDevice { Banner = Latin("SSH-2.0-x\r\n") }, 22, ct: cts.Token));
    }

    [Fact]
    public async Task ProbeCountIsBounded()
    {
        var d = new FakeDevice();
        await Run(d, 6379, intensity: 9);
        Assert.True(d.Log.Count(l => l == "plain") <= ServiceProbeRunner.MaxProbesPerPort);
    }

    [Fact]
    public async Task TlsPassSharesTheProbeBudgetWithThePlainPass()
    {
        var probes = string.Join(",", Enumerable.Range(0, 7).Select(i => "{\"name\":\"P" + i + "\",\"rarity\":" + i + ",\"payload\":\"x" + i + "\"}"));
        var db = ServiceProbeDb.Parse("{\"probes\":[" + probes + "]}");
        var d = new FakeDevice { Tls = _ => null };
        await ServiceProbeRunner.RunAsync(d.Open, db, 8123, 9, 100, CancellationToken.None);
        Assert.True(d.Log.Count(l => l is "plain" or "tls") <= ServiceProbeRunner.MaxProbesPerPort, string.Join(",", d.Log));
    }

    [Fact]
    public void HandshakeTimeout_IsNeverShorterThanTheBannerWait()
    {
        using var scope = PortScanner.UseAdaptiveTimeout(50);
        var old = PortScanner.BannerTimeoutMs;
        try
        {
            PortScanner.BannerTimeoutMs = 900;
            Assert.True(TcpProbeSession.HandshakeTimeoutMs >= 900);
        }
        finally { PortScanner.BannerTimeoutMs = old; }
    }

    [Fact]
    public void BadPatternIsSkippedNotFatal()
    {
        var db = ServiceProbeDb.Parse("{\"probes\":[{\"name\":\"X\",\"rarity\":0,\"matches\":[{\"pattern\":\"(\",\"service\":\"A\"},{\"pattern\":\"^ok\",\"service\":\"B\"}]}]}");
        Assert.Equal("B", db.Probes[0].Match("ok")!.Service);
    }

    [Fact]
    public async Task RealSocketSessionReadsABannerAndTimesOutOnSilence()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync();
                await c.GetStream().WriteAsync(Latin("SSH-2.0-OpenSSH_8.9\r\n"));
                await Task.Delay(1500);
            });
            await using var s = (await TcpProbeSession.OpenAsync("127.0.0.1", port, false, CancellationToken.None))!;
            var first = await s.ReadAsync(2000, CancellationToken.None);
            Assert.StartsWith("SSH-2.0-OpenSSH_8.9", Encoding.Latin1.GetString(first));
            var second = await s.ReadAsync(100, CancellationToken.None);
            Assert.Empty(second);
            await serve;
        }
        finally { listener.Stop(); }
    }
}
