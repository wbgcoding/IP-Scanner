using System;
using System.IO;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Os;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PacketDriverState")]
public class OsDisplayTests
{
    private static Device Online(string ip, int? ttl)
    {
        var d = new Device(ip);
        d.RecordPing(new PingResult(true, 1.0, ttl));
        return d;
    }

    [Fact]
    public void Config_OsFingerprint_DefaultsOff_AndRoundTrips()
    {
        Assert.False(new ScanConfig().OsFingerprint);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, new ScanConfig { OsFingerprint = true });
        Assert.True(ConfigManager.Load(path).OsFingerprint);
        ConfigManager.Save(path, new ScanConfig { OsFingerprint = false });
        Assert.False(ConfigManager.Load(path).OsFingerprint);
    }

    [Fact]
    public void Config_Clone_KeepsOsFingerprint()
    {
        Assert.True(new ScanConfig { OsFingerprint = true }.Clone().OsFingerprint);
    }

    [Fact]
    public void Text_IsEmptyWhenUnknown_AndAlwaysMarkedAsEstimate()
    {
        Assert.Equal("", OsDisplay.Text(OsEstimate.None));
        var t = OsDisplay.Text(new OsEstimate(OsFamily.Windows, 71));
        Assert.Equal(string.Format(Loc.OsEstimateFmt, Loc.OsFamilyWindows, 71), t);
        Assert.Contains("71", t);
    }

    [Fact]
    public void Text_ForFingerprint_NamesMatchQualityNotADistance()
    {
        var t = OsDisplay.Text(new OsEstimate(OsFamily.UnixLike, 80, true, "Linux 4.x", 92));
        Assert.Equal(string.Format(Loc.OsFingerprintFmt, "Linux 4.x", 92, 80), t);
        Assert.DoesNotContain("istance", t);
        Assert.DoesNotContain("Abstand", t);
    }

    [Fact]
    public void Text_ForFingerprint_ShowsKnownHopsSeparately()
    {
        var withHops = OsDisplay.Text(new OsEstimate(OsFamily.UnixLike, 80, true, "Linux 4.x", 92, 3));
        Assert.EndsWith(string.Format(Loc.OsHopsFmt, 3), withHops);
        var without = OsDisplay.Text(new OsEstimate(OsFamily.UnixLike, 80, true, "Linux 4.x", 92));
        Assert.DoesNotContain(string.Format(Loc.OsHopsFmt, 0), without);
    }

    [Fact]
    public void ViewModel_ShowsHeuristicFromTtl_AndPortsAfterTheyAreKnown()
    {
        var vm = new DeviceViewModel(Online("192.0.2.20", 128));
        Assert.Contains(Loc.OsFamilyWindows, vm.Os);
        Assert.NotEqual("", vm.Os);
        Assert.Equal(Loc.OsTipHeuristic, vm.OsTip);
    }

    [Fact]
    public void ViewModel_WithoutTtlAndPorts_IsEmptyWithExplainingTip()
    {
        var vm = new DeviceViewModel(new Device("192.0.2.21"));
        Assert.Equal("", vm.Os);
        Assert.Equal(Loc.OsTipNone, vm.OsTip);
    }

    [Fact]
    public void ViewModel_Tip_NamesTheDegradeWhenFingerprintIsOnButNoDriver()
    {
        var old = OsFingerprinter.Enabled;
        PacketDriver.OverrideForTests(new PacketDriverStatus(false, "missing"));
        try
        {
            OsFingerprinter.Enabled = true;
            var vm = new DeviceViewModel(Online("192.0.2.22", 64));
            Assert.Contains(Loc.OsFingerprintDegradeNote, vm.OsTip);
            OsFingerprinter.Enabled = false;
            Assert.DoesNotContain(Loc.OsFingerprintDegradeNote, new DeviceViewModel(Online("192.0.2.22", 64)).OsTip);
        }
        finally { OsFingerprinter.Enabled = old; PacketDriver.OverrideForTests(null); }
    }

    [Fact]
    public async Task ViewModel_Fingerprint_ReplacesTheHeuristic_WhenEnabledAndDriverPresent()
    {
        var oldEnabled = OsFingerprinter.Enabled;
        var oldNet = OsFingerprinter.Net;
        var net = new FakeDiscoveryNet();
        net.Link = (_, _) => new StackProfileLink(net.Route!, StackProfileLink.Windows10, 445);
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "1.0"), elevated: true);
        try
        {
            OsFingerprinter.Enabled = true;
            OsFingerprinter.Net = net;
            var dev = Online("10.0.0.7", 128);
            var vm = new DeviceViewModel(dev);
            vm.Services.Add(new NetService("SMB", 445, NetProtocol.Tcp));
            var changed = new System.Collections.Generic.List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            await vm.RunOsFingerprint(default);

            Assert.NotNull(dev.OsFingerprint);
            Assert.True(dev.OsFingerprint!.FromFingerprint);
            Assert.Contains(nameof(DeviceViewModel.Os), changed);
            Assert.Contains("Windows 10", vm.Os);
            Assert.Equal(Loc.OsTipFingerprint, vm.OsTip);
        }
        finally
        {
            OsFingerprinter.Enabled = oldEnabled;
            OsFingerprinter.Net = oldNet;
            PacketDriver.OverrideForTests(null);
        }
    }

    [Fact]
    public void Exports_CarryTheEstimate_AsTextWithPercent()
    {
        Loc.SetLanguage("en");
        var win = Online("10.0.0.10", 128);
        var ports = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<NetService>>
        {
            ["10.0.0.10"] = new[] { new NetService("SMB", 445, NetProtocol.Tcp), new NetService("RDP", 3389, NetProtocol.Tcp) },
        };
        var plain = Online("10.0.0.11", 64);
        var dir = Directory.CreateTempSubdirectory().FullName;
        var info = new NetworkInfo { Ip = "10.0.0.1", Gateway = "10.0.0.1" };
        var devices = new[] { win, plain };

        var csv = File.ReadAllText(IpScanner.Core.Export.CsvExporter.Write(devices, dir, "20260601_120000", "x", ports));
        var json = File.ReadAllText(IpScanner.Core.Export.JsonExporter.Write(devices, info, dir, "20260601_120000", "x", ports));
        var html = File.ReadAllText(IpScanner.Core.Export.HtmlExporter.Write(devices, info, dir, "20260601_120000", "x", null, ports));
        var txt = File.ReadAllText(IpScanner.Core.Export.TxtExporter.Write(devices, info, dir, "20260601_120000", "x", null, ports));

        var expected = OsDisplay.ForExport(win, ports);
        Assert.Contains("Windows", expected);
        Assert.Contains("estimate", expected);
        Assert.Contains("discovery_method,os_estimate,note", csv);
        Assert.Contains(expected, csv);
        Assert.Contains("\"os\":", json);
        Assert.Contains("\"family\": \"windows\"", json);
        Assert.Contains(Loc.OsColumnHeader, html);
        Assert.Contains(expected, html);
        Assert.Contains(Loc.OsColumnHeader, txt);
        Assert.Contains(expected, txt);
    }

    [Fact]
    public void OsColumn_IsHiddenByDefault_AndRoundTripsThroughHiddenColumns()
    {
        Assert.Contains("os", new ScanConfig().HiddenColumns);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, new ScanConfig { HiddenColumns = new() { "loss" } });
        Assert.DoesNotContain("os", ConfigManager.Load(path).HiddenColumns);
    }

    [Fact]
    public async Task ViewModel_Fingerprint_Disabled_SendsNothing()
    {
        var oldEnabled = OsFingerprinter.Enabled;
        var oldNet = OsFingerprinter.Net;
        var net = new FakeDiscoveryNet();
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "1.0"), elevated: true);
        try
        {
            OsFingerprinter.Enabled = false;
            OsFingerprinter.Net = net;
            var dev = Online("10.0.0.8", 64);
            await new DeviceViewModel(dev).RunOsFingerprint(default);
            Assert.Null(dev.OsFingerprint);
            Assert.Equal(0, net.Calls);
        }
        finally
        {
            OsFingerprinter.Enabled = oldEnabled;
            OsFingerprinter.Net = oldNet;
            PacketDriver.OverrideForTests(null);
        }
    }
}
