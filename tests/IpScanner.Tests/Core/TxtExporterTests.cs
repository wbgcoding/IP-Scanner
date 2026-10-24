using System;
using System.IO;
using System.Text.Json;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

// TxtExporter now reads its labels from Loc, so these tests depend
// on the active language - shares the "Localization" collection with every
// other test class that touches the process-wide Loc.German flag.
[Collection("Localization")]
public class TxtExporterTests
{
    [Fact]
    public void FormatRowsAsMarkdown_EscapesTableBreakingDataAndPreservesNotes()
    {
        Loc.SetLanguage("en");
        var table = TxtExporter.FormatRowsAsMarkdown(new[]
        {
            new TxtExporter.RowFields("192.0.2.1", "Online", "host|name", "1", "1", "1", "1",
                "0%", "0", "1/1", "AA", "vendor", "line one\nline | two")
        });
        Assert.Contains("host\\|name", table);
        Assert.Contains("line one line \\| two", table);
        Assert.Equal(3, table.Split("\r\n").Length);
    }

    [Fact]
    public void Write_ContainsHeaderAndDevice()
    {
        Loc.SetLanguage("en");
        try
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
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Write_IncludesMinMaxLastAndSortsByIp()
    {
        Loc.SetLanguage("en");
        try
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
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Write_ExportsMultiLineDeviceNote()
    {
        Loc.SetLanguage("en");
        try
        {
            var d = new Device("192.168.1.8") { Note = "rack one\ntop shelf" };
            d.RecordPing(new PingResult(true, 1.0, 64));
            var dir = Directory.CreateTempSubdirectory().FullName;

            var path = TxtExporter.Write(new[] { d }, new NetworkInfo(), dir, "20260604_091500", "");
            var text = File.ReadAllText(path);

            Assert.Contains("Note: rack one\n    top shelf", text.Replace("\r\n", "\n"));
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void HtmlAndJson_ExportNoteAsOwnField()
    {
        Loc.SetLanguage("en");
        try
        {
            const string note = "<script>alert(1)</script>\nsecond line";
            var d = new Device("192.168.1.9") { Note = note };
            d.RecordPing(new PingResult(true, 1.0, 64));
            var dir = Directory.CreateTempSubdirectory().FullName;

            var htmlPath = HtmlExporter.Write(new[] { d }, new NetworkInfo(), dir, "20260604_092000", "");
            var jsonPath = JsonExporter.Write(new[] { d }, new NetworkInfo(), dir, "20260604_092000", "");
            var html = File.ReadAllText(htmlPath);
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));

            Assert.Contains("<th>Note</th>", html);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Equal(note, json.RootElement.GetProperty("hosts")[0].GetProperty("note").GetString());
        }
        finally { Loc.SetLanguage("en"); }
    }

    // The port-check tool's export/copy must read exactly like a device
    // row's "copy open ports" - both now call this one shared formatter.
    [Fact]
    public void FormatPortsText_HeaderThenOnePortLinePerService()
    {
        var services = new[]
        {
            new IpScanner.Core.Scanner.NetService("SSH", 22, IpScanner.Core.Scanner.NetProtocol.Tcp) { Ip = "192.168.1.5" },
            new IpScanner.Core.Scanner.NetService("HTTP", 80, IpScanner.Core.Scanner.NetProtocol.Tcp, "nginx") { Ip = "192.168.1.5" },
        };

        var text = TxtExporter.FormatPortsText("192.168.1.5", "router", services);

        var lines = text.Split('\n');
        Assert.Equal("192.168.1.5  router", lines[0].TrimEnd('\r'));
        Assert.Contains(lines, l => l.Contains("22/TCP") && l.Contains("SSH"));
        Assert.Contains(lines, l => l.Contains("80/TCP") && l.Contains("HTTP nginx"));
    }

    [Fact]
    public void FormatPortsText_NoServices_ReturnsEmpty()
        => Assert.Equal("", TxtExporter.FormatPortsText("192.168.1.5", "router", System.Array.Empty<IpScanner.Core.Scanner.NetService>()));

    // "Copy row as text" - header line plus one tab-separated value
    // line, matching the table's visible columns (Group is a color swatch
    // only and has no place here).
    [Fact]
    public void FormatRowAsText_HeaderThenTabSeparatedValues()
    {
        Loc.SetLanguage("en");
        try
        {
            var text = TxtExporter.FormatRowAsText("192.168.1.5", "Online", "nas.local", "1.20ms", "0.90ms",
                "2.10ms", "1.00ms", "0%", "0.30ms", "10/10", "AA:BB:CC:DD:EE:FF", "Synology", "rack one\nrack two");

            var lines = text.Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            Assert.Equal(2, lines.Length);
            Assert.Equal(new[] { "IP address", "Status", "Hostname", "Avg", "Min", "Max", "Last", "Loss", "Jitter", "Progress", "MAC", "Vendor", "Note" }, lines[0].Split('\t'));
            Assert.Equal(new[] { "192.168.1.5", "Online", "nas.local", "1.20ms", "0.90ms", "2.10ms", "1.00ms", "0%", "0.30ms", "10/10", "AA:BB:CC:DD:EE:FF", "Synology", "rack one / rack two" }, lines[1].Split('\t'));
        }
        finally { Loc.SetLanguage("en"); }
    }

    // Several selected rows share one header and keep one value line each.
    [Fact]
    public void FormatRowsAsText_HeaderOnceThenOneValueLinePerRow()
    {
        Loc.SetLanguage("en");
        try
        {
            var rows = new[]
            {
                new TxtExporter.RowFields("192.168.1.5", "Online", "nas.local", "1.20ms", "0.90ms",
                    "2.10ms", "1.00ms", "0%", "0.30ms", "10/10", "AA:BB:CC:DD:EE:FF", "Synology"),
                new TxtExporter.RowFields("192.168.1.6", "Offline", "-", "-", "-", "-", "-", "-", "-", "8/10", "-", "-"),
            };

            var text = TxtExporter.FormatRowsAsText(rows);

            var lines = text.Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            Assert.Equal(3, lines.Length);
            Assert.Equal(new[] { "IP address", "Status", "Hostname", "Avg", "Min", "Max", "Last", "Loss", "Jitter", "Progress", "MAC", "Vendor", "Note" }, lines[0].Split('\t'));
            Assert.Equal("192.168.1.5", lines[1].Split('\t')[0]);
            Assert.Equal("192.168.1.6", lines[2].Split('\t')[0]);
        }
        finally { Loc.SetLanguage("en"); }
    }

    // FormatRowAsText must still behave exactly like a one-row FormatRowsAsText call.
    [Fact]
    public void FormatRowAsText_MatchesSingleRowFormatRowsAsText()
    {
        Loc.SetLanguage("en");
        try
        {
            var single = TxtExporter.FormatRowAsText("192.168.1.5", "Online", "nas.local", "1.20ms", "0.90ms",
                "2.10ms", "1.00ms", "0%", "0.30ms", "10/10", "AA:BB:CC:DD:EE:FF", "Synology");
            var viaRows = TxtExporter.FormatRowsAsText(new[]
            {
                new TxtExporter.RowFields("192.168.1.5", "Online", "nas.local", "1.20ms", "0.90ms",
                    "2.10ms", "1.00ms", "0%", "0.30ms", "10/10", "AA:BB:CC:DD:EE:FF", "Synology"),
            });

            Assert.Equal(single, viaRows);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Write_RelativeOutputDir_ResolvesUnderTheExeFolderNotTheWorkingDirectory()
    {
        Loc.SetLanguage("en");
        var previous = Directory.GetCurrentDirectory();
        var elsewhere = Directory.CreateTempSubdirectory().FullName;
        var relative = Path.Combine(".", "scans-" + Guid.NewGuid().ToString("N"));
        var expectedDir = Path.GetFullPath(relative, AppContext.BaseDirectory);
        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            var d = new Device("192.168.1.1") { Hostname = "router" };
            d.RecordPing(new PingResult(true, 1.0, 64));
            var path = TxtExporter.Write(new[] { d }, new NetworkInfo { Ip = "192.168.1.100" },
                relative, "20260101-000000", "");
            Assert.Equal(expectedDir, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            if (Directory.Exists(expectedDir)) Directory.Delete(expectedDir, true);
            Directory.Delete(elsewhere, true);
        }
    }
}
