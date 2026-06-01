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
}
