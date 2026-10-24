using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using IpScanner.Core.Export;
using IpScanner.Core.Import;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class Ipv6ExportTests
{
    private static Device Up(string ip)
    {
        var d = new Device(ip);
        d.RecordPing(new PingResult(true, 1.5, 64));
        return d;
    }

    private static ScanReport Report()
    {
        Loc.SetLanguage("en");
        var devices = new List<Device> { Up("2001:db8::10"), Up("fe80::1%12"), Up("192.168.1.20"), Up("192.168.1.3") };
        var smb = new NetService("SMB", 445, NetProtocol.Tcp);
        var extras = new ReportExtras
        {
            Targets = "-6 2001:db8::10",
            PortResults = new Dictionary<string, IReadOnlyList<PortResult>>
            {
                ["2001:db8::10"] = new[] { new PortResult(445, NetProtocol.Tcp, PortState.Open, "syn-ack", smb) }
            }
        };
        var info = new NetworkInfo { Interface = "Ethernet", Ip = "192.168.1.2", Gateway = "192.168.1.1", SubnetMask = "255.255.255.0" };
        return ReportBuilder.Build(devices, info, "20260601_120000", null, null, extras);
    }

    [Fact]
    public void Hosts_SortIpv4FirstThenIpv6()
    {
        Assert.Equal(new[] { "192.168.1.3", "192.168.1.20", "2001:db8::10", "fe80::1%12" }, Report().Hosts.Select(h => h.Ip));
    }

    [Fact]
    public void Json_CarriesTheFamilyAndRoundTrips()
    {
        var json = ReportWriters.Json(Report());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var families = doc.RootElement.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("family").GetString()).ToArray();
        Assert.Equal(new[] { "ipv4", "ipv4", "ipv6", "ipv6" }, families);
        var back = ReportWriters.ParseJson(json)!;
        Assert.Equal("ipv6", back.Hosts[2].Family);
        Assert.Equal(json, ReportWriters.Json(back));
    }

    [Fact]
    public void Xml_UsesAddrtypeAndStaysValid()
    {
        var xml = ReportWriters.Xml(Report());
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = System.Xml.ValidationType.Schema };
        using (var xsd = XmlReader.Create(new StringReader(ReportWriters.XmlSchema()))) settings.Schemas.Add(null, xsd);
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using (var reader = XmlReader.Create(new StringReader(xml), settings)) while (reader.Read()) { }
        Assert.Empty(errors);
        Assert.Contains("addrtype=\"ipv6\"", xml);
        Assert.Contains("addrtype=\"ipv4\"", xml);
    }

    [Fact]
    public void Grepable_And_Text_ShowTheFullAddress()
    {
        var report = Report();
        var grep = ReportWriters.Grepable(report);
        Assert.Contains("Host: 2001:db8::10", grep);
        Assert.Contains("445/open/tcp", grep);
        var text = ReportWriters.Text(report);
        Assert.Contains("2001:db8::10", text);
        Assert.Contains("fe80::1%12", text);
    }

    [Fact]
    public void Csv_ListsTheHostsInFamilyOrder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var devices = new List<Device> { Up("fe80::1%12"), Up("2001:db8::10"), Up("192.168.1.20"), Up("192.168.1.3") };
        var csv = File.ReadAllText(CsvExporter.Write(devices, dir, "20260601_120000", ""));
        var order = new[] { "192.168.1.3", "192.168.1.20", "2001:db8::10", "fe80::1%12" }.Select(ip => csv.IndexOf(ip, System.StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void Importer_AcceptsIpv6EntriesForEveryKind()
    {
        var net = ListImportParser.Parse("2001:db8::/64 Lab\nfe80::1\n192.168.1.0/24\n2001:db8::zz", ImportTargetKind.Network);
        Assert.Equal(3, net.Accepted.Count);
        Assert.Equal(1, net.SkippedLines);
        var pinned = ListImportParser.Parse("2001:db8::10 Box\n2001:db8::/64", ImportTargetKind.Pinned);
        Assert.Single(pinned.Accepted);
        Assert.Equal(1, pinned.SkippedLines);
    }

    [Theory]
    [InlineData("2001:DB8:0:0:0:0:0:10", "2001:db8::10")]
    [InlineData("2001:db8::0:10/64", "2001:db8::/64")]
    [InlineData("192.168.1.0/24", "192.168.1.0/24")]
    [InlineData("host.example", "host.example")]
    public void Normalize_UsesTheShortForm(string input, string expected) => Assert.Equal(expected, AddressSyntax.Normalize(input));

    [Theory]
    [InlineData("::1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("fe80::1%12", true)]
    [InlineData("1.2.3", false)]
    [InlineData("", false)]
    public void IsValidAddress_AcceptsBothFamilies(string text, bool expected) => Assert.Equal(expected, AddressSyntax.IsValidAddress(text));

    [Fact]
    public void AddressKey_ComparesThroughTheNonGenericInterface()
    {
        IpAddressKey.TryParse("10.0.0.2", out var a);
        IpAddressKey.TryParse("10.0.0.10", out var b);
        IpAddressKey.TryParse("::1", out var c);
        var list = new System.Collections.ArrayList { c, b, a };
        list.Sort();
        Assert.Equal(new object[] { a, b, c }, list.ToArray());
    }
}
