using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.Core;

public class SessionPortFactsTests
{
    private sealed class Scripted : IPortStrategy
    {
        private readonly Func<int, PortResult> _answer;
        public Scripted(Func<int, PortResult> answer) => _answer = answer;
        public NetProtocol Protocol => NetProtocol.Tcp;
        public Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
            => Task.FromResult<PortResult?>(_answer(port));
    }

    // Every test owns its facts, so no scan running elsewhere in the process can touch them.
    private readonly SessionPortFacts _facts = new();

    private Task Scan(string ip, Func<int, PortResult> answer, params int[] ports)
        => PortScanEngine.RunCoreAsync(ip, ports, new Scripted(answer), Array.Empty<int>(), null, null, null,
            CancellationToken.None, null, facts: _facts);

    private static PortResult R(int port, PortState state, string reason)
        => new(port, NetProtocol.Tcp, state, reason);

    [Fact]
    public async Task OnlyResetOrRefusal_CountsAsClosed_SilenceDoesNot()
    {
        const string ip = "192.0.2.201";
        await Scan(ip, p => p switch
        {
            22 => R(p, PortState.Closed, PortReason.ConnectRefused),
            80 => R(p, PortState.Closed, PortReason.Rst),
            443 => R(p, PortState.Filtered, PortReason.NoResponse),
            445 => R(p, PortState.Filtered, PortReason.IcmpUnreachable),
            _ => R(p, PortState.Open, PortReason.Connected),
        }, 22, 80, 443, 445, 3389);

        Assert.True(_facts.IsClosed(ip, 22));
        Assert.True(_facts.IsClosed(ip, 80));
        Assert.False(_facts.IsClosed(ip, 443));
        Assert.False(_facts.IsClosed(ip, 445));
        Assert.False(_facts.IsClosed(ip, 3389));
    }

    [Fact]
    public async Task ALaterScanThatDoesNotSeeTheRefusal_ClearsTheFact()
    {
        const string ip = "192.0.2.202";
        await Scan(ip, p => R(p, PortState.Closed, PortReason.ConnectRefused), 443);
        Assert.True(_facts.IsClosed(ip, 443));
        await Scan(ip, p => R(p, PortState.Filtered, PortReason.NoResponse), 443);
        Assert.False(_facts.IsClosed(ip, 443));
    }

    [Fact]
    public void Row_IsKnownClosedOnlyAfterARefusalInThisSession_NeverFromAStoredResult()
    {
        const string ip = "192.0.2.203";
        var vm = new DeviceViewModel(new Device(ip)) { PortFacts = _facts };
        // A complete result from an older session lacking 443 (or a filtered port) proves nothing.
        vm.Services.ReplaceAll(new[] { new NetService("SSH", 22, NetProtocol.Tcp) });
        Assert.False(vm.IsPortKnownClosed(443));

        _facts.Record(new PortResult(443, NetProtocol.Tcp, PortState.Closed, PortReason.ConnectRefused) { Ip = ip });
        Assert.True(vm.IsPortKnownClosed(443));

        // Listed as open now (e.g. a later full sweep merged it in): enabled again.
        vm.Services.ReplaceAll(new[] { new NetService("HTTPS", 443, NetProtocol.Tcp) });
        Assert.False(vm.IsPortKnownClosed(443));
    }

    [Fact]
    public void OnlyPortsTheOpenWithMenuUses_AreRemembered()
    {
        const string ip = "192.0.2.204";
        _facts.Record(new PortResult(1234, NetProtocol.Tcp, PortState.Closed, PortReason.Rst) { Ip = ip });
        _facts.Record(new PortResult(443, NetProtocol.Tcp, PortState.Closed, PortReason.Rst) { Ip = ip });
        Assert.False(_facts.IsClosed(ip, 1234));
        Assert.True(_facts.IsClosed(ip, 443));
        Assert.Equal(1, _facts.Count);
    }
}
