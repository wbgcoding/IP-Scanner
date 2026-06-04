using System.IO;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class CsvExporterTests
{
    [Fact]
    public void Write_ProducesHeaderAndRow()
    {
        var d = new Device("192.168.1.1") { Mac = "AA:BB:CC:11:22:33", Hostname = "router" };
        d.RecordPing(new PingResult(true, 2.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260601_120000", "router");

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("ip,status,hostname,group,vendor,mac", lines[0]);
        Assert.Contains("192.168.1.1", lines[1]);
        Assert.Contains("ONLINE", lines[1]);
    }

    [Fact]
    public void Write_SortsRowsByNumericIp()
    {
        Device D(string ip) { var d = new Device(ip); d.RecordPing(new PingResult(true, 1.0, 64)); return d; }
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { D("192.168.1.20"), D("192.168.1.3"), D("192.168.1.100") },
                                     dir, "20260604_080000", "");

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("192.168.1.3,", lines[1]);
        Assert.StartsWith("192.168.1.20,", lines[2]);
        Assert.StartsWith("192.168.1.100,", lines[3]);
    }
}
