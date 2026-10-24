using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class ScanCompareTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static ArchivedDevice Dev(string ip, string? mac, string? name, double? ms, bool online = true) =>
        new(ip, mac, name, null, online, 10, 10, 0, ms, ms, ms, 0, 0, null);

    private static ScanArchiveDetail Run(long id, DateTimeOffset at, IReadOnlyList<ArchivedDevice> devices,
        params ArchivedFinding[] findings) =>
        new(new ArchivedScan(id, at, at.AddMinutes(1), "192.0.2.0/24", ["192.0.2.0/24"], "{}",
            devices.Count, devices.Count(d => d.Online), 0, 0, 0), devices, findings);

    private static ArchivedFinding[] Ports(string ip, params string[] open) =>
        new[] { new ArchivedFinding(ip, ScanCompare.PortScannedKind, null) }
            .Concat(open.Select(p => new ArchivedFinding(ip, ScanCompare.OpenPortKind, p))).ToArray();

    /// <summary>Run A and B built so every change type occurs for exactly one host.</summary>
    private static (ScanArchiveDetail A, ScanArchiveDetail B) Fabricated()
    {
        var a = Run(1, T0, [
            Dev("192.0.2.10", "AA:00:00:00:00:10", "stable", 2),
            Dev("192.0.2.11", "AA:00:00:00:00:11", "gone-host", 3),
            Dev("192.0.2.12", "AA:00:00:00:00:12", "mover", 3),
            Dev("192.0.2.13", "AA:00:00:00:00:13", "swapped", 4),
            Dev("192.0.2.14", "AA:00:00:00:00:14", "opened", 5),
            Dev("192.0.2.15", "AA:00:00:00:00:15", "closed", 5),
            Dev("192.0.2.16", "AA:00:00:00:00:16", "slow", 10)
        ], Ports("192.0.2.10", "80/TCP").Concat(Ports("192.0.2.14", "22/TCP"))
            .Concat(Ports("192.0.2.15", "22/TCP", "443/TCP")).Concat(Ports("192.0.2.16")).ToArray());
        var b = Run(2, T0.AddDays(1), [
            Dev("192.0.2.10", "AA:00:00:00:00:10", "stable", 2),
            Dev("192.0.2.20", "AA:00:00:00:00:12", "mover", 3),
            Dev("192.0.2.13", "BB:00:00:00:00:99", "swapped", 4),
            Dev("192.0.2.14", "AA:00:00:00:00:14", "opened", 5),
            Dev("192.0.2.15", "AA:00:00:00:00:15", "closed", 5),
            Dev("192.0.2.16", "AA:00:00:00:00:16", "slow", 40),
            Dev("192.0.2.30", "AA:00:00:00:00:30", "fresh", 6)
        ], Ports("192.0.2.10", "80/TCP").Concat(Ports("192.0.2.14", "22/TCP", "8080/TCP"))
            .Concat(Ports("192.0.2.15", "22/TCP")).Concat(Ports("192.0.2.16")).ToArray());
        return (a, b);
    }

    [Fact]
    public void Compute_ReportsEveryChangeTypeExactlyOnce()
    {
        var (a, b) = Fabricated();
        var result = ScanCompare.Compute(a, b);

        Assert.Equal(7, result.Changes.Count);
        foreach (var type in Enum.GetValues<ScanChangeType>())
            Assert.Single(result.Changes, c => c.Type == type);
        Assert.Equal("192.0.2.30", Single(result, ScanChangeType.New).Ip);
        Assert.Equal("192.0.2.11", Single(result, ScanChangeType.Gone).Ip);
        var ip = Single(result, ScanChangeType.IpChanged);
        Assert.Equal(("192.0.2.12", "192.0.2.20"), (ip.Before, ip.After));
        var mac = Single(result, ScanChangeType.MacChanged);
        Assert.Equal(("192.0.2.13", "AA:00:00:00:00:13", "BB:00:00:00:00:99"), (mac.Ip, mac.Before, mac.After));
        Assert.Equal("8080/TCP", Single(result, ScanChangeType.PortsOpened).After);
        Assert.Equal("443/TCP", Single(result, ScanChangeType.PortsClosed).Before);
        var latency = Single(result, ScanChangeType.LatencyChanged);
        Assert.Equal(("192.0.2.16", "10 ms", "40 ms"), (latency.Ip, latency.Before, latency.After));
        Assert.True(result.PortsCompared);
    }

    private static ScanChange Single(ScanCompareResult r, ScanChangeType t) => r.Changes.Single(c => c.Type == t);

    [Fact]
    public void Compute_IdenticalRunsHaveNoChanges()
    {
        var (a, _) = Fabricated();
        var again = a with { Scan = a.Scan with { Id = 9, StartedAt = T0.AddDays(3) } };

        Assert.Empty(ScanCompare.Compute(a, again).Changes);
        Assert.Empty(ScanCompare.Compute(a, a).Changes);
    }

    [Fact]
    public void Compute_ChangedOrderAndOfflineRowsAreNoChanges()
    {
        var a = Run(1, T0, [Dev("192.0.2.1", "AA:00:00:00:00:01", "x", 2), Dev("192.0.2.2", null, null, null, online: false)]);
        var b = Run(2, T0.AddHours(1), [Dev("192.0.2.2", null, null, null, online: false), Dev("192.0.2.1", "aa:00:00:00:00:01", "x", 2)]);

        Assert.Empty(ScanCompare.Compute(a, b).Changes);
    }

    [Theory]
    [InlineData(10, 40, 50, 10, true)]    // +30 ms and +300 %
    [InlineData(10, 15, 50, 10, false)]   // +50 % but only 5 ms
    [InlineData(100, 140, 50, 10, false)] // 40 ms but only 40 %
    [InlineData(100, 160, 50, 10, true)]  // 60 ms and 60 %
    [InlineData(100, 140, 20, 10, true)]  // threshold made configurable
    [InlineData(100, 140, 20, 50, false)] // minimum made configurable
    [InlineData(40, 10, 50, 10, true)]    // faster counts as well
    public void Compute_LatencyNeedsBothThresholds(double before, double after, double percent, double minMs, bool changed)
    {
        var a = Run(1, T0, [Dev("192.0.2.1", "AA:00:00:00:00:01", null, before)]);
        var b = Run(2, T0.AddHours(1), [Dev("192.0.2.1", "AA:00:00:00:00:01", null, after)]);

        var result = ScanCompare.Compute(a, b, new ScanCompareOptions(percent, minMs));

        Assert.Equal(changed, result.Changes.Any(c => c.Type == ScanChangeType.LatencyChanged));
    }

    [Fact]
    public void Compute_PortsAreOnlyComparedWhenBothRunsScannedTheHost()
    {
        var a = Run(1, T0, [Dev("192.0.2.1", "AA:00:00:00:00:01", null, 2)]);
        var b = Run(2, T0.AddHours(1), [Dev("192.0.2.1", "AA:00:00:00:00:01", null, 2)], Ports("192.0.2.1", "80/TCP"));

        var result = ScanCompare.Compute(a, b);

        Assert.Empty(result.Changes);
        Assert.False(result.PortsCompared);
    }

    [Fact]
    public void Export_JsonIsValidAndCarriesEveryChange()
    {
        var (a, b) = Fabricated();
        var result = ScanCompare.Compute(a, b);

        using var doc = JsonDocument.Parse(ScanCompareExport.ToJson(result));
        var root = doc.RootElement;
        var changes = root.GetProperty("changes");
        Assert.Equal(7, changes.GetArrayLength());
        Assert.Equal(new[] { "gone", "ipChanged", "latencyChanged", "macChanged", "new", "portsClosed", "portsOpened" },
            changes.EnumerateArray().Select(c => c.GetProperty("type").GetString()!).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(1, root.GetProperty("summary").GetProperty("macChanged").GetInt32());
        Assert.Equal(50, root.GetProperty("options").GetProperty("latencyPercent").GetDouble());
        Assert.Equal(1, root.GetProperty("runA").GetProperty("id").GetInt64());
    }

    [Fact]
    public void Export_JsonOfIdenticalRunsIsValidAndEmpty()
    {
        var (a, _) = Fabricated();
        using var doc = JsonDocument.Parse(ScanCompareExport.ToJson(ScanCompare.Compute(a, a)));
        Assert.Equal(0, doc.RootElement.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void Compute_SkipsPortsAgainstAnAggregatedRun()
    {
        var (a, b) = Fabricated();
        var merged = b with { Scan = b.Scan with { SampleCount = 3 } };

        var result = ScanCompare.Compute(a, merged);

        Assert.False(result.PortsCompared);
        Assert.DoesNotContain(result.Changes, c => c.Type is ScanChangeType.PortsOpened or ScanChangeType.PortsClosed);
        Assert.Contains(result.Changes, c => c.Type == ScanChangeType.New);
    }

    [Fact]
    public void Export_XmlParsesAndCarriesEveryChange()
    {
        var (a, b) = Fabricated();
        var doc = System.Xml.Linq.XDocument.Parse(ScanCompareExport.ToXml(ScanCompare.Compute(a, b)));
        var root = doc.Root!;

        Assert.Equal("scanCompare", root.Name.LocalName);
        Assert.Equal(7, root.Element("changes")!.Elements("change").Count());
        Assert.Equal("1", root.Element("summary")!.Elements("count").Single(c => (string?)c.Attribute("type") == "macChanged").Attribute("value")!.Value);
        Assert.Equal("1", root.Element("runA")!.Attribute("id")!.Value);
    }

    [Fact]
    public void Export_XmlAndGrepableSurviveHostNamesWithControlCharacters()
    {
        var (a, b) = Fabricated();
        var odd = new ScanChange(ScanChangeType.New, "192.0.2.99", null, "bad\u0001na\tme\r\n<x>&", null, null);
        var result = ScanCompare.Compute(a, b) with { Changes = [odd] };

        var doc = System.Xml.Linq.XDocument.Parse(ScanCompareExport.ToXml(result));
        var name = doc.Root!.Element("changes")!.Element("change")!.Attribute("hostname")!.Value;
        Assert.DoesNotContain('\u0001', name);
        Assert.Contains("<x>&", name);

        var lines = ScanCompareExport.ToGrepable(result).TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal(6, lines[1].Split('\t').Length);
        Assert.Contains("Name: bad na me  <x>&", lines[1]);
        Assert.DoesNotContain('\r', lines[1]);
    }

    [Fact]
    public void Export_GrepableHasOneLinePerChange()
    {
        var (a, b) = Fabricated();
        var lines = ScanCompareExport.ToGrepable(ScanCompare.Compute(a, b)).TrimEnd('\n').Split('\n');

        Assert.Equal(1 + 7, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("Host: ", l));
        Assert.Contains(lines, l => l.Contains("Host: 192.0.2.20") && l.Contains("Change: ipChanged") && l.Contains("Before: 192.0.2.12"));
    }

    [Fact]
    public void Export_TextGroupsByChangeType()
    {
        var (a, b) = Fabricated();
        var text = ScanCompareExport.ToText(ScanCompare.Compute(a, b));

        foreach (var type in Enum.GetValues<ScanChangeType>())
            Assert.Contains(ScanCompareExport.TypeLabel(type) + " (1)", text);
        Assert.Contains("192.0.2.20  mover  192.0.2.12 -> 192.0.2.20", text);
    }

    [Fact]
    public void SavedArchive_KeepsPortsSoRunsCanBeCompared()
    {
        var db = new KnownDevicesDb(Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db"));
        Device Host(double ms)
        {
            var d = new Device("192.0.2.5") { Mac = "AA:00:00:00:00:05", Hostname = "box" };
            d.RecordPing(new PingResult(true, ms, 64));
            return d;
        }
        IReadOnlyDictionary<string, IReadOnlyList<NetService>> Open(params int[] ports) =>
            new Dictionary<string, IReadOnlyList<NetService>>
            { ["192.0.2.5"] = ports.Select(p => new NetService("svc", p, NetProtocol.Tcp)).ToArray() };
        var idA = db.SaveScanArchive(T0, T0.AddMinutes(1), "n", ["192.0.2.0/24"], "{}", [Host(2)], [], false, Open(80));
        var idB = db.SaveScanArchive(T0.AddDays(1), T0.AddDays(1).AddMinutes(1), "n", ["192.0.2.0/24"], "{}", [Host(2)], [], false, Open(80, 443));

        var result = ScanCompare.Compute(db.LoadScanArchive(idA)!, db.LoadScanArchive(idB)!);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ScanChangeType.PortsOpened, change.Type);
        Assert.Equal("443/TCP", change.After);
    }

    [Fact]
    public async Task Page_WithOneRunShowsAnExplanatoryEmptyState()
    {
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        var db = new KnownDevicesDb(dbPath);
        var d = new Device("192.0.2.5") { Mac = "AA:00:00:00:00:05" };
        d.RecordPing(new PingResult(true, 2, 64));
        db.SaveScanArchive(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now, "n", ["192.0.2.0/24"], "{}", [d], []);

        var state = await WpfTestHost.RunAsync(async () =>
        {
            var page = new ArchivePage();
            await page.OpenAsync(new ScanConfig { DatabasePath = dbPath });
            await page.ReviewShowCompareAsync();
            return (page.CompareArea.Visibility, page.CompareEmptyText.Visibility, page.CompareEmptyText.Text,
                page.CompareGrid.Visibility, page.MainArea.Visibility);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(System.Windows.Visibility.Visible, state.Item1);
        Assert.Equal(System.Windows.Visibility.Visible, state.Item2);
        Assert.Contains("1", state.Item3);
        Assert.Equal(string.Format(Loc.CompareNeedTwoRunsFmt, 1), state.Item3);
        Assert.Equal(System.Windows.Visibility.Collapsed, state.Item4);
        Assert.Equal(System.Windows.Visibility.Collapsed, state.Item5);
    }

    [Fact]
    public async Task Page_WithAnUnusableThresholdClearsTheViewAndDisablesExport()
    {
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        var db = new KnownDevicesDb(dbPath);
        var d = new Device("192.0.2.5") { Mac = "AA:00:00:00:00:05" };
        d.RecordPing(new PingResult(true, 2, 64));
        db.SaveScanArchive(T0, T0.AddMinutes(1), "n", ["192.0.2.0/24"], "{}", [d], []);
        db.SaveScanArchive(T0.AddDays(1), T0.AddDays(1).AddMinutes(1), "n", ["192.0.2.0/24"], "{}", [d], []);

        var state = await WpfTestHost.RunAsync(async () =>
        {
            var page = new ArchivePage();
            await page.OpenAsync(new ScanConfig { DatabasePath = dbPath });
            await page.ReviewShowCompareAsync();
            var validEnabled = page.CompareJsonButton.IsEnabled;
            page.ComparePercentBox.Text = "abc";
            await page.ReviewShowCompareAsync();
            return (validEnabled, page.CompareJsonButton.IsEnabled, page.CompareCopyButton.IsEnabled,
                page.CompareHintText.Text, page.CompareGrid.ItemsSource);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(state.validEnabled);
        Assert.False(state.Item2);
        Assert.False(state.Item3);
        Assert.Equal(Loc.CompareThresholdInvalid, state.Item4);
        Assert.Null(state.Item5);
    }
}
