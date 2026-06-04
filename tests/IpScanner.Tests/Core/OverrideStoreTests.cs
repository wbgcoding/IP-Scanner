using System.IO;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class OverrideStoreTests
{
    private static string TempPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.conf");

    [Fact]
    public void SetAndGet_ByMacAndIpFallback()
    {
        var s = new OverrideStore();
        s.Load(TempPath());

        s.SetHostname(null, "192.168.1.5", "printer");
        Assert.Equal("printer", s.Get(null, "192.168.1.5")?.Hostname);

        // Once the MAC is known, the entry migrates to the MAC key.
        s.SetHostname("aa:bb:cc:11:22:33", "192.168.1.5", "printer2");
        Assert.Equal("printer2", s.Get("AA:BB:CC:11:22:33", "10.0.0.9")?.Hostname);
        Assert.Null(s.Get(null, "192.168.1.5"));
    }

    [Fact]
    public void Reset_RemovesEntry_ColorAndHostnameIndependent()
    {
        var s = new OverrideStore();
        s.Load(TempPath());

        s.SetHostname(null, "192.168.1.7", "nas");
        s.SetColor(null, "192.168.1.7", "#A6E3A1");
        s.SetHostname(null, "192.168.1.7", null);    // reset hostname only
        Assert.Null(s.Get(null, "192.168.1.7")?.Hostname);
        Assert.Equal("#A6E3A1", s.Get(null, "192.168.1.7")?.Color);

        s.SetColor(null, "192.168.1.7", null);       // reset color -> entry gone
        Assert.Null(s.Get(null, "192.168.1.7"));
    }

    [Fact]
    public void Save_RoundTripsThroughLoad()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname("AA:BB:CC:00:11:22", "192.168.1.9", "cam");
        a.SetColor("AA:BB:CC:00:11:22", "192.168.1.9", "#89B4FA");

        var b = new OverrideStore();
        b.Load(path);
        var e = b.Get("AA:BB:CC:00:11:22", "192.168.1.9");
        Assert.Equal("cam", e?.Hostname);
        Assert.Equal("#89B4FA", e?.Color);
    }
}
