using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Xunit;
using System.Xml.Schema;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class ExporterTests
{
    private const string Formula = "=HYPERLINK(\"http://x\",\"y\")";
    private static readonly string Banner = new string('B', 996) + "-end";

    private static Device Online(string ip, string? host = null, string? mac = null)
    {
        var d = new Device(ip) { Hostname = host, Mac = mac };
        d.RecordPing(new PingResult(true, 1.5, 64));
        return d;
    }

    private static (List<Device> Devices, ScanReport Report) Sample(bool partial = true)
    {
        Loc.SetLanguage("en");
        var web = Online("192.168.1.10", Formula, "AA:BB:CC:00:11:22");
        web.Note = "line one\nline two";
        var win = Online("192.168.1.20", "pc\u0001-two", "AA:BB:CC:00:11:33");
        var off = new Device("192.168.1.30");
        off.MarkListed();
        var devices = new List<Device> { win, off, web };

        var http = new NetService("HTTP", 80, NetProtocol.Tcp, Banner);
        var smb = new NetService("SMB", 445, NetProtocol.Tcp);
        var snmp = new NetService("SNMP", 161, NetProtocol.Udp, null, ServiceConfidence.Probable);
        var ports = new Dictionary<string, IReadOnlyList<PortResult>>
        {
            [web.Ip] = new[]
            {
                new PortResult(80, NetProtocol.Tcp, PortState.Open, "syn-ack", http),
                new PortResult(81, NetProtocol.Tcp, PortState.Closed, "reset"),
                new PortResult(161, NetProtocol.Udp, PortState.OpenFiltered, "no-response", snmp),
            },
            [win.Ip] = new[] { new PortResult(445, NetProtocol.Tcp, PortState.Open, "syn-ack", smb) }
        };
        var checks = new Dictionary<string, IReadOnlyList<CheckResult>>
        {
            [web.Ip] = new[]
            {
                new CheckResult("http-title", CheckCategory.Safe, http, CheckOutcome.Found,
                    new[] { new CheckFinding("title", "Login \"x\" <b>") })
            }
        };
        var extras = new ReportExtras
        {
            Command = "ipscan-cli -p 80 192.168.1.0/24",
            Targets = "192.168.1.0/24",
            Partial = partial,
            PortResults = ports,
            Checks = checks,
            Findings = new[] { new ConflictFinding(web.Ip, "AA:BB:CC:00:11:22", ConflictKind.MacOnMultipleIps, "also at 192.168.1.20") }
        };
        var info = new NetworkInfo { Interface = "Ethernet", Ip = "192.168.1.2", Gateway = "192.168.1.1", SubnetMask = "255.255.255.0" };
        return (devices, ReportBuilder.Build(devices, info, "20260601_120000", null, null, extras));
    }

    [Fact]
    public void Json_RoundTripsThroughAReader()
    {
        var (_, report) = Sample();
        var json = ReportWriters.Json(report);
        var back = ReportWriters.ParseJson(json);

        Assert.NotNull(back);
        Assert.Equal(1, back!.Schema);
        Assert.Equal(report.Hosts.Count, back.Hosts.Count);
        Assert.Equal(new[] { "192.168.1.10", "192.168.1.20", "192.168.1.30" }, back.Hosts.Select(h => h.Ip));
        Assert.True(back.Meta.Partial);
        Assert.Equal("ipscan-cli -p 80 192.168.1.0/24", back.Meta.Command);
        var web = back.Hosts[0];
        Assert.Equal(Formula, web.Hostname);
        Assert.Equal("line one\nline two", web.Note);
        Assert.Equal(3, web.Ports.Count);
        Assert.Equal(1000, web.Ports.First(p => p.Port == 80).Version!.Length);
        Assert.Equal("open|filtered", web.Ports.First(p => p.Port == 161).State);
        Assert.Equal("Login \"x\" <b>", web.Checks[0].Findings[0].Value);
        Assert.Single(back.Findings);
        Assert.Equal("macOnMultipleIps", back.Findings[0].Kind);
        Assert.Equal(json, ReportWriters.Json(back));
    }

    [Fact]
    public void Json_UsesCamelCaseNamesAndNoNulls()
    {
        var (_, report) = Sample();
        using var doc = System.Text.Json.JsonDocument.Parse(ReportWriters.Json(report));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var host = root.GetProperty("hosts")[0];
        Assert.Equal("ONLINE", host.GetProperty("status").GetString());
        Assert.False(root.GetProperty("hosts")[2].TryGetProperty("hostname", out _));
    }

    private static void AssertKeys(System.Text.Json.JsonElement element, string[] required, string[] allowed, string where)
    {
        var names = element.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var key in required)
            Assert.True(names.Contains(key), $"{where}: missing '{key}'");
        foreach (var name in names)
            Assert.True(allowed.Contains(name), $"{where}: undocumented field '{name}'");
    }

    [Fact]
    public void Json_MatchesTheDocumentedFields()
    {
        var (_, report) = Sample();
        using var doc = System.Text.Json.JsonDocument.Parse(ReportWriters.Json(report));
        var root = doc.RootElement;
        AssertKeys(root, ["schemaVersion", "meta", "hosts", "findings"], ["schemaVersion", "meta", "hosts", "findings"], "report");
        AssertKeys(root.GetProperty("meta"), ["tool", "toolVersion", "startedAt", "command", "targets", "partial"],
            ["tool", "toolVersion", "startedAt", "command", "targets", "partial", "technique", "interface", "localIp", "gateway", "subnetMask", "dns"], "meta");
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("meta").GetProperty("startedAt").GetString(), out _));

        string[] hostKeys = ["ip", "family", "status", "hostname", "mac", "vendor", "group", "discovery", "avgMs", "minMs", "maxMs", "lastMs",
            "pingsDone", "pingsTarget", "fromDb", "note", "os", "ports", "checks"];
        string[] portKeys = ["port", "protocol", "state", "reason", "service", "version", "confidence"];
        string[] checkKeys = ["id", "category", "service", "outcome", "findings"];
        var sawPort = false;
        var sawCheck = false;
        foreach (var host in root.GetProperty("hosts").EnumerateArray())
        {
            AssertKeys(host, ["ip", "status"], hostKeys, "host");
            Assert.Contains(host.GetProperty("status").GetString(), new[] { "ONLINE", "OFFLINE", "LISTED" });
            if (host.TryGetProperty("os", out var os))
                AssertKeys(os, ["family", "percent"], ["family", "percent", "fromFingerprint", "match", "matchPercent", "hops"], "os");
            if (host.TryGetProperty("ports", out var ports))
                foreach (var port in ports.EnumerateArray())
                {
                    sawPort = true;
                    AssertKeys(port, ["port", "protocol", "state"], portKeys, "port");
                    Assert.Contains(port.GetProperty("protocol").GetString(), new[] { "tcp", "udp" });
                    Assert.Contains(port.GetProperty("state").GetString(),
                        new[] { "open", "closed", "filtered", "open|filtered", "unfiltered" });
                }
            if (host.TryGetProperty("checks", out var checks))
                foreach (var check in checks.EnumerateArray())
                {
                    sawCheck = true;
                    AssertKeys(check, ["id", "category", "outcome"], checkKeys, "check");
                    Assert.Contains(check.GetProperty("outcome").GetString(), new[] { "Found", "NothingFound", "NoAnswer" });
                    if (check.TryGetProperty("findings", out var findings))
                        foreach (var f in findings.EnumerateArray())
                            AssertKeys(f, ["key", "value"], ["key", "value"], "check finding");
                }
        }
        Assert.True(sawPort && sawCheck, "the sample report must exercise ports and checks");
        foreach (var finding in root.GetProperty("findings").EnumerateArray())
        {
            AssertKeys(finding, ["ip", "mac", "kind", "detail"], ["ip", "mac", "kind", "detail"], "finding");
            Assert.Contains(finding.GetProperty("kind").GetString(), new[] { "macChanged", "macOnMultipleIps", "multipleMacsForOneIp" });
        }
    }

    private static List<string> Validate(string xml)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema };
        using (var xsd = XmlReader.Create(new StringReader(ReportWriters.XmlSchema())))
            settings.Schemas.Add(null, xsd);
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read()) { }
        return errors;
    }

    [Fact]
    public void Xml_ValidatesAgainstItsOwnSchema()
    {
        var (_, report) = Sample();
        var xml = ReportWriters.Xml(report);

        Assert.Empty(Validate(xml));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("hostname=\"" + Formula.Replace("\"", "&quot;") + "\"", xml);
        Assert.False(xml.Contains('\u0001'));
        Assert.Contains(new string('B', 996) + "-end", xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.Equal("line one\nline two", doc.SelectSingleNode("//host[@ip='192.168.1.10']/note")!.InnerText.Replace("\r\n", "\n"));
        Assert.Equal(Formula, doc.SelectSingleNode("//host[@ip='192.168.1.10']/@hostname")!.Value);
    }

    [Fact]
    public void Xml_SchemaRejectsAnInvalidDocument()
    {
        var (_, report) = Sample();
        var xml = ReportWriters.Xml(report).Replace("status=\"ONLINE\"", "status=\"BOGUS\"");
        Assert.NotEmpty(Validate(xml));
    }

    [Fact]
    public void Xml_EmptyScanIsValid()
    {
        Loc.SetLanguage("en");
        var report = ReportBuilder.Build(Array.Empty<Device>(), new NetworkInfo(), "20260601_120000", null);
        Assert.Empty(Validate(ReportWriters.Xml(report)));
    }

    [Fact]
    public void Grepable_IsOneLinePerHost_EvenWithLineBreaksAndLongBanners()
    {
        var (devices, report) = Sample();
        var text = ReportWriters.Grepable(report);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hostLines = lines.Where(l => l.StartsWith("Host: ", StringComparison.Ordinal)).ToList();

        Assert.Equal(report.Hosts.Count, hostLines.Count);
        Assert.All(lines.Except(hostLines), l => Assert.StartsWith("#", l));
        Assert.DoesNotContain('\r', text);
        var web = hostLines.Single(l => l.StartsWith("Host: 192.168.1.10 ", StringComparison.Ordinal));
        Assert.Contains("\tPorts: 80/open/tcp//HTTP//" + Banner + "/", web);
        Assert.Contains("161/open|filtered/udp//SNMP//", web);
        Assert.Contains("Note: line one line two", web);
        Assert.Contains("Status: Listed", hostLines.Single(l => l.StartsWith("Host: 192.168.1.30 ", StringComparison.Ordinal)));
        Assert.Contains("# Done: 3 hosts", lines);
        Assert.Equal(devices.Count, hostLines.Count);
    }

    [Fact]
    public void Grepable_FieldsSurviveAGrepStyleSplit()
    {
        var (_, report) = Sample();
        var line = ReportWriters.Grepable(report).Split('\n').First(l => l.StartsWith("Host: 192.168.1.20 ", StringComparison.Ordinal));
        var fields = line.Split('\t');

        Assert.Equal("Host: 192.168.1.20 (pc -two)", fields[0]);
        Assert.Equal("Status: Up", fields[1]);
        Assert.StartsWith("MAC: AA:BB:CC:00:11:33", fields[2]);
        Assert.Contains(fields, f => f.StartsWith("Ports: 445/open/tcp//SMB//", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_CarriesHostsPortsChecksAndThePartialNote()
    {
        var (_, report) = Sample();
        var text = ReportWriters.Text(report);

        Assert.Contains("192.168.1.10", text);
        Assert.Contains(Formula, text);
        Assert.Contains(Loc.OpenPortsHeader, text);
        Assert.Contains("80/TCP", text);
        Assert.Contains(Loc.ReportChecksHeader, text);
        Assert.Contains("http-title", text);
        Assert.Contains(Loc.ReportConflictsHeader, text);
        Assert.Contains(Loc.ReportPartialNote, text);
        Assert.Contains("ipscan-cli -p 80", text);
    }

    [Fact]
    public void Text_ForACompleteScanHasNoPartialNote()
    {
        var (_, report) = Sample(partial: false);
        Assert.DoesNotContain(Loc.ReportPartialNote, ReportWriters.Text(report));
        Assert.False(report.Meta.Partial);
    }

    [Fact]
    public void FormulaHostname_IsDefusedInCsvOnly()
    {
        var (devices, report) = Sample();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var csv = File.ReadAllText(CsvExporter.Write(devices, dir, "20260601_120000", "x"));
            Assert.Contains("'=HYPERLINK", csv);
            Assert.DoesNotContain(",=HYPERLINK", csv);
            foreach (var format in ReportFormats.All)
                Assert.Contains(format == ReportFormat.Xml ? "=HYPERLINK(&quot;" : "=HYPERLINK(", ReportWriters.Render(report, format));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SameScan_GivesIdenticalBytesEveryTime()
    {
        var first = Sample().Report;
        var second = Sample().Report;

        foreach (var format in ReportFormats.All)
            Assert.Equal(ReportFile.Bytes(ReportWriters.Render(first, format), format),
                         ReportFile.Bytes(ReportWriters.Render(second, format), format));
    }

    [Fact]
    public void Writers_CreateOneFilePerFormatWithItsExtension()
    {
        var (devices, _) = Sample();
        var info = new NetworkInfo { Interface = "Ethernet", Ip = "192.168.1.2" };
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var paths = new[]
            {
                TxtExporter.Write(devices, info, dir, "20260601_120000", ""),
                XmlExporter.Write(devices, info, dir, "20260601_120000", ""),
                JsonExporter.Write(devices, info, dir, "20260601_120000", ""),
                GrepableExporter.Write(devices, info, dir, "20260601_120000", "")
            };
            Assert.Equal(new[] { ".txt", ".xml", ".json", ".gnmap" }, paths.Select(Path.GetExtension));
            Assert.All(paths, p => Assert.True(new FileInfo(p).Length > 0));
            Assert.Empty(Validate(File.ReadAllText(paths[1])));
            Assert.NotNull(ReportWriters.ParseJson(File.ReadAllText(paths[2])));
            Assert.Equal(0xEF, File.ReadAllBytes(paths[0])[0]);
            Assert.NotEqual(0xEF, File.ReadAllBytes(paths[2])[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_ReplacesAnExistingFileAndCreatesTheFolder()
    {
        var (_, report) = Sample();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "sub", "out.json");
            ReportFile.WriteTo(path, report, ReportFormat.Json);
            var first = File.ReadAllBytes(path);
            ReportFile.WriteTo(path, report, ReportFormat.Json);
            Assert.Equal(first, File.ReadAllBytes(path));
            Assert.NotNull(ReportWriters.ParseJson(File.ReadAllText(path)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Formats_HaveStableNamesAndExtensions()
    {
        Assert.Equal(4, ReportFormats.All.Count);
        Assert.Equal(new[] { "txt", "xml", "json", "gnmap" }, ReportFormats.All.Select(ReportFormats.Extension));
        Assert.Equal(new[] { "Text", "XML", "JSON", "Grepable" }, ReportFormats.All.Select(ReportFormats.Name));
    }
}
