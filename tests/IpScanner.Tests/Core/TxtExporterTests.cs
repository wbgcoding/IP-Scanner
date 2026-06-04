using System.IO;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class TxtExporterTests
{
    [Fact]
    public void Write_ContainsHeaderAndDevice()
    {
        var info = new NetworkInfo { Ip = "192.168.1.100", Gateway = "192.168.1.1" };
        var d = new Device("192.168.1.1") { Hostname = "router" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = TxtExporter.Write(new[] { d }, info, dir, "20260601_120000", "router");

        var text = File.ReadAllText(path);
        Assert.Contains("NETWORK SCAN REPORT", text);
        Assert.Contains("192.168.1.1", text);
        Assert.Contains("router", text);
    }

    [Fact]
    public void Write_IncludesMinMaxLastAndSortsByIp()
    {
        Device D(string ip, double ms)
        {
            var d = new Device(ip);
            d.RecordPing(new PingResult(true, ms, 64));
            d.RecordPing(new PingResult(true, ms * 2, 64));
            return d;
        }
        var info = new NetworkInfo { Ip = "192.168.1.100" };
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = TxtExporter.Write(new[] { D("192.168.1.30", 4.0), D("192.168.1.2", 1.0) },
                                     info, dir, "20260604_080000", "");

        var lines = File.ReadAllLines(path);
        Assert.Contains(lines, l => l.Contains("Min") && l.Contains("Max") && l.Contains("Last"));
        // .2 row (min 1.00ms, max 2.00ms) must come before .30
        int i2 = System.Array.FindIndex(lines, l => l.StartsWith("192.168.1.2 "));
        int i30 = System.Array.FindIndex(lines, l => l.StartsWith("192.168.1.30 "));
        Assert.True(i2 >= 0 && i30 > i2);
        Assert.Contains("1.00ms", lines[i2]);
        Assert.Contains("2.00ms", lines[i2]);
    }
}
