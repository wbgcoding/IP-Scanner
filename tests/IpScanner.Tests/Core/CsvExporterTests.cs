using System.Collections.Generic;
using System.IO;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
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

        var path = CsvExporter.Write(new[] { d }, dir, "20260601_120000", "router", delimiter: ',');

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

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_090000", "", delimiter: ',');

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

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_090500", "", delimiter: ',');

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
                                     dir, "20260604_080000", "", delimiter: ',');

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

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_091000", "", delimiter: ',');
        var lines = File.ReadAllLines(path);

        Assert.EndsWith(",note", lines[0]);
        Assert.EndsWith(",'=cmd", lines[1]);
    }

    [Fact]
    public void Write_SemicolonDelimiterQuotesTheOpenPortsCell()
    {
        var d = new Device("192.168.1.9") { Hostname = "nas" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var ports = new Dictionary<string, IReadOnlyList<NetService>>
        {
            ["192.168.1.9"] = new[]
            {
                new NetService("HTTP", 80, NetProtocol.Tcp),
                new NetService("HTTPS", 443, NetProtocol.Tcp)
            }
        };
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_092000", "", ports, ';');
        var lines = File.ReadAllLines(path);

        Assert.StartsWith("ip;status;hostname;group;", lines[0]);
        Assert.StartsWith("192.168.1.9;ONLINE;nas;", lines[1]);
        Assert.Contains(";\"80/TCP HTTP; 443/TCP HTTPS\";", lines[1]);
    }

    [Fact]
    public void Write_CommaDelimiterKeepsCommaFormat()
    {
        var d = new Device("192.168.1.10");
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_092100", "", delimiter: ',');

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("ip,status,hostname", lines[0]);
        Assert.DoesNotContain(';', lines[1]);
    }

    [Fact]
    public void Write_StartsWithUtf8Bom()
    {
        var d = new Device("192.168.1.11");
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260604_092200", "");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public void Write_DefaultDelimiterFollowsTheCultureListSeparator()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var d = new Device("192.168.1.12");
            d.RecordPing(new PingResult(true, 1.0, 64));
            var dir = Directory.CreateTempSubdirectory().FullName;

            var path = CsvExporter.Write(new[] { d }, dir, "20260604_092300", "");

            Assert.StartsWith("ip;status;", File.ReadAllLines(path)[0]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
