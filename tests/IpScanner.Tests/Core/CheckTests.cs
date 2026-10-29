using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>A fake <see cref="ICheckIo"/>: every call is answered by a delegate the test sets, so
/// each check is exercised with a recorded response instead of real sockets.</summary>
internal sealed class FakeCheckIo : ICheckIo
{
    public Func<string, int, bool, string, CancellationToken, Task<HttpReply?>>? OnHttpGet;
    public Func<string, int, bool, CancellationToken, Task<IProbeSession?>>? OnOpenTcp;
    public Func<string, int, byte[], int, CancellationToken, Task<byte[]?>>? OnUdpRequest;
    public Func<byte[], int, CancellationToken, Task<IReadOnlyList<byte[]>?>>? OnDhcpBroadcast;

    public Task<HttpReply?> HttpGetAsync(string ip, int port, bool tls, string path, CancellationToken ct)
        => OnHttpGet?.Invoke(ip, port, tls, path, ct) ?? Task.FromResult<HttpReply?>(null);

    public Task<IProbeSession?> OpenTcpAsync(string ip, int port, bool tls, CancellationToken ct)
        => OnOpenTcp?.Invoke(ip, port, tls, ct) ?? Task.FromResult<IProbeSession?>(null);

    public Task<byte[]?> UdpRequestAsync(string ip, int port, byte[] request, int timeoutMs, CancellationToken ct)
        => OnUdpRequest?.Invoke(ip, port, request, timeoutMs, ct) ?? Task.FromResult<byte[]?>(null);

    public Task<IReadOnlyList<byte[]>?> DhcpBroadcastAsync(byte[] request, int listenMs, CancellationToken ct)
        => OnDhcpBroadcast?.Invoke(request, listenMs, ct) ?? Task.FromResult<IReadOnlyList<byte[]>?>(null);
}

internal sealed class FakeProbeSession : IProbeSession
{
    public TlsInfo? Tls { get; init; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task WriteAsync(byte[] data, CancellationToken ct) => Task.CompletedTask;
    public Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
}

public class CheckTests
{
    private static NetService Http(int port = 80) => new("HTTP", port, NetProtocol.Tcp) { Ip = "10.0.0.5" };
    private static NetService Https(int port = 443) => new("HTTPS", port, NetProtocol.Tcp) { Ip = "10.0.0.5" };
    private static NetService Udp(string name, int port) => new(name, port, NetProtocol.Udp) { Ip = "10.0.0.5" };
    private static NetService Tcp(string name, int port) => new(name, port, NetProtocol.Tcp) { Ip = "10.0.0.5" };

    // ---- HttpTitleCheck ----

    [Fact]
    public async Task HttpTitleCheck_RecordedPage_ExtractsTitleServerAndPoweredBy()
    {
        var io = new FakeCheckIo
        {
            OnHttpGet = (_, _, _, _, _) => Task.FromResult<HttpReply?>(new HttpReply(200,
                new Dictionary<string, string> { ["Server"] = "nginx/1.2", ["X-Powered-By"] = "PHP/8.1" },
                "<html><head><title>  Router   Login </title></head></html>")),
        };
        var check = new HttpTitleCheck(io);
        var result = await check.RunAsync("10.0.0.5", Http(), default);

        Assert.Equal(CheckOutcome.Found, result.Outcome);
        Assert.Contains(result.Findings, f => f.Key == "title" && f.Value == "Router Login");
        Assert.Contains(result.Findings, f => f.Key == "server" && f.Value == "nginx/1.2");
        Assert.Contains(result.Findings, f => f.Key == "poweredBy" && f.Value == "PHP/8.1");
    }

    [Fact]
    public async Task HttpTitleCheck_NoReply_IsSilent()
    {
        var io = new FakeCheckIo { OnHttpGet = (_, _, _, _, _) => Task.FromResult<HttpReply?>(null) };
        var result = await new HttpTitleCheck(io).RunAsync("10.0.0.5", Http(), default);
        Assert.Equal(CheckOutcome.NoAnswer, result.Outcome);
    }

    [Theory]
    [InlineData("<TITLE>Caps</TITLE>", "Caps")]
    [InlineData("<title>a\n  b</title>", "a b")]
    [InlineData("<title></title>", null)]
    public void HttpTitleCheck_ExtractTitle_HandlesWhitespaceAndCase(string html, string? expected)
        => Assert.Equal(expected, HttpTitleCheck.ExtractTitle(html));

    // ---- HttpSecurityHeadersCheck ----

    [Fact]
    public async Task HttpSecurityHeadersCheck_MissingHeaders_ListsEachOne()
    {
        var io = new FakeCheckIo
        {
            OnHttpGet = (_, _, _, _, _) => Task.FromResult<HttpReply?>(
                new HttpReply(200, new Dictionary<string, string>(), "")),
        };
        var result = await new HttpSecurityHeadersCheck(io).RunAsync("10.0.0.5", Https(), default);
        Assert.Equal(CheckOutcome.Found, result.Outcome);
        Assert.Contains(result.Findings, f => f.Key == "missingHeader" && f.Value == "Strict-Transport-Security");
        Assert.Contains(result.Findings, f => f.Key == "missingHeader" && f.Value == "Content-Security-Policy");
    }

    [Fact]
    public async Task HttpSecurityHeadersCheck_AllHeadersSet_ReportsOneFinding()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = "default-src 'self'",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
        };
        var io = new FakeCheckIo { OnHttpGet = (_, _, _, _, _) => Task.FromResult<HttpReply?>(new HttpReply(200, headers, "")) };
        var result = await new HttpSecurityHeadersCheck(io).RunAsync("10.0.0.5", Http(), default);
        Assert.Single(result.Findings);
        Assert.Equal("allHeadersSet", result.Findings[0].Key);
    }

    [Fact]
    public void HttpSecurityHeadersCheck_CspFrameAncestors_CountsAsFramed()
    {
        var headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "frame-ancestors 'none'" };
        var missing = HttpSecurityHeadersCheck.MissingHeaders(headers, tls: false);
        Assert.DoesNotContain("X-Frame-Options", missing);
    }

    // ---- TlsCertificateCheck ----

    [Fact]
    public async Task TlsCertificateCheck_RecordedHandshake_ReportsProtocolAndExpiry()
    {
        var tls = new TlsInfo("Tls13", "CN=device.local", "CN=Test CA", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var io = new FakeCheckIo { OnOpenTcp = (_, _, _, _) => Task.FromResult<IProbeSession?>(new FakeProbeSession { Tls = tls }) };
        var result = await new TlsCertificateCheck(io).RunAsync("10.0.0.5", Https(), default);
        Assert.Equal(CheckOutcome.Found, result.Outcome);
        Assert.Contains(result.Findings, f => f.Key == "protocol" && f.Value == "Tls13");
        Assert.Contains(result.Findings, f => f.Key == "certExpired");
    }

    [Fact]
    public async Task TlsCertificateCheck_NoTlsSession_IsSilent()
    {
        var io = new FakeCheckIo { OnOpenTcp = (_, _, _, _) => Task.FromResult<IProbeSession?>(null) };
        var result = await new TlsCertificateCheck(io).RunAsync("10.0.0.5", Https(), default);
        Assert.Equal(CheckOutcome.NoAnswer, result.Outcome);
    }

    // ---- SnmpSysDescrCheck ----

    [Fact]
    public async Task SnmpSysDescrCheck_RecordedGetResponse_ExtractsSysDescr()
    {
        byte[] packet = BuildSnmpGetResponse("Linux router 5.10");
        var io = new FakeCheckIo { OnUdpRequest = (_, _, _, _, _) => Task.FromResult<byte[]?>(packet) };
        var result = await new SnmpSysDescrCheck(io).RunAsync("10.0.0.5", Udp("SNMP", 161), default);
        Assert.Contains(result.Findings, f => f.Key == "sysDescr" && f.Value == "Linux router 5.10");
    }

    [Fact]
    public void SnmpSysDescrCheck_ParseSysDescr_RejectsWrongPduTag()
    {
        // A valid outer shape but the PDU tag is 0xA1 (GetRequest), not 0xA2 (GetResponse): a wrong
        // community produces exactly this shape on some agents, and it must never be read as data.
        var p = new byte[] { 0x30, 0x03, 0xA1, 0x01, 0x00 };
        Assert.Null(SnmpSysDescrCheck.ParseSysDescr(p));
    }

    [Fact]
    public void SnmpSysDescrCheck_Matches_OnlyUdp161()
    {
        Assert.True(new SnmpSysDescrCheck(new FakeCheckIo()).Matches(Udp("SNMP", 161)));
        Assert.False(new SnmpSysDescrCheck(new FakeCheckIo()).Matches(Tcp("SNMP", 161)));
        Assert.False(new SnmpSysDescrCheck(new FakeCheckIo()).Matches(Udp("OTHER", 162)));
    }

    private static byte[] BuildSnmpGetResponse(string sysDescr)
    {
        var oid = new byte[] { 0x06, 0x08, 0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x00 };
        var value = Encoding.ASCII.GetBytes(sysDescr);
        var valueTlv = new byte[] { 0x04, (byte)value.Length }.Concat(value).ToArray();
        var varbind = new byte[] { 0x30, (byte)(oid.Length + valueTlv.Length) }.Concat(oid).Concat(valueTlv).ToArray();
        var varbindList = new byte[] { 0x30, (byte)varbind.Length }.Concat(varbind).ToArray();
        var requestId = new byte[] { 0x02, 0x01, 0x01 };
        var errorStatus = new byte[] { 0x02, 0x01, 0x00 };
        var errorIndex = new byte[] { 0x02, 0x01, 0x00 };
        var pduBody = requestId.Concat(errorStatus).Concat(errorIndex).Concat(varbindList).ToArray();
        var pdu = new byte[] { 0xA2, (byte)pduBody.Length }.Concat(pduBody).ToArray();
        var version = new byte[] { 0x02, 0x01, 0x00 };
        var community = new byte[] { 0x04, 0x06 }.Concat(Encoding.ASCII.GetBytes("public")).ToArray();
        var body = version.Concat(community).Concat(pdu).ToArray();
        return new byte[] { 0x30, (byte)body.Length }.Concat(body).ToArray();
    }

    // ---- UpnpDeviceCheck ----

    [Fact]
    public async Task UpnpDeviceCheck_LocationOnTarget_FetchesXmlFields()
    {
        var ssdpReply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.5:5000/desc.xml\r\n\r\n");
        var xml = "<root><device><friendlyName>Printer</friendlyName><manufacturer>Acme</manufacturer></device></root>";
        var io = new FakeCheckIo
        {
            OnUdpRequest = (_, _, _, _, _) => Task.FromResult<byte[]?>(ssdpReply),
            OnHttpGet = (_, _, _, _, _) => Task.FromResult<HttpReply?>(new HttpReply(200, new Dictionary<string, string>(), xml)),
        };
        var result = await new UpnpDeviceCheck(io).RunAsync("10.0.0.5", Udp("SSDP", 1900), default);
        Assert.Contains(result.Findings, f => f.Key == "friendlyName" && f.Value == "Printer");
        Assert.Contains(result.Findings, f => f.Key == "manufacturer" && f.Value == "Acme");
    }

    [Fact]
    public async Task UpnpDeviceCheck_LocationOnDifferentHost_NeverFetchesIt()
    {
        var ssdpReply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.9:5000/desc.xml\r\n\r\n");
        bool fetched = false;
        var io = new FakeCheckIo
        {
            OnUdpRequest = (_, _, _, _, _) => Task.FromResult<byte[]?>(ssdpReply),
            OnHttpGet = (_, _, _, _, _) => { fetched = true; return Task.FromResult<HttpReply?>(null); },
        };
        var result = await new UpnpDeviceCheck(io).RunAsync("10.0.0.5", Udp("SSDP", 1900), default);
        Assert.False(fetched);
        Assert.Contains(result.Findings, f => f.Key == "upnpLocation");
    }

    // ---- MdnsServicesCheck ----

    [Fact]
    public void MdnsServicesCheck_BuildPtrQuery_SetsQuBitAndPtrType()
    {
        var q = MdnsServicesCheck.BuildPtrQuery("_services._dns-sd._udp.local");
        Assert.Equal(1, q[4] << 8 | q[5]); // QDCOUNT = 1
        Assert.Equal(0x8001, q[^2] << 8 | q[^1]); // QCLASS with QU bit
    }

    [Fact]
    public async Task MdnsServicesCheck_RecordedPtrAnswers_ListsServiceNames()
    {
        byte[] packet = BuildMdnsPtrReply("_http._tcp.local", "_printer._tcp.local");
        var io = new FakeCheckIo { OnUdpRequest = (_, _, _, _, _) => Task.FromResult<byte[]?>(packet) };
        var result = await new MdnsServicesCheck(io).RunAsync("10.0.0.5", Udp("MDNS", 5353), default);
        Assert.Contains(result.Findings, f => f.Key == "mdnsService" && f.Value == "_http._tcp.local");
        Assert.Contains(result.Findings, f => f.Key == "mdnsService" && f.Value == "_printer._tcp.local");
    }

    private static byte[] BuildMdnsPtrReply(params string[] ptrTargets)
    {
        using var ms = new System.IO.MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0); W16(0x8400); W16(0); W16(ptrTargets.Length); W16(0); W16(0);
        foreach (var target in ptrTargets)
        {
            // Name: a single dummy label (owner name does not matter for ParsePtrNames).
            ms.WriteByte(1); ms.WriteByte((byte)'_'); ms.WriteByte(0);
            W16(12); W16(1); // TYPE=PTR, CLASS=IN
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // TTL
            var labels = target.Split('.');
            using var rdata = new System.IO.MemoryStream();
            foreach (var label in labels)
            {
                var b = Encoding.ASCII.GetBytes(label);
                rdata.WriteByte((byte)b.Length);
                rdata.Write(b, 0, b.Length);
            }
            rdata.WriteByte(0);
            var rd = rdata.ToArray();
            W16(rd.Length);
            ms.Write(rd, 0, rd.Length);
        }
        return ms.ToArray();
    }

    // ---- NetBiosNamesCheck ----

    [Fact]
    public async Task NetBiosNamesCheck_RecordedNbstat_ReturnsUniqueNonGroupNames()
    {
        byte[] packet = BuildNbstatReply(("WORKSTATION1   ", false), ("WORKGROUP      ", true));
        var io = new FakeCheckIo { OnUdpRequest = (_, _, _, _, _) => Task.FromResult<byte[]?>(packet) };
        var result = await new NetBiosNamesCheck(io).RunAsync("10.0.0.5", Udp("NETBIOS", 137), default);
        Assert.Contains(result.Findings, f => f.Key == "netbiosName" && f.Value.Trim() == "WORKSTATION1");
        Assert.DoesNotContain(result.Findings, f => f.Value.Contains("WORKGROUP"));
    }

    [Fact]
    public void NetBiosNamesCheck_Matches_Udp137OrTcp139()
    {
        var check = new NetBiosNamesCheck(new FakeCheckIo());
        Assert.True(check.Matches(Udp("NETBIOS", 137)));
        Assert.True(check.Matches(Tcp("SMB", 139)));
        Assert.False(check.Matches(Tcp("SMB", 445)));
    }

    private static byte[] BuildNbstatReply(params (string Name, bool Group)[] names)
    {
        var header = new byte[57];
        header[56] = (byte)names.Length;
        var bytes = new List<byte>(header);
        foreach (var (name, group) in names)
        {
            var padded = name.PadRight(15).Substring(0, 15);
            bytes.AddRange(Encoding.ASCII.GetBytes(padded));
            bytes.Add((byte)' '); // the 16th "name" byte (type)
            bytes.Add((byte)(group ? 0x80 : 0x00));
            bytes.Add(0x04);
        }
        return bytes.ToArray();
    }

    // ---- DhcpDiscoveryCheck ----

    [Fact]
    public void DhcpDiscoveryCheck_Matches_OnlyUdp67()
    {
        var check = new DhcpDiscoveryCheck(new FakeCheckIo());
        Assert.True(check.Matches(Udp("DHCP", 67)));
        Assert.False(check.Matches(Udp("OTHER", 68)));
    }

    [Fact]
    public async Task DhcpDiscoveryCheck_RecordedOffer_ExtractsServerRouterDnsLease()
    {
        var io = new FakeCheckIo
        {
            OnDhcpBroadcast = (req, _, _) => Task.FromResult<IReadOnlyList<byte[]>?>(new List<byte[]>
                { BuildDhcpOffer("192.168.1.50", "192.168.1.1", "192.168.1.1", "8.8.8.8", 86400, req) }),
        };
        var result = await new DhcpDiscoveryCheck(io).RunAsync("192.168.1.50", Udp("DHCP", 67), default);
        Assert.Contains(result.Findings, f => f.Key == "dhcpServer" && f.Value == "192.168.1.1");
        Assert.Contains(result.Findings, f => f.Key == "dhcpRouter" && f.Value == "192.168.1.1");
        Assert.Contains(result.Findings, f => f.Key == "dhcpDns" && f.Value == "8.8.8.8");
        Assert.Contains(result.Findings, f => f.Key == "dhcpLease" && f.Value == "86400");
    }

    [Fact]
    public async Task DhcpDiscoveryCheck_IgnoresRepliesThatAreNotOffersToUs()
    {
        var io = new FakeCheckIo
        {
            OnDhcpBroadcast = (req, _, _) =>
            {
                var otherXid = BuildDhcpOffer("10.0.0.9", "10.0.0.1", null, null, 60, req);
                otherXid[4] ^= 0xFF;
                var otherClient = BuildDhcpOffer("10.0.0.8", "10.0.0.2", null, null, 60, req);
                otherClient[28] ^= 0xFF;
                var ack = BuildDhcpOffer("10.0.0.7", "10.0.0.3", null, null, 60, req, messageType: 5);
                var ours = BuildDhcpOffer("192.168.1.50", "192.168.1.1", null, null, 86400, req);
                return Task.FromResult<IReadOnlyList<byte[]>?>(new List<byte[]> { otherXid, otherClient, ack, ours });
            },
        };
        var result = await new DhcpDiscoveryCheck(io).RunAsync("192.168.1.50", Udp("DHCP", 67), default);
        var servers = result.Findings.Where(f => f.Key == "dhcpServer").Select(f => f.Value).ToList();
        Assert.Equal(new[] { "192.168.1.1" }, servers);
    }

    [Fact]
    public void DhcpDiscoveryCheck_BuildDiscover_SetsBroadcastFlagAndMagicCookie()
    {
        var p = DhcpDiscoveryCheck.BuildDiscover();
        Assert.Equal(1, p[0]); // BOOTREQUEST
        Assert.Equal(0x80, p[10]); // broadcast flag
        Assert.Equal(new byte[] { 0x63, 0x82, 0x53, 0x63 }, p[236..240]);
    }

    private static byte[] BuildDhcpOffer(string yourAddress, string? serverId, string? router, string? dns, uint leaseSeconds,
        byte[] request, byte messageType = 2)
    {
        var p = new byte[240];
        p[0] = 2; // BOOTREPLY
        Array.Copy(request, 4, p, 4, 4);   // xid
        Array.Copy(request, 28, p, 28, 6); // chaddr
        var yi = yourAddress.Split('.').Select(byte.Parse).ToArray();
        Array.Copy(yi, 0, p, 16, 4);
        p[236] = 0x63; p[237] = 0x82; p[238] = 0x53; p[239] = 0x63;
        var options = new List<byte> { 53, 1, messageType };
        void AddIp(byte code, string ip) { options.Add(code); options.Add(4); options.AddRange(ip.Split('.').Select(byte.Parse)); }
        if (serverId is not null) AddIp(54, serverId);
        if (router is not null) AddIp(3, router);
        if (dns is not null) AddIp(6, dns);
        options.Add(51); options.Add(4);
        options.Add((byte)(leaseSeconds >> 24)); options.Add((byte)(leaseSeconds >> 16));
        options.Add((byte)(leaseSeconds >> 8)); options.Add((byte)leaseSeconds);
        options.Add(255);
        return p.Concat(options).ToArray();
    }

    // ---- CheckRunner: category gating, matching, per-host ----

    private static CheckRunner RunnerWithFakes(FakeCheckIo io) => new(CheckCatalog.Create(io));

    [Fact]
    public void Plan_ServiceNeverMatchingAnyCheck_ProducesNoEntry()
    {
        var runner = RunnerWithFakes(new FakeCheckIo());
        var plan = runner.Plan(new[] { Tcp("UNKNOWN-SERVICE", 54321) }, CheckSelection.Default);
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_DisablingACategory_RemovesItsChecksFromThePlan()
    {
        var runner = RunnerWithFakes(new FakeCheckIo());
        var services = new[] { Http(), Udp("SNMP", 161) };

        var withDiscovery = runner.Plan(services, CheckSelection.Default);
        Assert.Contains(withDiscovery, e => e.Check.Id == "snmp-sysdescr");

        var withoutDiscovery = runner.Plan(services, CheckSelection.Default with { Discovery = false });
        Assert.DoesNotContain(withoutDiscovery, e => e.Check.Id == "snmp-sysdescr");
        Assert.Contains(withoutDiscovery, e => e.Check.Id == "http-title"); // Safe stays on
    }

    [Fact]
    public void CheckSelectionDefault_ExcludesIntrusive()
    {
        Assert.False(CheckSelection.Default.Intrusive);
        var runner = RunnerWithFakes(new FakeCheckIo());
        var plan = runner.Plan(new[] { Udp("DHCP", 67) }, CheckSelection.Default);
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_IntrusiveSelected_IncludesDhcpCheck()
    {
        var runner = RunnerWithFakes(new FakeCheckIo());
        var plan = runner.Plan(new[] { Udp("DHCP", 67) }, new CheckSelection(true, true, true));
        Assert.Contains(plan, e => e.Check.Id == "dhcp-discover");
    }

    [Fact]
    public void Plan_PerHostCheck_RunsOnlyOnceForTheFirstMatchingService()
    {
        var runner = RunnerWithFakes(new FakeCheckIo());
        var services = new[] { Udp("NETBIOS", 137), Tcp("SMB", 139) };
        var plan = runner.Plan(services, CheckSelection.Default);
        Assert.Single(plan, e => e.Check.Id == "netbios-names");
    }

    [Fact]
    public async Task RunAsync_NonMatchingService_NeverInvokesTheCheck()
    {
        bool invoked = false;
        var io = new FakeCheckIo { OnHttpGet = (_, _, _, _, _) => { invoked = true; return Task.FromResult<HttpReply?>(null); } };
        var runner = RunnerWithFakes(io);
        await runner.RunAsync("10.0.0.5", new[] { Udp("SNMP", 161) }, new CheckSelection(true, false, false), default);
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_CheckThrows_BecomesASilentResultInsteadOfFailingTheRun()
    {
        var io = new FakeCheckIo { OnHttpGet = (_, _, _, _, _) => throw new InvalidOperationException("boom") };
        var runner = RunnerWithFakes(io);
        var results = await runner.RunAsync("10.0.0.5", new[] { Http() }, CheckSelection.Default, default);
        Assert.Contains(results, r => r.CheckId == "http-title" && r.Outcome == CheckOutcome.NoAnswer);
    }
}
