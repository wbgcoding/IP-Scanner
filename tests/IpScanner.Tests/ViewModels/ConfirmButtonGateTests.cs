using System;
using System.Collections.Generic;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ConfirmButtonGateTests
{
    private sealed class ManualScheduler
    {
        public Action? Pending;
        public TimeSpan LastDelay;
        public void Schedule(Action onTimeout, TimeSpan delay) { Pending = onTimeout; LastDelay = delay; }
        public void Fire() { Pending?.Invoke(); Pending = null; }
    }

    private static (ConfirmButtonGate Gate, ManualScheduler Sched, List<bool> Log) Make(TimeSpan? timeout = null)
    {
        var sched = new ManualScheduler();
        var log = new List<bool>();
        return (new ConfirmButtonGate(log.Add, timeout, sched.Schedule), sched, log);
    }

    [Fact]
    public void FirstPress_Arms_SecondPress_ConfirmsAndDisarms()
    {
        var (gate, _, log) = Make();

        Assert.False(gate.Press());
        Assert.True(gate.IsArmed);
        Assert.True(gate.Press());
        Assert.False(gate.IsArmed);
        Assert.Equal(new[] { true, false }, log);
    }

    [Fact]
    public void Timeout_RevertsTheArmedLook_AndTheNextPressArmsAgain()
    {
        var (gate, sched, log) = Make();

        gate.Press();
        sched.Fire();

        Assert.False(gate.IsArmed);
        Assert.Equal(new[] { true, false }, log);
        Assert.False(gate.Press());
        Assert.True(gate.IsArmed);
    }

    [Fact]
    public void Reset_WhenArmed_RaisesDisarmOnce_WhenIdle_RaisesNothing()
    {
        var (gate, _, log) = Make();

        gate.Reset();
        Assert.Empty(log);

        gate.Press();
        gate.Reset();
        gate.Reset();
        Assert.Equal(new[] { true, false }, log);
        Assert.False(gate.Press());
    }

    [Fact]
    public void ConfiguredTimeout_IsPassedToTheScheduler()
    {
        var (gate, sched, _) = Make(TimeSpan.FromSeconds(4));

        gate.Press();

        Assert.Equal(TimeSpan.FromSeconds(4), sched.LastDelay);
    }

    [Fact]
    public void TwoGates_AreIndependent()
    {
        var (a, _, logA) = Make();
        var (b, _, logB) = Make();

        a.Press();

        Assert.True(a.IsArmed);
        Assert.False(b.IsArmed);
        Assert.Empty(logB);
        Assert.Single(logA);
    }
}
