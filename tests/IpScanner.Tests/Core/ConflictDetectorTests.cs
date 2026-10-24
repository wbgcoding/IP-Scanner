using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ConflictDetectorTests
{
    [Fact]
    public void Detect_MacDifferentFromPreviousSnapshot_ReportsMacChanged()
    {
        var devices = new List<Device> { new("10.0.0.5") { Mac = "00:11:22:00:00:02" } };
        var previous = new Dictionary<string, string> { ["10.0.0.5"] = "00:11:22:00:00:01" };

        var findings = ConflictDetector.Detect(devices, previous);

        var f = Assert.Single(findings);
        Assert.Equal(ConflictKind.MacChanged, f.Kind);
        Assert.Equal("10.0.0.5", f.Ip);
        Assert.Equal("00:11:22:00:00:02", f.Mac);
        Assert.Equal("00:11:22:00:00:01", f.Detail);
    }

    [Fact]
    public void Detect_SameMacAsPrevious_ReportsNothing()
    {
        var devices = new List<Device> { new("10.0.0.5") { Mac = "00:11:22:00:00:01" } };
        var previous = new Dictionary<string, string> { ["10.0.0.5"] = "00:11:22:00:00:01" };

        Assert.Empty(ConflictDetector.Detect(devices, previous));
    }

    [Fact]
    public void Detect_PreviousMacWasLocallyAdministered_SuppressesMacChanged()
    {
        // 0x02 in the second-least-significant bit of the first octet = locally administered.
        var devices = new List<Device> { new("10.0.0.5") { Mac = "02:BB:CC:00:00:02" } };
        var previous = new Dictionary<string, string> { ["10.0.0.5"] = "02:BB:CC:00:00:01" };

        Assert.Empty(ConflictDetector.Detect(devices, previous));
    }

    [Fact]
    public void Detect_NoPreviousEntry_ReportsNothing()
    {
        var devices = new List<Device> { new("10.0.0.5") { Mac = "00:11:22:00:00:01" } };

        Assert.Empty(ConflictDetector.Detect(devices, new Dictionary<string, string>()));
    }

    [Fact]
    public void Detect_SameMacOnTwoIps_ReportsMacOnMultipleIpsForBoth()
    {
        var devices = new List<Device>
        {
            new("10.0.0.5") { Mac = "00:11:22:00:00:01" },
            new("10.0.0.6") { Mac = "00:11:22:00:00:01" },
        };

        var findings = ConflictDetector.Detect(devices, new Dictionary<string, string>());

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(ConflictKind.MacOnMultipleIps, f.Kind));
        var byIp = findings.ToDictionary(f => f.Ip);
        Assert.Equal("10.0.0.6", byIp["10.0.0.5"].Detail);
        Assert.Equal("10.0.0.5", byIp["10.0.0.6"].Detail);
    }

    [Fact]
    public void Detect_DeviceFlaggedThisRun_ReportsMultipleMacsForOneIp()
    {
        var devices = new List<Device> { new("10.0.0.5") { Mac = "00:11:22:00:00:01", HasMacConflictThisRun = true } };

        var findings = ConflictDetector.Detect(devices, new Dictionary<string, string>());

        var f = Assert.Single(findings);
        Assert.Equal(ConflictKind.MultipleMacsForOneIp, f.Kind);
        Assert.Equal("10.0.0.5", f.Ip);
    }

    [Theory]
    [InlineData("02:00:00:00:00:01", true)]
    [InlineData("06:00:00:00:00:01", true)]
    [InlineData("00:00:00:00:00:01", false)]
    [InlineData("00:11:22:00:00:01", false)]
    public void IsLocallyAdministered_ChecksSecondBitOfFirstOctet(string mac, bool expected)
    {
        Assert.Equal(expected, ConflictDetector.IsLocallyAdministered(mac));
    }

    [Fact]
    public void Detect_DeviceWithUnknownMac_IsIgnored()
    {
        var devices = new List<Device> { new("10.0.0.5") { Mac = Device.Unknown } };
        var previous = new Dictionary<string, string> { ["10.0.0.5"] = "00:11:22:00:00:01" };

        Assert.Empty(ConflictDetector.Detect(devices, previous));
    }
}
