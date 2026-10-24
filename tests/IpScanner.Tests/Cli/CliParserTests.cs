using System;
using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Cli;

public class CliParserTests
{
    private static CliParseResult Parse(string line) =>
        CliParser.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static CliOptions Ok(string line)
    {
        var r = Parse(line);
        Assert.True(r.Ok, r.Error);
        return r.Options;
    }

    [Fact]
    public void PortsAndJsonOutput_AreRead()
    {
        var o = Ok("-p 22,80 -oJ out.json 192.168.1.10");
        Assert.Equal("22,80", o.Ports);
        Assert.Equal("out.json", o.Outputs[ReportFormat.Json]);
        Assert.Equal(["192.168.1.10"], o.Targets);
        Assert.Equal(CliMode.Scan, o.Mode);
    }

    [Theory]
    [InlineData("-p22 10.0.0.1", "22")]
    [InlineData("-p- 10.0.0.1", "-")]
    [InlineData("--ports=1-100 10.0.0.1", "1-100")]
    public void PortValue_CanBeAttachedOrAssigned(string line, string expected) =>
        Assert.Equal(expected, Ok(line).Ports);

    [Fact]
    public void TimingTemplate_CanBeAttached()
    {
        Assert.Equal(4, Ok("-T4 10.0.0.1").Timing);
        Assert.Equal(2, Ok("-T 2 10.0.0.1").Timing);
        Assert.NotNull(Parse("-T9 10.0.0.1").Error);
    }

    [Fact]
    public void UnknownFlag_IsAnErrorWithASuggestion()
    {
        var r = Parse("--top-port 10 10.0.0.1");
        Assert.False(r.Ok);
        Assert.Contains("Did you mean '--top-ports'", r.Error);
        var far = Parse("--zzzzzzzz 10.0.0.1");
        Assert.False(far.Ok);
        Assert.Contains("--help", far.Error);
    }

    [Fact]
    public void ConflictingOptions_AreRejected()
    {
        Assert.NotNull(Parse("-sT -sS 10.0.0.1").Error);
        Assert.NotNull(Parse("-sn -p 22 10.0.0.1").Error);
        Assert.NotNull(Parse("-sL -sV 10.0.0.1").Error);
        Assert.NotNull(Parse("-F -p 22 10.0.0.1").Error);
        Assert.NotNull(Parse("--min-rate 100 --max-rate 10 10.0.0.1").Error);
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("750", 750_000)]
    public void HostTimeout_AcceptsUnits(string text, int ms) =>
        Assert.Equal(ms, Ok("--host-timeout " + text + " 10.0.0.1").HostTimeoutMs);

    [Fact]
    public void HostTimeout_RejectsGarbage() =>
        Assert.NotNull(Parse("--host-timeout soon 10.0.0.1").Error);

    [Fact]
    public void ScanTypes_SetTheTransport()
    {
        Assert.Equal(ScanType.Syn, Ok("-sS 10.0.0.1").TcpType);
        Assert.True(Ok("-sU 10.0.0.1").Udp);
        Assert.Null(Ok("-sU 10.0.0.1").TcpType);
        var both = Ok("-sT -sU 10.0.0.1");
        Assert.True(both.Udp);
        Assert.Equal(ScanType.Connect, both.TcpType);
    }

    [Fact]
    public void ModesAndSwitches_AreRead()
    {
        Assert.Equal(CliMode.Ping, Ok("-sn 10.0.0.0/24").Mode);
        Assert.Equal(CliMode.List, Ok("-sL 10.0.0.0/30").Mode);
        var o = Ok("-Pn -sV -O --open --reason -vv -d --allow-full-scan 10.0.0.1");
        Assert.True(o.SkipDiscovery && o.ServiceDetection && o.OsDetection && o.OpenOnly && o.Reason && o.Debug && o.AllowFullScan);
        Assert.Equal(2, o.Verbosity);
    }

    [Fact]
    public void SeveralTargets_AreCollected() =>
        Assert.Equal(["10.0.0.1", "10.0.1.0/24", "host.example"], Ok("10.0.0.1 10.0.1.0/24 host.example").Targets);

    [Fact]
    public void HelpAndVersion_NeedNoTarget()
    {
        Assert.True(Ok("--help").Help);
        Assert.True(Ok("-h").Help);
        Assert.True(Ok("--version").Version);
    }

    [Fact]
    public void MissingValue_IsAnError()
    {
        Assert.NotNull(Parse("10.0.0.1 -p").Error);
        Assert.NotNull(Parse("10.0.0.1 -oJ").Error);
    }

    private static string Sample(CliFlag f) => f.Id switch
    {
        "iL" or "exclude-file" => "list.txt",
        "exclude" => "10.0.0.2",
        "p" => "22,80",
        "top-ports" => "20",
        "version-intensity" => "5",
        "checks" => "safe,discovery",
        "T" => "3",
        "min-rate" or "max-rate" or "max-retries" => "5",
        "host-timeout" => "30s",
        "oN" or "oX" or "oJ" or "oG" => "out.txt",
        "interface" => "192.168.1.5",
        _ => "x"
    };

    [Fact]
    public void EveryFlagInTheTable_ParsesAlone()
    {
        foreach (var f in CliFlags.All)
        {
            foreach (var name in f.Names)
            {
                var args = new List<string> { name };
                if (f.Arg != CliArg.None) args.Add(Sample(f));
                args.Add("10.0.0.1");
                var r = CliParser.Parse(args);
                Assert.True(r.Ok, $"{string.Join(' ', args)}: {r.Error}");
            }
        }
    }

    [Fact]
    public void FlagTable_HasUniqueNames()
    {
        var names = CliFlags.All.SelectMany(f => f.Names).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(CliFlags.All, f => Assert.Contains(f.Group, CliFlags.Groups));
    }

    [Fact]
    public void HelpExamples_AllParse()
    {
        Assert.NotEmpty(CliHelp.Examples);
        foreach (var (args, _) in CliHelp.Examples)
        {
            var r = Parse(args);
            Assert.True(r.Ok, $"{args}: {r.Error}");
        }
    }

    [Fact]
    public void HelpText_ListsEveryFlagTheExamplesAndTheExitCodes()
    {
        var text = CliHelp.Text("IP-Scanner");
        foreach (var f in CliFlags.All) Assert.Contains(f.Display, text, StringComparison.Ordinal);
        foreach (var (args, _) in CliHelp.Examples) Assert.Contains("IP-Scanner " + args, text, StringComparison.Ordinal);
        foreach (var (code, _) in CliExit.Table) Assert.Contains($"{code,3}  ", text, StringComparison.Ordinal);
        Assert.Contains("Exit codes:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksList_ReadsCategories()
    {
        var sel = CliChecks.Parse("safe,intrusive");
        Assert.NotNull(sel);
        Assert.True(sel!.Value.Safe);
        Assert.True(sel.Value.Intrusive);
        Assert.False(sel.Value.Discovery);
        Assert.Null(CliChecks.Parse("bogus"));
        Assert.Null(CliChecks.Parse(""));
    }

    [Fact]
    public void TwoOutputsToTheSameFile_AreRejected()
    {
        Assert.NotNull(Parse("-oJ same.out -oN SAME.out 192.168.1.10").Error);
        Assert.NotNull(Parse("-oJ a/../same.out -oN same.out 192.168.1.10").Error);
        Assert.True(Parse("-oJ one.out -oN two.out 192.168.1.10").Ok);
    }

    [Fact]
    public void ValueThatLooksLikeAnOption_IsRefusedUnlessWrittenWithEquals()
    {
        var r = Parse("-oN -sV 10.0.0.1");
        Assert.False(r.Ok);
        Assert.Contains("-sV", r.Error);
        Assert.Equal("-x.txt", Ok("-oN=-x.txt 10.0.0.1").Outputs[ReportFormat.Text]);
        Assert.Equal("-", Ok("-oN - 10.0.0.1").Outputs[ReportFormat.Text]);
    }

    [Theory]
    [InlineData("-sn -sL 10.0.0.1")]
    [InlineData("-sL -sn 10.0.0.1")]
    [InlineData("-sL -p 80 10.0.0.1")]
    [InlineData("-sL -F 10.0.0.1")]
    [InlineData("-sL --top-ports 10 10.0.0.1")]
    [InlineData("-Pn -sn 10.0.0.1")]
    public void ConflictingModes_AreUsageErrors(string line) => Assert.False(Parse(line).Ok);

    [Fact]
    public void RepeatedVerbose_IsCounted()
    {
        Assert.Equal(2, Ok("-v -v 10.0.0.1").Verbosity);
        Assert.Equal(3, Ok("-vvv 10.0.0.1").Verbosity);
        Assert.Equal(1, Ok("-v 10.0.0.1").Verbosity);
    }

    [Theory]
    [InlineData("99999999999999999h")]
    [InlineData("9999999999999999999999")]
    [InlineData("600000h")]
    public void HugeDuration_IsRejected(string text) => Assert.False(CliParser.TryDuration(text, out _));
}
