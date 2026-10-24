using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class ReportPortStateTests
{
    private static string Text(ScanType? technique, params PortResult[] rows)
    {
        Loc.SetLanguage("en");
        var d = new Device("192.168.1.10");
        d.RecordPing(new PingResult(true, 1.5, 64));
        var extras = new ReportExtras
        {
            Technique = technique,
            PortResults = new Dictionary<string, IReadOnlyList<PortResult>> { [d.Ip] = rows },
        };
        var report = ReportBuilder.Build([d], new NetworkInfo(), "20260601_120000", null, null, extras);
        return ReportWriters.Text(report);
    }

    private static PortResult P(int port, PortState state, string reason = "r") => new(port, NetProtocol.Tcp, state, reason);

    [Fact]
    public void Ack_ListsUnfilteredPortsInsteadOfAnEmptyReport()
    {
        var text = Text(ScanType.Ack, P(22, PortState.Unfiltered), P(80, PortState.Filtered), P(443, PortState.Unfiltered));
        Assert.Contains("22/TCP", text);
        Assert.Contains("unfiltered", text);
        Assert.Contains("80/TCP", text);
        Assert.Contains("443/TCP", text);
    }

    [Fact]
    public void Window_ListsOpenAndClosedPorts()
    {
        var text = Text(ScanType.Window, P(22, PortState.Open), P(23, PortState.Closed), P(80, PortState.Closed));
        Assert.Contains("22/TCP", text);
        Assert.Contains("23/TCP", text);
        Assert.Contains("80/TCP", text);
        Assert.Contains("closed", text);
    }

    [Fact]
    public void Maimon_ListsOpenFilteredAndClosedPorts()
    {
        var text = Text(ScanType.Maimon, P(22, PortState.OpenFiltered), P(23, PortState.Closed));
        Assert.Contains("22/TCP", text);
        Assert.Contains("open|filtered", text);
        Assert.Contains("23/TCP", text);
        Assert.Contains("closed", text);
    }

    [Theory]
    [InlineData(ScanType.Ack, PortState.Filtered)]
    [InlineData(ScanType.Window, PortState.Closed)]
    [InlineData(ScanType.Maimon, PortState.Closed)]
    public void ABigState_IsSummarisedLikeNmap(ScanType type, PortState bulk)
    {
        var rows = Enumerable.Range(1, 40).Select(i => P(i, bulk)).Append(P(8080, PortState.Open)).ToArray();
        var text = Text(type, rows);
        Assert.Contains("8080/TCP", text);
        Assert.Contains(string.Format(Loc.ReportNotShownFmt, 40, ReportBuilder.StateId(bulk)), text);
        Assert.DoesNotContain("  7/TCP", text);
    }

    [Theory]
    [InlineData(ScanType.Connect)]
    [InlineData(ScanType.Syn)]
    public void OtherTypes_StillListOnlyOpenPorts(ScanType type)
    {
        var text = Text(type, P(22, PortState.Open), P(23, PortState.Closed), P(24, PortState.Filtered));
        Assert.Contains("22/TCP", text);
        Assert.DoesNotContain("23/TCP", text);
        Assert.DoesNotContain("24/TCP", text);
    }

    [Fact]
    public void Technique_IsWrittenToXmlJsonAndGrepable()
    {
        var d = new Device("192.168.1.10");
        d.RecordPing(new PingResult(true, 1.5, 64));
        var extras = new ReportExtras { Technique = ScanType.Window, PortResults = new Dictionary<string, IReadOnlyList<PortResult>> { [d.Ip] = [P(1, PortState.Closed)] } };
        var report = ReportBuilder.Build([d], new NetworkInfo(), "20260601_120000", null, null, extras);
        Assert.Equal("window", report.Meta.Technique);
        Assert.Contains("technique=\"window\"", ReportWriters.Xml(report));
        Assert.Contains("\"technique\": \"window\"", ReportWriters.Json(report));
        Assert.Contains("# Scan type: window", ReportWriters.Grepable(report));
    }
}
