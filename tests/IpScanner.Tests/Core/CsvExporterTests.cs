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
    public void Write_QuotesFieldsContainingDelimiters()
    {
        var d = new Device("192.168.1.5") { Hostname = "router, \"main\"" };
        d.RecordPing(new PingResult(true, 2.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_090000", "");

        var lines = File.ReadAllLines(path);
        // RFC 4180: field quoted, inner quotes doubled.
        Assert.Contains("\"router, \"\"main\"\"\"", lines[1]);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void Write_DefusesFormulaInjectionInNetworkSourcedHostname(string malicious)
    {
        // A hostname discovered via DNS/mDNS/NetBIOS is attacker-controlled.
        // Opened in Excel, a leading =/+/-/@ makes the cell execute as a formula.
        var d = new Device("192.168.1.7") { Hostname = malicious };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_090500", "");

        var lines = File.ReadAllLines(path);
        var hostnameField = lines[1].Split(',')[2].Trim('"').Replace("\"\"", "\"");
        Assert.Equal("'" + malicious, hostnameField);
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

    [Fact]
    public void Write_ExportsNoteAndDefusesFormulaInjection()
    {
        var d = new Device("192.168.1.8") { Note = "=cmd" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_091000", "");
        var lines = File.ReadAllLines(path);

        Assert.EndsWith(",note", lines[0]);
        Assert.EndsWith(",'=cmd", lines[1]);
    }
}
