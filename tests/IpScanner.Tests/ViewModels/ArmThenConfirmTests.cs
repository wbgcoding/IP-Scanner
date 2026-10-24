using System;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ArmThenConfirmTests
{
    private sealed class FakeEntry : IDeletable
    {
        public bool IsDeleteArmed { get; set; }
    }

    /// <summary>Captures the scheduled timeout callback instead of starting a
    /// real WPF DispatcherTimer, so the test can fire (or not fire) it on demand.</summary>
    private sealed class ManualScheduler
    {
        public Action? Pending;
        public void Schedule(Action onTimeout, TimeSpan delay) => Pending = onTimeout;
        public void Fire() { Pending?.Invoke(); Pending = null; }
    }

    [Fact]
    public void FirstClick_Arms_DoesNotConfirm()
    {
        var scheduler = new ManualScheduler();
        var arm = new ArmThenConfirm(scheduleTimeout: scheduler.Schedule);
        var entry = new FakeEntry();

        bool confirmed = arm.Confirm(entry);

        Assert.False(confirmed);
        Assert.True(entry.IsDeleteArmed);
    }

    [Fact]
    public void SecondClick_OnSameEntry_Confirms_AndDisarms()
    {
        var scheduler = new ManualScheduler();
        var arm = new ArmThenConfirm(scheduleTimeout: scheduler.Schedule);
        var entry = new FakeEntry();

        arm.Confirm(entry);            // arms
        bool confirmed = arm.Confirm(entry);   // confirms

        Assert.True(confirmed);
        Assert.False(entry.IsDeleteArmed);
    }

    [Fact]
    public void ClickOnDifferentEntry_ReArmsNewOne_DisarmsPrevious()
    {
        var scheduler = new ManualScheduler();
        var arm = new ArmThenConfirm(scheduleTimeout: scheduler.Schedule);
        var first = new FakeEntry();
        var second = new FakeEntry();

        arm.Confirm(first);             // arms first
        bool confirmed = arm.Confirm(second);   // arms second instead

        Assert.False(confirmed);
        Assert.False(first.IsDeleteArmed);
        Assert.True(second.IsDeleteArmed);
    }

    [Fact]
    public void Timeout_DisarmsWithoutASecondClick()
    {
        var scheduler = new ManualScheduler();
        var arm = new ArmThenConfirm(scheduleTimeout: scheduler.Schedule);
        var entry = new FakeEntry();

        arm.Confirm(entry);   // arms
        scheduler.Fire();     // simulate the timeout elapsing

        Assert.False(entry.IsDeleteArmed);
        // A "click" after the timeout must arm again, not confirm.
        Assert.False(arm.Confirm(entry));
        Assert.True(entry.IsDeleteArmed);
    }

    [Fact]
    public void Disarm_ClearsArmedStateDirectly()
    {
        var scheduler = new ManualScheduler();
        var arm = new ArmThenConfirm(scheduleTimeout: scheduler.Schedule);
        var entry = new FakeEntry();

        arm.Confirm(entry);
        arm.Disarm();

        Assert.False(entry.IsDeleteArmed);
        Assert.False(arm.Confirm(entry));   // back to "first click" behavior
    }
}
