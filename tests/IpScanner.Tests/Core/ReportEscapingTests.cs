using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner.Os;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class ReportEscapingTests
{
    private const string Esc = "\u001b]0;owned\u0007";

    private static ScanReport Report(string hostname, string? osMatch, string detail)
    {
        Loc.SetLanguage("en");
        var meta = new ReportMeta("ipscan-cli", "1.0.0", null, "ipscan-cli " + Esc, null, false, "Eth", null, null, null, []);
        var os = new ReportOs(OsFamily.UnixLike, 80, true, osMatch, null, null);
        var port = new ReportPort(80, "tcp", "open", "syn-ack", "http" + Esc, "1.0" + Esc, null);
        var check = new ReportCheck("id", "safe", "svc" + Esc, "found",
            [new KeyValuePair<string, string>("k" + Esc, "v" + Esc)]);
        var host = new ReportHost("192.168.1.9", ReportBuilder.Online, hostname, "AA:BB", null, "A", null,
            null, null, null, null, 0, "1", false, "note" + Esc + "\nsecond", os, [port], [check]);
        return new ScanReport(meta, [host], [new ReportFinding("192.168.1.9", null, "dup", detail)]);
    }

    [Fact]
    public void XmlSafe_KeepsSurrogatePairsAndDropsLoneOnes()
    {
        Assert.Equal("a\U0001F600b", ReportCodec.XmlSafe("a\U0001F600b"));
        Assert.Equal("ab", ReportCodec.XmlSafe("a\ud800b"));
        Assert.Equal("ab", ReportCodec.XmlSafe("a\udc00b"));
        Assert.Equal("ab", ReportCodec.XmlSafe("a\u0001b"));
    }

    [Fact]
    public void Xml_KeepsAnEmojiInAHostname()
    {
        var xml = ReportWriters.Xml(Report("pc-\U0001F600", null, "d"));
        Assert.Contains("pc-\U0001F600", xml);
    }

    [Fact]
    public void Text_HasNoEscapeCharactersFromReportedValues()
    {
        var text = ReportWriters.Text(Report("h" + Esc, "Linux" + Esc, "detail" + Esc));
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Grepable_KeepsOneLinePerHostAndFinding()
    {
        var text = ReportWriters.Grepable(Report("h", "Linux\r\nsplit", "de\ntail"));
        Assert.Single(text.Split('\n'), l => l.StartsWith("Host: ", System.StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), l => l.StartsWith("# Finding: ", System.StringComparison.Ordinal));
        Assert.DoesNotContain('\r', text);
    }
}
