using IpScanner.Core.Models;
using IpScanner.Core.Scanner.Os;
using Xunit;

namespace IpScanner.Tests.Core;

public class OsGuessTests
{
    private static OsEstimate Guess(int? ttl, params int[] ports)
        => OsHeuristic.Estimate(new OsInputs(ttl, ports));

    [Fact]
    public void WindowsHost_TtlAndSmbRdp_IsWindows()
    {
        var g = Guess(125, 135, 445, 3389);
        Assert.Equal(OsFamily.Windows, g.Family);
        Assert.InRange(g.Percent, 60, OsHeuristic.MaxPercent);
        Assert.False(g.FromFingerprint);
    }

    [Fact]
    public void LinuxHost_TtlAndSshWithoutSmb_IsUnixLike()
    {
        var g = Guess(60, 22, 80);
        Assert.Equal(OsFamily.UnixLike, g.Family);
        Assert.InRange(g.Percent, 60, OsHeuristic.MaxPercent);
    }

    [Fact]
    public void Printer_RawPrintPort_BeatsTheUnixTtl()
    {
        var g = Guess(64, 80, 9100);
        Assert.Equal(OsFamily.Printer, g.Family);
    }

    [Fact]
    public void MediaDevice_CastAndRtspPorts_AreMedia()
    {
        Assert.Equal(OsFamily.Media, Guess(64, 8009).Family);
        Assert.Equal(OsFamily.Media, Guess(64, 554).Family);
    }

    [Fact]
    public void NetworkGear_Ttl255_IsNetworkGear()
    {
        Assert.Equal(OsFamily.NetworkGear, Guess(250, 23).Family);
    }

    [Fact]
    public void SambaNas_SshAndSmb_StaysUnixLikeButWithLowConfidence()
    {
        var nas = Guess(64, 22, 445);
        var plain = Guess(64, 22);
        Assert.Equal(OsFamily.UnixLike, nas.Family);
        Assert.True(nas.Percent < plain.Percent);
    }

    [Fact]
    public void NoEvidence_IsUnknown_AndConfidenceNeverReaches100()
    {
        Assert.Equal(OsFamily.Unknown, Guess(null).Family);
        Assert.Equal(OsFamily.Unknown, Guess(20).Family);
        var best = Guess(128, 135, 139, 445, 3389);
        Assert.True(best.Percent <= OsHeuristic.MaxPercent && best.Percent < 100);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(32, 0)]
    [InlineData(40, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(128, 128)]
    [InlineData(129, 255)]
    public void InitialTtl_RoundsUpToTheCommonStartValue(int ttl, int expected)
        => Assert.Equal(expected, OsHeuristic.InitialTtl(ttl) ?? 0);

    [Fact]
    public void Device_KeepsTheLatestReplyTtl_AndTheEstimatorUsesIt()
    {
        var d = new Device("10.0.0.9");
        d.RecordPing(new PingResult(false, null, null));
        Assert.Null(d.Ttl);
        d.RecordPing(new PingResult(true, 1, 127));
        Assert.Equal(127, d.Ttl);
        Assert.Equal(OsFamily.Windows, OsEstimator.For(d, new[] { 445, 3389 }).Family);
    }

    [Fact]
    public void Estimator_PrefersAFingerprintResult()
    {
        var d = new Device("10.0.0.9");
        d.RecordPing(new PingResult(true, 1, 64));
        d.OsFingerprint = new OsEstimate(OsFamily.Windows, 80, true, "Windows 10/11", 1);
        var e = OsEstimator.For(d, new[] { 22 });
        Assert.True(e.FromFingerprint);
        Assert.Equal(OsFamily.Windows, e.Family);
    }
}
