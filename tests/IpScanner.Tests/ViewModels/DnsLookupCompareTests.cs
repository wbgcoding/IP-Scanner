using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using IpScanner.Core.Localization;
using IpScanner.Core.Net;
using IpScanner.Tests.Core;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class DnsLookupCompareTests
{
    private static DnsRecord A(string ip, string name = "host.test", uint ttl = 300)
        => new(DnsRecordType.A, name, ttl, ip);

    private static DnsResult Reply(long ms, int rcode, params DnsRecord[] answers)
        => new(DnsFailure.None, rcode, false, ms, answers);

    private static DnsResult Fail(DnsFailure failure)
        => new(failure, 0, false, 0, Array.Empty<DnsRecord>());

    private static DnsLookupToolViewModel NewVm(params string[] systemServers)
        => new() { SystemServerProvider = () => systemServers.Select(IPAddress.Parse).ToList() };

    // ── Server validation ──

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData(" 192.168.1.1 ", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("::1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("8.8.8", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1%12", false)]
    [InlineData("dns.example.com", false)]
    [InlineData("8.8.8.8:53", false)]
    public void TryParseServer_AcceptsOnlyUsableAddresses(string text, bool expected)
        => Assert.Equal(expected, DnsLookupToolViewModel.TryParseServer(text, out _));

    [Fact]
    public void Server_BlankOrSystemLabelMeansTheSystemResolver()
    {
        var vm = NewVm("10.0.0.1");
        vm.Target = "example.com";
        Assert.True(vm.UsesSystemResolver);
        vm.Server = "9.9.9.9";
        Assert.False(vm.UsesSystemResolver);
        Assert.True(vm.CanStart);
        vm.Server = Loc.DnsServerSystem;
        Assert.True(vm.UsesSystemResolver);
        Assert.False(vm.HasServerError);
        vm.Server = "";
        Assert.True(vm.UsesSystemResolver);
    }

    [Fact]
    public void Server_InvalidTextBlocksStartAndShowsTheError()
    {
        var vm = NewVm();
        vm.Target = "example.com";
        vm.Server = "not-an-ip";
        Assert.False(vm.CanStart);
        Assert.True(vm.HasServerError);
        Assert.Equal(Loc.DnsServerInvalid, vm.ServerError);
        vm.Server = "1.1.1.1";
        Assert.True(vm.CanStart);
        Assert.False(vm.HasServerError);
    }

    [Fact]
    public void ARecordTypeOrACustomServerLeavesTheOperatingSystemPath()
    {
        var vm = NewVm();
        Assert.True(vm.UsesSystemResolver);
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.MX);
        Assert.False(vm.UsesSystemResolver);
        vm.SelectedType = vm.TypeOptions[0];
        Assert.True(vm.UsesSystemResolver);
        vm.CompareMode = true;
        Assert.False(vm.UsesSystemResolver);
    }

    [Fact]
    public void TypeOptions_StartWithAutomaticAndListAllEightTypes()
    {
        var vm = NewVm();
        Assert.Null(vm.TypeOptions[0].Type);
        Assert.Equal(
            new[] { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "PTR", "SOA" }.OrderBy(x => x),
            vm.TypeOptions.Skip(1).Select(o => o.Type!.Value.ToString()).OrderBy(x => x));
    }

    // ── Compare list ──

    [Fact]
    public void CompareMode_PrefillsTheSystemServersOnceUpToTheLimit()
    {
        var vm = NewVm("10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6", "10.0.0.7");
        vm.CompareMode = true;
        Assert.Equal(DnsLookupToolViewModel.MaxCompareServers, vm.CompareServers.Count);
        vm.RemoveCompareServer("10.0.0.1");
        vm.CompareMode = false;
        vm.CompareMode = true;
        Assert.Equal(DnsLookupToolViewModel.MaxCompareServers - 1, vm.CompareServers.Count);
    }

    [Fact]
    public void AddCompareServer_ValidatesDeduplicatesAndCapsAtSix()
    {
        var vm = NewVm();
        vm.CompareMode = true;
        vm.Target = "example.com";
        Assert.False(vm.CanStart);

        vm.NewServer = "nope";
        Assert.False(vm.AddCompareServer());
        Assert.Equal(Loc.DnsServerInvalid, vm.ServerError);
        vm.NewServer = "nope2";
        Assert.False(vm.HasServerError);

        for (var i = 1; i <= 6; i++)
        {
            vm.NewServer = "10.0.0." + i;
            Assert.True(vm.AddCompareServer());
        }
        Assert.True(vm.CanStart);
        Assert.Equal("", vm.NewServer);

        vm.NewServer = "10.0.0.3";
        Assert.True(vm.AddCompareServer());
        Assert.Equal(6, vm.CompareServers.Count);

        vm.NewServer = "10.0.0.7";
        Assert.False(vm.AddCompareServer());
        Assert.Equal(Loc.DnsCompareMax, vm.ServerError);
        Assert.Equal(6, vm.CompareServers.Count);

        vm.RemoveCompareServer("10.0.0.1");
        Assert.True(vm.AddCompareServer());
        Assert.Equal(6, vm.CompareServers.Count);
    }

    [Fact]
    public void CompareMode_NeedsAtLeastOneServerToStart()
    {
        var vm = NewVm("10.0.0.1");
        vm.Target = "example.com";
        vm.CompareMode = true;
        Assert.True(vm.CanStart);
        vm.RemoveCompareServer("10.0.0.1");
        Assert.False(vm.CanStart);
    }

    // ── Compare table ──

    [Fact]
    public void BuildCompareRows_MarksTheFastestReplyAndTheDeviatingAnswer()
    {
        var (rows, fastest) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2", "s3" },
            new[]
            {
                Reply(40, 0, A("1.1.1.1")),
                Reply(12, 0, A("1.1.1.1", ttl: 10)),
                Reply(5, 0, A("9.9.9.9")),
            });
        Assert.Equal(2, fastest);
        Assert.Equal(new[] { false, false, true }, rows.Select(r => r.IsFastest));
        Assert.Equal(new[] { false, false, true }, rows.Select(r => r.IsMismatch));
        Assert.Equal(Loc.DnsAnswerDiffers, rows[2].Tip);
        Assert.Null(rows[0].Tip);
        Assert.Equal("NOERROR", rows[0].Code);
        Assert.Equal("12", rows[1].Time);
    }

    [Fact]
    public void BuildCompareRows_IgnoresOrderTtlAndCaseWhenComparing()
    {
        var (rows, _) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2" },
            new[]
            {
                Reply(10, 0, A("1.1.1.1"), A("2.2.2.2")),
                Reply(20, 0, A("2.2.2.2", name: "HOST.test", ttl: 5), A("1.1.1.1", ttl: 7)),
            });
        Assert.All(rows, r => Assert.False(r.IsMismatch));
    }

    [Fact]
    public void BuildCompareRows_DifferentResponseCodesCountAsDifferent()
    {
        var (rows, _) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2", "s3" },
            new[] { Reply(10, 0, A("1.1.1.1")), Reply(20, 0, A("1.1.1.1")), Reply(30, 3) });
        Assert.Equal(new[] { false, false, true }, rows.Select(r => r.IsMismatch));
        Assert.Equal("NXDOMAIN", rows[2].Code);
        Assert.Equal("–", rows[2].Answers);
    }

    [Fact]
    public void BuildCompareRows_AMajorityDecidesAndATieFollowsTheFirstServer()
    {
        var (majority, _) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2", "s3" },
            new[] { Reply(10, 0, A("9.9.9.9")), Reply(20, 0, A("1.1.1.1")), Reply(30, 0, A("1.1.1.1")) });
        Assert.Equal(new[] { true, false, false }, majority.Select(r => r.IsMismatch));

        var (tie, _) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2" },
            new[] { Reply(10, 0, A("1.1.1.1")), Reply(20, 0, A("2.2.2.2")) });
        Assert.Equal(new[] { false, true }, tie.Select(r => r.IsMismatch));
    }

    [Fact]
    public void BuildCompareRows_ServersWithoutAReplyAreNeverFastestOrDeviating()
    {
        var (rows, fastest) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2", "s3" },
            new[] { Fail(DnsFailure.Timeout), Reply(30, 0, A("1.1.1.1")), Fail(DnsFailure.Network) });
        Assert.Equal(1, fastest);
        Assert.Equal(new[] { false, false, false }, rows.Select(r => r.IsMismatch));
        Assert.Equal(Loc.DnsCodeTimeout, rows[0].Code);
        Assert.Equal(Loc.DnsCodeError, rows[2].Code);
        Assert.Equal("–", rows[0].Time);
    }

    [Fact]
    public void BuildCompareRows_NoReplyAtAllHasNoFastestRow()
    {
        var (rows, fastest) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1", "s2" }, new[] { Fail(DnsFailure.Timeout), Fail(DnsFailure.Timeout) });
        Assert.Equal(-1, fastest);
        Assert.All(rows, r => Assert.False(r.IsFastest || r.IsMismatch));
    }

    [Fact]
    public void BuildCompareRows_JoinsSeveralAnswers()
    {
        var (rows, _) = DnsLookupToolViewModel.BuildCompareRows(
            new[] { "s1" }, new[] { Reply(10, 0, A("1.1.1.1"), A("2.2.2.2")) });
        Assert.Equal("1.1.1.1; 2.2.2.2", rows[0].Answers);
        Assert.False(rows[0].IsMismatch);
    }

    // ── Over the loopback (fake servers) ──

    private static byte[] Ip(byte a, byte b, byte c, byte d) => new[] { a, b, c, d };

    [Fact]
    public async Task Single_AskedServerAnswersIntoTheRecordTable()
    {
        using var server = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false, (DnsRecordType.A, Ip(1, 2, 3, 4))));
        var vm = NewVm();
        vm.Port = server.Port;
        vm.Target = "host.test";
        vm.Server = "127.0.0.1";
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.A);
        await vm.StartAsync();

        Assert.True(vm.ShowRecordResults);
        Assert.False(vm.ShowCompareResults);
        var row = Assert.Single(vm.Records);
        Assert.Equal("A", row.Type);
        Assert.Equal("1.2.3.4", row.Value);
        Assert.Equal("300", row.Ttl);
        Assert.Contains("127.0.0.1", vm.Status);
        Assert.Contains("NOERROR", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Equal("A\thost.test\t1.2.3.4\t300", vm.CopyText());
    }

    [Fact]
    public async Task Single_AMalformedNameIsRejectedBeforeAnythingIsSent()
    {
        using var server = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false));
        var vm = NewVm();
        vm.Port = server.Port;
        vm.Target = "bad name with spaces";
        vm.Server = "127.0.0.1";
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.A);
        await vm.StartAsync();
        Assert.Equal(Loc.DnsBadName, vm.Status);
        Assert.Empty(server.Questions);
    }

    [Fact]
    public async Task Single_AnUnreachableServerReportsTheTimeout()
    {
        using var server = new DnsClientTests.FakeServer(_ => null);
        var vm = NewVm();
        vm.Port = server.Port;
        vm.QueryTimeout = TimeSpan.FromMilliseconds(500);
        vm.Target = "host.test";
        vm.Server = "127.0.0.1";
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.A);
        await vm.StartAsync();
        Assert.Equal(string.Format(Loc.DnsNoReplyFmt, "127.0.0.1", "0.5"), vm.Status);
        Assert.False(vm.ShowRecordResults);
    }

    [Fact]
    public async Task Compare_AsksEveryServerAndMarksTheOneThatDiffers()
    {
        using var s1 = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false, (DnsRecordType.A, Ip(1, 2, 3, 4))));
        using var s2 = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false, (DnsRecordType.A, Ip(1, 2, 3, 4))),
            address: IPAddress.Parse("127.0.0.2"), fixedPort: s1.Port);
        using var s3 = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false, (DnsRecordType.A, Ip(6, 6, 6, 6))),
            address: IPAddress.Parse("127.0.0.3"), fixedPort: s1.Port);
        using var s4 = new DnsClientTests.FakeServer(_ => null, address: IPAddress.Parse("127.0.0.4"), fixedPort: s1.Port);

        var vm = NewVm();
        vm.Port = s1.Port;
        vm.QueryTimeout = TimeSpan.FromMilliseconds(400);
        vm.Target = "host.test";
        vm.CompareMode = true;
        foreach (var ip in new[] { "127.0.0.1", "127.0.0.2", "127.0.0.3", "127.0.0.4" })
        {
            vm.NewServer = ip;
            Assert.True(vm.AddCompareServer());
        }
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.A);
        await vm.StartAsync();

        Assert.True(vm.ShowCompareResults);
        Assert.False(vm.ShowRecordResults);
        Assert.Equal(new[] { "127.0.0.1", "127.0.0.2", "127.0.0.3", "127.0.0.4" }, vm.CompareRows.Select(r => r.Server));
        Assert.Equal(new[] { false, false, true, false }, vm.CompareRows.Select(r => r.IsMismatch));
        Assert.Equal(1, vm.CompareRows.Count(r => r.IsFastest));
        Assert.False(vm.CompareRows[3].IsFastest);
        Assert.Equal(Loc.DnsCodeTimeout, vm.CompareRows[3].Code);
        Assert.Equal("6.6.6.6", vm.CompareRows[2].Answers);
        var fast = vm.CompareRows.Single(r => r.IsFastest);
        Assert.Contains(fast.Server, vm.Status);
        Assert.Contains(fast.Time, vm.Status);
        Assert.StartsWith("127.0.0.1\t", vm.CopyText());
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Compare_WhenNoServerAnswersTheStatusNamesThem()
    {
        using var s1 = new DnsClientTests.FakeServer(_ => null);
        using var s2 = new DnsClientTests.FakeServer(_ => null, address: IPAddress.Parse("127.0.0.2"), fixedPort: s1.Port);
        var vm = NewVm("127.0.0.1", "127.0.0.2");
        vm.Port = s1.Port;
        vm.QueryTimeout = TimeSpan.FromMilliseconds(250);
        vm.Target = "host.test";
        vm.CompareMode = true;
        vm.SelectedType = vm.TypeOptions.First(o => o.Type == DnsRecordType.A);
        await vm.StartAsync();
        Assert.Equal(2, vm.CompareRows.Count);
        Assert.DoesNotContain(vm.CompareRows, r => r.IsFastest);
        Assert.Contains("127.0.0.1, 127.0.0.2", vm.Status);
    }

    [Fact]
    public async Task Compare_WithAutomaticTypeAnswersAnIpTargetAsReverseLookup()
    {
        using var server = new DnsClientTests.FakeServer(q => DnsClientTests.Reply(q, 0, false));
        var vm = NewVm("127.0.0.1");
        vm.Port = server.Port;
        vm.Target = "192.0.2.7";
        vm.CompareMode = true;
        await vm.StartAsync();
        Assert.Single(vm.CompareRows);
        Assert.Contains(server.Questions, x => x.Type == DnsRecordType.PTR && x.Name.StartsWith("7.2.0.192.in-addr.arpa"));
    }
}
