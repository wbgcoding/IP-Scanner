using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using Xunit;

namespace IpScanner.Tests.Cli;

[Collection("Localization")]
public sealed class CliAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ips-cli-" + Guid.NewGuid().ToString("N"));

    public CliAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly HashSet<string> Up = ["192.168.1.1", "192.168.1.2", "192.168.1.10", "2001:db8::10", "2001:db8::11"];

    private static PortResult Port(int port, PortState state, string reason, string? service = null) =>
        new(port, NetProtocol.Tcp, state, reason, service is null ? null : new NetService(service, port, NetProtocol.Tcp));

    private static CliServices Fake(CliPortRunner? ports = null, Func<ScanType, (bool, string?)>? availability = null,
        Action<string>? pinged = null, CliCheckRunner? checks = null) => new()
    {
        Ping = (ip, _) =>
        {
            pinged?.Invoke(ip);
            return Up.Contains(ip) ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        },
        PortRunner = ports ?? ((ip, spec, _, _, _, onResult) =>
        {
            var list = new List<PortResult>();
            foreach (var p in spec.Tcp)
            {
                var r = p switch
                {
                    22 => Port(22, PortState.Open, PortReason.SynAck, "SSH"),
                    80 => Port(80, PortState.Open, PortReason.SynAck, "HTTP"),
                    _ => Port(p, PortState.Closed, PortReason.Rst),
                };
                list.Add(r);
                onResult?.Invoke(r);
            }
            return Task.FromResult<IReadOnlyList<PortResult>>(list);
        }),
        CheckRunner = checks ?? ((_, _, _, _) => Task.FromResult<IReadOnlyList<CheckResult>>([])),
        Network = _ => new NetworkInfo { Ip = "192.168.1.5", SubnetMask = "255.255.255.0", Gateway = "192.168.1.1", Interface = "Test" },
        Cards = () => [new NetworkDetector.NetworkCard("Ethernet", "192.168.1.5", "255.255.255.0")],
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
        Availability = availability ?? (_ => (true, null)),
    };

    private (int Code, string Out, string Err) Run(string line, CliServices? services = null, CancellationToken ct = default,
        string program = "IP-Scanner")
    {
        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, ct, services ?? Fake(), program);
        return (code, so.ToString(), se.ToString());
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void PingSweep_PrintsLiveHostsAndExitsZero()
    {
        var (code, output, _) = Run("-sn 192.168.1.0/29");
        Assert.Equal(CliExit.Ok, code);
        Assert.Contains("192.168.1.1", output);
        Assert.Contains("192.168.1.2", output);
    }

    [Fact]
    public void PortScan_WritesValidJson()
    {
        var file = Path_("out.json");
        var (code, _, err) = Run("-p 22,80,443 -oJ " + file + " 192.168.1.10");
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal("", err);
        var report = ReportWriters.ParseJson(File.ReadAllText(file));
        Assert.NotNull(report);
        var host = Assert.Single(report!.Hosts, h => h.Ip == "192.168.1.10");
        Assert.Contains(host.Ports, p => p.Port == 22 && p.State == "open");
        Assert.Contains(host.Ports, p => p.Port == 443 && p.State == "closed");
        Assert.False(report.Meta.Partial);
        Assert.Contains("-p 22,80,443", report.Meta.Command);
    }

    [Fact]
    public void OpenFlag_DropsClosedPorts()
    {
        var file = Path_("open.json");
        Assert.Equal(CliExit.Ok, Run("-p 22,80,443 --open -oJ " + file + " 192.168.1.10").Code);
        var host = ReportWriters.ParseJson(File.ReadAllText(file))!.Hosts.Single(h => h.Ip == "192.168.1.10");
        Assert.Equal(new[] { 22, 80 }, host.Ports.Select(p => p.Port).ToArray());
    }

    [Fact]
    public void DashSendsThatFormatToStdout()
    {
        var (code, output, _) = Run("-p 22 -oJ - 192.168.1.10");
        Assert.Equal(CliExit.Ok, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void AllFormats_AreWrittenInOneRun()
    {
        var n = Path_("a.txt");
        var x = Path_("a.xml");
        var j = Path_("a.json");
        var g = Path_("a.gnmap");
        Assert.Equal(CliExit.Ok, Run("-p 22 -oN " + n + " -oX " + x + " -oJ " + j + " -oG " + g + " 192.168.1.10").Code);
        foreach (var f in new[] { n, x, j, g }) Assert.True(new FileInfo(f).Length > 0, f);
        Assert.StartsWith("<?xml", File.ReadAllText(x));
    }

    [Fact]
    public void Help_ListsEveryFlagAndExitsZero()
    {
        var (code, output, _) = Run("--help");
        Assert.Equal(CliExit.Ok, code);
        foreach (var f in CliFlags.All) Assert.Contains(f.Display, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_IsPrinted()
    {
        var (code, output, _) = Run("--version");
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal(CliHelp.Version(), output.Trim());
    }

    [Theory]
    [InlineData("--bogus 192.168.1.10")]
    [InlineData("-p 22")]
    [InlineData("-p 22 2001:db8::10")]
    [InlineData("-6 -p 22 192.168.1.10")]
    [InlineData("-p 22 not..a..host!")]
    public void UsageErrors_ExitTwo(string line)
    {
        var (code, output, err) = Run(line);
        Assert.Equal(CliExit.UsageError, code);
        Assert.Equal("", output);
        Assert.Contains("--help", err);
    }

    [Theory]
    [InlineData("-p 22 2001:db8::10", "-6")]
    [InlineData("-6 -p 22 192.168.1.10", "IPv6")]
    [InlineData("-6 -sF --allow-aggressive -p 22 2001:db8::10", "IPv6")]
    [InlineData("-6 -O -p 22 2001:db8::10", "IPv6")]
    public void Ipv6Rules_NameTheFlagOrTheLimit(string line, string expected)
    {
        var (code, _, err) = Run(line);
        Assert.Equal(CliExit.UsageError, code);
        Assert.Contains(expected, err);
    }

    [Fact]
    public void Ipv6Scan_ReportsTheHostUnderItsShortAddress()
    {
        var file = Path_("v6.json");
        var (code, _, err) = Run("-6 -p 22,80 -oJ " + file + " 2001:db8:0:0:0:0:0:10,2001:db8::12");
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal("", err);
        var report = ReportWriters.ParseJson(File.ReadAllText(file));
        var host = Assert.Single(report!.Hosts);
        Assert.Equal("2001:db8::10", host.Ip);
        Assert.Contains(host.Ports, p => p.Port == 22 && p.State == "open");
    }

    [Fact]
    public void Ipv6Scan_SortsAddressesNumerically()
    {
        var (code, output, _) = Run("-6 -sn 2001:db8::11,2001:db8::10");
        Assert.Equal(CliExit.Ok, code);
        Assert.True(output.IndexOf("2001:db8::10 ", StringComparison.Ordinal) is var a and > 0 && a < output.IndexOf("2001:db8::11 ", StringComparison.Ordinal), output);
    }

    [Fact]
    public void UnknownFlag_SuggestsTheNearestOne()
    {
        var (code, _, err) = Run("--top-port 5 192.168.1.10");
        Assert.Equal(CliExit.UsageError, code);
        Assert.Contains("--top-ports", err);
    }

    [Theory]
    [InlineData("-p- 192.168.1.10", "--allow-full-scan")]
    [InlineData("-sF 192.168.1.10", "--allow-aggressive")]
    [InlineData("--checks intrusive 192.168.1.10", "--allow-intrusive")]
    [InlineData("-sn 10.0.0.0/8", "--allow-large")]
    public void SafetyGates_NeedTheirFlag(string line, string flag)
    {
        var (code, _, err) = Run(line);
        Assert.Equal(CliExit.UsageError, code);
        Assert.Contains(flag, err);
    }

    [Theory]
    [InlineData("-p 1-65534")]
    [InlineData("-p 2-65535")]
    [InlineData("-p 1-65535")]
    [InlineData("-p 1-101")]
    [InlineData("--top-ports 1001")]
    [InlineData("-sS -sU -p 1-51")]
    public void LargeChosenPortSelection_NeedsAllowFullScan(string ports)
    {
        var (code, output, err) = Run(ports + " 192.168.1.10");
        Assert.Equal(CliExit.UsageError, code);
        Assert.Equal("", output);
        Assert.Contains("--allow-full-scan", err);
        Assert.Equal(CliExit.Ok, Run(ports + " --allow-full-scan 192.168.1.10").Code);
    }

    [Theory]
    [InlineData("-p 22,80,443")]
    [InlineData("-p 1-100")]
    [InlineData("--top-ports 100")]
    [InlineData("--top-ports 101")]
    [InlineData("--top-ports 1000")]
    [InlineData("-F")]
    [InlineData("")]
    public void SmallOrDefaultPortSelection_NeedsNoFlag(string ports)
    {
        var (code, _, err) = Run((ports + " 192.168.1.10").Trim());
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal("", err);
    }

    [Fact]
    public void UnavailableScanType_ExitsOneWithTheReason()
    {
        var (code, _, err) = Run("-sS 192.168.1.10", Fake(availability: _ => (false, "no packet driver")));
        Assert.Equal(CliExit.ScanError, code);
        Assert.Contains("no packet driver", err);
    }

    [Fact]
    public void ListMode_SendsNothing()
    {
        var pinged = new List<string>();
        var (code, output, _) = Run("-sL 192.168.1.0/30", Fake(pinged: pinged.Add));
        Assert.Equal(CliExit.Ok, code);
        Assert.Empty(pinged);
        Assert.Contains("192.168.1.1", output);
    }

    [Fact]
    public void UnknownInterface_ExitsTwo()
    {
        var (code, _, err) = Run("--interface nope 192.168.1.10");
        Assert.Equal(CliExit.UsageError, code);
        Assert.Contains("nope", err);
    }

    [Fact]
    public void UnwritableOutput_ExitsOne()
    {
        File.WriteAllText(Path_("blocker"), "a file where a folder should be");
        var bad = Path.Combine(_dir, "blocker", "x.json");
        var (code, _, err) = Run("-p 22 -oJ " + bad + " 192.168.1.10");
        Assert.Equal(CliExit.ScanError, code);
        Assert.Contains("cannot write", err);
    }

    [Fact]
    public void Cancel_StillWritesThePartialResult()
    {
        using var cts = new CancellationTokenSource();
        var services = Fake(ports: (ip, spec, _, _, ct, onResult) =>
        {
            onResult?.Invoke(Port(22, PortState.Open, PortReason.SynAck, "SSH"));
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });
        var file = Path_("partial.json");
        var (code, _, err) = Run("-p 22,80 -oJ " + file + " 192.168.1.10", services, cts.Token);
        Assert.Equal(CliExit.Cancelled, code);
        Assert.Contains("partial", err, StringComparison.OrdinalIgnoreCase);
        var report = ReportWriters.ParseJson(File.ReadAllText(file))!;
        Assert.True(report.Meta.Partial);
        Assert.Contains(report.Hosts.Single(h => h.Ip == "192.168.1.10").Ports, p => p.Port == 22 && p.State == "open");
    }

    [Fact]
    public void CheckRunner_ReceivesTheOpenServices()
    {
        var seen = new List<string>();
        var services = Fake(checks: (ip, list, _, _) =>
        {
            seen.AddRange(list.Select(s => ip + ":" + s.Port));
            return Task.FromResult<IReadOnlyList<CheckResult>>([]);
        });
        var (code, _, _) = Run("-p 22,80,443 --checks safe 192.168.1.10", services);
        Assert.Equal(CliExit.Ok, code);
        Assert.Equal(new[] { "192.168.1.10:22", "192.168.1.10:80" }, seen.OrderBy(s => s).ToArray());
    }

    private sealed class ClosedPipeWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new IOException("The pipe is being closed.");
        public override void Write(string? value) => throw new IOException("The pipe is being closed.");
        public override void WriteLine(string? value) => throw new IOException("The pipe is being closed.");
        public override void Flush() => throw new IOException("The pipe is being closed.");
    }

    [Fact]
    public void ThrowingPortRunnerForOneHost_KeepsTheOtherHostsAndTheReport()
    {
        var services = Fake(ports: (ip, spec, _, _, _, onResult) =>
        {
            if (ip == "192.168.1.2") throw new System.Net.Sockets.SocketException(10013);
            var r = Port(22, PortState.Open, PortReason.SynAck, "SSH");
            onResult?.Invoke(r);
            return Task.FromResult<IReadOnlyList<PortResult>>([r]);
        });
        var file = Path_("hostfail.json");
        var (code, _, err) = Run("-p 22 -oJ " + file + " 192.168.1.1-10", services);
        Assert.Equal(CliExit.Ok, code);
        Assert.Contains("192.168.1.2", err);
        Assert.Contains("Warning", err);
        var report = ReportWriters.ParseJson(File.ReadAllText(file))!;
        Assert.Contains(report.Hosts.Single(h => h.Ip == "192.168.1.10").Ports, p => p.Port == 22);
    }

    [Fact]
    public void ThrowingCheckRunnerForOneHost_KeepsTheReport()
    {
        var services = Fake(checks: (ip, _, _, _) =>
            ip == "192.168.1.10" ? throw new InvalidDataException("bad banner") : Task.FromResult<IReadOnlyList<CheckResult>>([]));
        var file = Path_("checkfail.json");
        var (code, _, err) = Run("-p 22 --checks safe -oJ " + file + " 192.168.1.10", services);
        Assert.Equal(CliExit.Ok, code);
        Assert.Contains("192.168.1.10: service checks failed", err);
        Assert.NotNull(ReportWriters.ParseJson(File.ReadAllText(file)));
    }

    [Fact]
    public void ClosedStdout_WritesTheFilesFirstAndEndsQuietly()
    {
        var file = Path_("piped.json");
        var se = new StringWriter();
        int code = CliApp.Run(["-p", "22", "-oJ", file, "192.168.1.10"], new ClosedPipeWriter(), se, default, Fake(), "IP-Scanner");
        Assert.Equal(CliExit.OutputClosed, code);
        Assert.Equal("", se.ToString());
        Assert.NotNull(ReportWriters.ParseJson(File.ReadAllText(file)));
    }

    [Fact]
    public void Help_ExplainsHowToWaitForTheWindowExe()
    {
        var help = Run("--help").Out;
        Assert.Contains("start /wait", help);
        Assert.Contains("Out-Host", help);
        Assert.Contains("Out-File", help);
        Assert.Contains("cannot be combined with `>`", help);
        Assert.DoesNotContain("works as usual", help);
        Assert.DoesNotContain("ipscan-cli", help);
        Assert.StartsWith("IP-Scanner ", help);
    }

    [Fact]
    public void UnwritableOutput_FailsBeforeTheScanStarts()
    {
        File.WriteAllText(Path_("blocker2"), "a file where a folder should be");
        var bad = Path.Combine(_dir, "blocker2", "x.json");
        bool scanned = false;
        var services = Fake(ports: (ip, spec, _, _, _, onResult) =>
        {
            scanned = true;
            return Task.FromResult<IReadOnlyList<PortResult>>([]);
        });
        var (code, _, err) = Run("-p 22 -oJ " + bad + " 192.168.1.10", services);
        Assert.Equal(CliExit.ScanError, code);
        Assert.Contains("cannot write", err);
        Assert.False(scanned);
    }

    [Fact]
    public void OutputPathThatIsAFolder_FailsBeforeTheScanStarts()
    {
        var (code, _, err) = Run("-p 22 -oJ " + _dir + " 192.168.1.10");
        Assert.Equal(CliExit.ScanError, code);
        Assert.Contains("cannot write", err);
    }

    [Fact]
    public void ExistingReport_IsReplacedAndNoTempFileStaysBehind()
    {
        var file = Path_("replace.json");
        File.WriteAllText(file, "old");
        var (code, _, _) = Run("-p 22 -oJ " + file + " 192.168.1.10");
        Assert.Equal(CliExit.Ok, code);
        Assert.NotNull(ReportWriters.ParseJson(File.ReadAllText(file)));
        Assert.Empty(Directory.GetFiles(_dir, ".*.tmp"));
    }
}
