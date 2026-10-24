using System;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ArpHelperTests
{
    [Fact]
    public void ParseArpOutput_ExtractsMac()
    {
        const string sample = "  192.168.1.1          aa-bb-cc-dd-ee-ff     dynamisch";
        var mac = ArpHelper.ParseMac(sample);
        Assert.Equal("AA-BB-CC-DD-EE-FF", mac);
    }

    [Fact]
    public void ParseArpOutput_NoMac_ReturnsNull()
        => Assert.Null(ArpHelper.ParseMac("no mac here"));

    [Fact]
    public void ParseTable_MapsEveryIpToMac()
    {
        const string sample =
            "Interface: 192.168.1.50 --- 0x5\n" +
            "  192.168.1.1           aa-bb-cc-dd-ee-ff     dynamisch\n" +
            "  192.168.2.7           11-22-33-44-55-66     dynamisch\n" +
            "  192.168.1.255         ff-ff-ff-ff-ff-ff     statisch\n";
        var map = ArpHelper.ParseTable(sample);
        Assert.Equal("AA-BB-CC-DD-EE-FF", map["192.168.1.1"]);
        Assert.Equal("11-22-33-44-55-66", map["192.168.2.7"]);   // other local range
        Assert.False(map.ContainsKey("0.0.0.0"));
    }

    [Fact]
    public void ParseNetbios_ExtractsName()
    {
        const string sample = "    DESKTOP-ABC    <20>  UNIQUE      Registered";
        var name = NetBiosHelper.ParseName(sample);
        Assert.Equal("DESKTOP-ABC", name);
    }

    [Fact]
    public async Task RunCaptureAsync_ReturnsTheToolOutput()
    {
        var output = await ArpHelper.RunCaptureAsync("hostname", "", 5000);

        Assert.Equal(System.Net.Dns.GetHostName(), output.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task LookupAsync_WithACancelledToken_ReturnsOrCancelsPromptly()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var started = System.Diagnostics.Stopwatch.StartNew();

        try { await NetBiosHelper.LookupAsync("127.0.0.1", cts.Token); }
        catch (OperationCanceledException) { }

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "took " + started.Elapsed);
    }

    [Fact]
    public async Task ResolveAsync_AnUnparsableAddress_YieldsNoMac()
        => Assert.Null(await ArpHelper.ResolveAsync("not-an-ip"));

    [Fact]
    public async Task RunCaptureAsync_KillsAToolThatRunsPastTheTimeout()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        await ArpHelper.RunCaptureAsync("ping", "-n 30 127.0.0.1", 400);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "took " + started.Elapsed);
    }
}
