using System;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class OfflineAlertTrackerTests
{
    /// <summary>A tracker that already saw its first reply, as every watched device does once it is online.</summary>
    private static OfflineAlertTracker Armed()
    {
        var t = new OfflineAlertTracker();
        t.Observe(true, 0);
        return t;
    }

    [Fact]
    public void LossesBeforeTheFirstReply_RaiseNothing_ThenTheNormalCycleStarts()
    {
        var t = new OfflineAlertTracker();
        for (int i = 0; i < 10; i++) Assert.Null(t.Observe(false, i * 1000));
        Assert.False(t.IsDown);
        Assert.Null(t.Observe(true, 11_000));
        Assert.Null(t.Observe(false, 12_000));
        Assert.Null(t.Observe(false, 13_000));
        Assert.Equal(OfflineAlertKind.Lost, t.Observe(false, 14_000)!.Value.Kind);
    }

    [Fact]
    public void Device_OfflineAtWatchStart_RaisesNoAlert()
    {
        var d = new Device("192.168.1.9") { AlertOffline = true };
        for (int i = 0; i < 6; i++) d.RecordPing(new PingResult(false, null, null));
        Assert.False(d.TryTakeAlert(out _));
    }

    [Fact]
    public void ThreeLostPingsInARow_RaiseOneLostAlert()
    {
        var t = Armed();
        Assert.Null(t.Observe(false, 0));
        Assert.Null(t.Observe(false, 1000));
        var alert = t.Observe(false, 2000);
        Assert.Equal(OfflineAlertKind.Lost, alert!.Value.Kind);
        Assert.True(t.IsDown);
    }

    [Fact]
    public void FurtherLostPings_DoNotRepeatTheAlert()
    {
        var t = Armed();
        for (int i = 0; i < 3; i++) t.Observe(false, i * 1000);
        for (int i = 3; i < 20; i++) Assert.Null(t.Observe(false, i * 1000));
    }

    [Fact]
    public void AReplyBetweenLosses_RestartsTheCount()
    {
        var t = Armed();
        t.Observe(false, 0);
        t.Observe(false, 1000);
        Assert.Null(t.Observe(true, 2000));
        Assert.Null(t.Observe(false, 3000));
        Assert.Null(t.Observe(false, 4000));
        Assert.False(t.IsDown);
        Assert.NotNull(t.Observe(false, 5000));
    }

    [Fact]
    public void FirstReplyAfterTheAlert_RaisesBackWithTheTimeSinceTheFirstLoss()
    {
        var t = Armed();
        t.Observe(false, 10_000);
        t.Observe(false, 11_000);
        t.Observe(false, 12_000);
        var back = t.Observe(true, 70_000);
        Assert.Equal(OfflineAlertKind.Back, back!.Value.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), back.Value.Down);
        Assert.False(t.IsDown);
        Assert.Null(t.Observe(true, 71_000));
    }

    [Fact]
    public void ReplyWithoutAnEarlierAlert_RaisesNothing()
    {
        var t = Armed();
        Assert.Null(t.Observe(true, 0));
        t.Observe(false, 1000);
        Assert.Null(t.Observe(true, 2000));
    }

    [Fact]
    public void ASecondOutage_AlertsAgain()
    {
        var t = Armed();
        for (int i = 0; i < 3; i++) t.Observe(false, i * 1000);
        t.Observe(true, 5000);
        t.Observe(false, 6000);
        t.Observe(false, 7000);
        Assert.Equal(OfflineAlertKind.Lost, t.Observe(false, 8000)!.Value.Kind);
    }

    [Fact]
    public void Reset_DropsARunningOutageWithoutABackAlert()
    {
        var t = Armed();
        for (int i = 0; i < 3; i++) t.Observe(false, i * 1000);
        t.Reset();
        Assert.False(t.IsDown);
        Assert.Null(t.Observe(true, 9000));
    }

    [Theory]
    [InlineData(42, "0:42")]
    [InlineData(725, "12:05")]
    [InlineData(11229, "3:07:09")]
    [InlineData(-5, "0:00")]
    public void Duration_IsCompactAndLanguageNeutral(int seconds, string expected)
        => Assert.Equal(expected, IpScanner.Core.NumberFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Device_QueuesAlertsOnlyWhileTheToggleIsOn()
    {
        var d = new Device("192.168.1.9");
        for (int i = 0; i < 4; i++) d.RecordPing(new PingResult(false, null, null));
        Assert.False(d.TryTakeAlert(out _));

        d.AlertOffline = true;
        d.RecordPing(new PingResult(true, 3, 64));
        for (int i = 0; i < 3; i++) d.RecordPing(new PingResult(false, null, null));
        Assert.True(d.TryTakeAlert(out var lost));
        Assert.Equal(OfflineAlertKind.Lost, lost.Kind);
        Assert.False(d.TryTakeAlert(out _));

        d.RecordPing(new PingResult(true, 3, 64));
        Assert.True(d.TryTakeAlert(out var back));
        Assert.Equal(OfflineAlertKind.Back, back.Kind);
    }

    [Fact]
    public void Device_SwitchingTheToggleOffForgetsTheOutage()
    {
        var d = new Device("192.168.1.9") { AlertOffline = true };
        d.RecordPing(new PingResult(true, 3, 64));
        for (int i = 0; i < 3; i++) d.RecordPing(new PingResult(false, null, null));
        d.TryTakeAlert(out _);
        d.AlertOffline = false;
        d.RecordPing(new PingResult(true, 3, 64));
        Assert.False(d.TryTakeAlert(out _));
        d.AlertOffline = true;
        d.RecordPing(new PingResult(true, 3, 64));
        Assert.False(d.TryTakeAlert(out _));
    }
}
