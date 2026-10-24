using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Discovery;
using Xunit;

namespace IpScanner.Tests.Cli;

[Collection("Localization")]
public sealed class ScanPresetTests
{
    private static CliOptions Parse(string flags)
    {
        var parsed = CliParser.Parse(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Null(parsed.Error);
        return parsed.Options;
    }

    [Theory]
    [InlineData("", "", "192.168.2.0/24", "10.0.0.0/8", "192.168.2.0/24")]
    [InlineData("  ", null, null, "10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("", "", null, null, "192.168.1.0/24")]
    [InlineData("172.16.0.0/16", "/24", "192.168.2.0/24", "10.0.0.0/8", "172.16.0.0/16")]
    [InlineData("172.16.0.0", "/20", "192.168.2.0/24", null, "172.16.0.0/20")]
    [InlineData("not an ip", "/24", "192.168.2.0/24", null, "192.168.2.0/24")]
    public void PreviewTarget_PrefersTypedThenDetectedThenConfigured(string typed, string? cidr, string? detected, string? configured, string expected)
    {
        var configuredList = configured is null ? new List<string>() : new List<string> { configured };
        Assert.Equal(expected, ScanPresets.PreviewTarget(typed, cidr, detected, configuredList));
    }

    public static IEnumerable<object[]> BuiltInFlags() => ScanPresets.BuiltIn().Select(p => new object[] { p.Flags });

    [Theory]
    [MemberData(nameof(BuiltInFlags))]
    public void BuiltInPreset_GivesTheSameScanAsTheCommandLine(string flags)
    {
        // Command line route.
        var cli = ScanPlan.FromOptions(Parse(flags));
        var cliPorts = cli.PingOnly ? null : cli.ResolvePorts();

        // Settings route: the preset is applied to a configuration and read back from it.
        Assert.True(ScanPresets.TryParseFlags(flags, out var plan, out _));
        var config = new ScanConfig();
        plan.ApplyTo(config);
        var guiPlan = ScanPlan.FromConfig(config);
        var guiPorts = config.ExportPorts ? guiPlan.ResolvePorts() : null;

        Assert.Equal(ScanTiming.For(cli.Timing), ScanTiming.For(config.PortScanTiming));
        Assert.Equal(DiscoveryModes.Parse(cli.DiscoveryModeKeys), DiscoveryModes.Parse(config.DiscoveryModes));
        Assert.Equal(cli.PingOnly, !config.ExportPorts);
        Assert.Equal(cliPorts?.Tcp, guiPorts?.Tcp);
        Assert.Equal(cliPorts?.Udp, guiPorts?.Udp);
        if (!cli.PingOnly)
        {
            Assert.Equal(cli.ServiceDetection, config.PortServiceDetection);
            Assert.Equal(cli.OsDetection, config.OsFingerprint);
        }
        Assert.Equal(cli, guiPlan);
    }

    [Theory]
    [MemberData(nameof(BuiltInFlags))]
    public void Preview_RoundTripsToTheSamePlan(string flags)
    {
        Assert.True(ScanPresets.TryParseFlags(flags, out var plan, out _));
        var command = ScanPresets.Command(plan, "192.168.1.0/24");
        Assert.StartsWith("IP-Scanner.exe ", command);
        var parsed = CliParser.Parse(command.Split(' ').Skip(1).ToArray());
        Assert.Null(parsed.Error);
        Assert.Equal(["192.168.1.0/24"], parsed.Options.Targets);
        Assert.Equal(plan.Normalized(), ScanPlan.FromOptions(parsed.Options));
    }

    [Theory]
    [InlineData("-T4 -F", "IP-Scanner.exe -T4 -F 192.168.1.0/24")]
    [InlineData("-T3 -sV", "IP-Scanner.exe -sV 192.168.1.0/24")]
    [InlineData("-Pn -p-", "IP-Scanner.exe -Pn -p- --allow-full-scan 192.168.1.0/24")]
    [InlineData("-T1 -sn", "IP-Scanner.exe -T1 -sn 192.168.1.0/24")]
    public void Preview_ShowsTheEquivalentCommand(string flags, string expected)
    {
        Assert.True(ScanPresets.TryParseFlags(flags, out var plan, out _));
        Assert.Equal(expected, ScanPresets.Command(plan, "192.168.1.0/24"));
    }

    [Theory]
    [InlineData("-T4 -F")]
    [InlineData("-p 22,80,443 -sV")]
    [InlineData("-T2 -sn")]
    [InlineData("-T5 -p 1-1024 -sV")]
    [InlineData("-p- -sV -O")]
    public void Preview_RunsAsGiven(string flags)
    {
        Assert.True(ScanPresets.TryParseFlags(flags, out var plan, out _));
        var command = ScanPresets.Command(plan, "192.168.1.10");
        var args = command.Split(' ').Skip(1).ToList();
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, default, FakeServices(), "IP-Scanner.exe");
        Assert.True(code == CliExit.Ok, command + " -> " + code + " " + se);
        Assert.Contains("192.168.1.10", so.ToString());
    }

    [Fact]
    public void SkipDiscoveryWithoutPorts_GivesARunnableCommandAndEntry()
    {
        // The settings allow skipping discovery while exporting no ports; the command line refuses -Pn with -sn.
        var config = new ScanConfig { DiscoveryModes = "skip", ExportPorts = false };
        var plan = ScanPlan.FromConfig(config);
        var command = ScanPresets.Command(plan, "192.168.1.10");
        Assert.DoesNotContain("-Pn", command);
        var args = command.Split(' ').Skip(1).ToList();
        int code = CliApp.Run(args, new StringWriter(), new StringWriter(), default, FakeServices(), "IP-Scanner.exe");
        Assert.Equal(CliExit.Ok, code);
        Assert.True(ScanPresets.TryParseEntry(ScanPresets.ToEntry("Mine", plan), out _));
    }

    [Fact]
    public void ChosenPortsAboveTheGate_CarryTheAllowFlag()
    {
        Assert.True(ScanPresets.TryParseFlags("-p 1-1024", out var plan, out _));
        Assert.Contains("--allow-full-scan", ScanPresets.Command(plan, "192.168.1.10"));
        Assert.True(ScanPresets.TryParseFlags("-p 22,80,443", out var small, out _));
        Assert.DoesNotContain("--allow-full-scan", ScanPresets.Command(small, "192.168.1.10"));
    }

    [Fact]
    public void StoredEntry_RoundTripsThroughTheParser()
    {
        Assert.True(ScanPresets.TryParseFlags("-T4 -p 22,80 -sV -O", out var plan, out _));
        var entry = ScanPresets.ToEntry("Office", plan);
        Assert.True(ScanPresets.TryParseEntry(entry, out var preset));
        Assert.Equal("Office", preset.Name);
        Assert.False(preset.BuiltIn);
        Assert.Equal(plan, ScanPresets.PlanOf(preset));
    }

    [Theory]
    [InlineData("-T4 -F 192.168.1.1")]
    [InlineData("-sL")]
    [InlineData("-sU")]
    [InlineData("-oJ out.json")]
    [InlineData("--unknown")]
    [InlineData("-p 22,U:53")]
    [InlineData("--max-rate 10")]
    public void Flags_ThatAreNotPartOfAPreset_AreRejected(string flags)
    {
        Assert.False(ScanPresets.TryParseFlags(flags, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData("| -T4")]
    [InlineData("x | --unknown")]
    public void BrokenEntries_AreSkipped(string entry)
    {
        Assert.False(ScanPresets.TryParseEntry(entry, out _));
        var config = new ScanConfig { ScanPresets = [entry, "Ok | -T4 -F"] };
        Assert.Equal(ScanPresets.BuiltIn().Count + 1, ScanPresets.All(config).Count);
    }

    [Fact]
    public void Match_FindsTheBuiltInAndOtherwiseCustom()
    {
        var config = new ScanConfig();
        Assert.True(ScanPresets.TryParseFlags("-T4 -F", out var quick, out _));
        quick.ApplyTo(config);
        Assert.Equal(ScanPresets.BuiltIn()[0].Name, ScanPresets.Match(config)!.Name);

        config.PortScanTiming = 2;
        Assert.Null(ScanPresets.Match(config));

        config.ScanPresets.Add(ScanPresets.ToEntry("Mine", ScanPlan.FromConfig(config)));
        Assert.Equal("Mine", ScanPresets.Match(config)!.Name);
    }

    [Fact]
    public void ApplyTo_KeepsTheOtherDiscoveryModes()
    {
        var config = new ScanConfig { DiscoveryModes = "icmp,arp" };
        var plan = ScanPlan.FromConfig(config);
        plan.ApplyTo(config);
        Assert.Equal(DiscoveryModes.Parse("icmp,arp"), DiscoveryModes.Parse(config.DiscoveryModes));
        (plan with { SkipDiscovery = true }).ApplyTo(config);
        Assert.Equal("skip", config.DiscoveryModes);
        plan.ApplyTo(config);
        Assert.DoesNotContain("skip", config.DiscoveryModes);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a|b", false)]
    [InlineData("a # b", false)]
    [InlineData("a;b", false)]
    [InlineData("Same", false)]
    [InlineData("same", false)]
    [InlineData("Office scan", true)]
    public void ValidateName_AcceptsOnlyGoodNames(string name, bool valid)
    {
        Assert.Equal(valid, ScanPresets.ValidateName(name, ["Same"]) is null);
    }

    [Fact]
    public void ValidateName_LimitsTheLength()
    {
        Assert.NotNull(ScanPresets.ValidateName(new string('x', ScanPresets.MaxNameLength + 1), []));
        Assert.Null(ScanPresets.ValidateName(new string('x', ScanPresets.MaxNameLength), []));
    }

    private static CliServices FakeServices() => new()
    {
        Ping = (_, _) => new PingResult(true, 1.0, 64),
        PortRunner = (ip, spec, _, _, _, onResult) =>
        {
            var list = new List<PortResult>();
            foreach (var p in spec.Tcp.Take(5))
            {
                var r = new PortResult(p, NetProtocol.Tcp, PortState.Closed, PortReason.Rst, null);
                list.Add(r);
                onResult?.Invoke(r);
            }
            return Task.FromResult<IReadOnlyList<PortResult>>(list);
        },
        CheckRunner = (_, _, _, _) => Task.FromResult<IReadOnlyList<CheckResult>>([]),
        Network = _ => new NetworkInfo { Ip = "192.168.1.5", SubnetMask = "255.255.255.0", Gateway = "192.168.1.1", Interface = "Test" },
        Cards = () => [new NetworkDetector.NetworkCard("Ethernet", "192.168.1.5", "255.255.255.0")],
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
        Availability = _ => (true, null),
    };
}
