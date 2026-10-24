using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using Xunit;

namespace IpScanner.Tests.Cli;

[Collection("Localization")]
public sealed class CliWarningTests
{
    private static CliServices Fake() => new()
    {
        Ping = (ip, _) => new PingResult(ip == "192.0.2.10", 1.0, 64),
        PortRunner = (ip, spec, _, _, _, onResult) =>
        {
            var list = spec.Tcp.Select(p => new PortResult(p, NetProtocol.Tcp, PortState.Closed, PortReason.Rst, null)).ToList();
            return Task.FromResult<IReadOnlyList<PortResult>>(list);
        },
        CheckRunner = (_, _, _, _) => Task.FromResult<IReadOnlyList<CheckResult>>([]),
        Network = _ => new NetworkInfo { Ip = "192.0.2.5", SubnetMask = "255.255.255.0", Gateway = "192.0.2.1", Interface = "Test" },
        Cards = () => [],
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
        Availability = _ => (true, null),
    };

    private static (int Code, string Out, string Err) Run(string line)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(), so, se, CancellationToken.None, Fake(), "IP-Scanner");
        return (code, so.ToString(), se.ToString());
    }

    private static string WithoutCommandLine(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("Command line:", StringComparison.Ordinal)));

    private static int Count(string text, string needle) =>
        text.Split(needle).Length - 1;

    [Theory]
    [InlineData("-sF --allow-aggressive 192.0.2.10")]
    [InlineData("-T4 192.0.2.10")]
    [InlineData("-T5 192.0.2.10")]
    [InlineData("-p 1-65535 --allow-full-scan 192.0.2.10")]
    [InlineData("--top-ports 500 --allow-full-scan 192.0.2.10")]
    [InlineData("--checks intrusive --allow-intrusive 192.0.2.10")]
    public void AggressiveRun_WarnsOnceOnStderrAndStillRuns(string line)
    {
        var (code, output, err) = Run(line);
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal(1, Count(err, "Warning: this run is aggressive"));
        Assert.Contains("--i-know-what-i-am-doing", err);
        Assert.DoesNotContain("Warning", output);
    }

    [Theory]
    [InlineData("-sF --allow-aggressive")]
    [InlineData("-T4")]
    [InlineData("-p 1-65535 --allow-full-scan")]
    [InlineData("--checks intrusive --allow-intrusive")]
    public void Flag_HidesTheWarningAndKeepsTheOutput(string line)
    {
        var with = Run(line + " --i-know-what-i-am-doing 192.0.2.10");
        var without = Run(line + " 192.0.2.10");
        Assert.Equal(CliExit.Ok, with.Code);
        Assert.DoesNotContain("Warning", with.Err);
        Assert.Equal(WithoutCommandLine(without.Out), WithoutCommandLine(with.Out));
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("-sn 192.0.2.0/28")]
    [InlineData("-T3 -p 22,80 192.0.2.10")]
    [InlineData("-F -sV 192.0.2.10")]
    [InlineData("--top-ports 50 192.0.2.10")]
    public void CarefulRun_PrintsNoWarning(string line)
    {
        var (code, _, err) = Run(line);
        Assert.Equal(CliExit.Ok, code);
        Assert.DoesNotContain("Warning", err);
    }

    [Fact]
    public void RefusedRun_GivesTheGateErrorNotTheWarning()
    {
        var (code, _, err) = Run("-sF 192.0.2.10");
        Assert.Equal(CliExit.UsageError, code);
        Assert.DoesNotContain("this run is aggressive", err);
    }

    [Fact]
    public void FlagIsInTheTableAndHelp()
    {
        Assert.Contains(CliFlags.All, f => f.Id == "i-know-what-i-am-doing");
        Assert.Contains("--i-know-what-i-am-doing", CliHelp.Text("IP-Scanner"));
    }
}
