using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceTypeDetectorTests
{
    [Theory]
    [InlineData("fritz.box", null, DeviceType.Router)]
    [InlineData(null, "Ubiquiti Networks Inc.", DeviceType.Router)]
    [InlineData("office-printer", null, DeviceType.Printer)]
    [InlineData(null, "Hewlett Packard", DeviceType.Printer)]
    [InlineData("front-door-camera", null, DeviceType.Camera)]
    [InlineData(null, "Hikvision", DeviceType.Camera)]
    [InlineData("Bens-iPhone", null, DeviceType.Phone)]
    [InlineData(null, "Samsung Electronics Co.,Ltd", DeviceType.Phone)]
    [InlineData("living-room-tv", null, DeviceType.Tv)]
    [InlineData(null, "Roku, Inc", DeviceType.Tv)]
    [InlineData("backup-nas", null, DeviceType.Nas)]
    [InlineData(null, "Synology Incorporated", DeviceType.Nas)]
    [InlineData("db-server-01", null, DeviceType.Server)]
    [InlineData("office-desktop", null, DeviceType.Pc)]
    [InlineData(null, "Dell Inc.", DeviceType.Pc)]
    [InlineData("random-thing-123", "Unbekannt", DeviceType.Unknown)]
    [InlineData(null, null, DeviceType.Unknown)]
    public void Detect_HostnameOrVendorHint_ReturnsExpectedType(string? hostname, string? vendor, DeviceType expected)
        => Assert.Equal(expected, DeviceTypeDetector.Detect(hostname, vendor));

    [Fact]
    public void Detect_NoHint_ButNasPortSignature_ReturnsPc()
    {
        // Ports 445/3389 alone (no naming hint) are the Windows-share/RDP
        // signature - not enough to call it a NAS just because a NAS also
        // often has 445 open. This documents the deliberate priority: an
        // explicit NAS-vendor/hostname hint always wins over the port guess.
        Assert.Equal(DeviceType.Pc, DeviceTypeDetector.Detect(null, null, new[] { 445 }));
    }

    [Fact]
    public void Detect_PrinterPortWithoutHostnameHint_ReturnsPrinter()
    {
        Assert.Equal(DeviceType.Printer, DeviceTypeDetector.Detect(null, null, new[] { 9100 }));
    }

    [Fact]
    public void Detect_ServerPortCombo_SshPlusWeb_ReturnsServer()
    {
        Assert.Equal(DeviceType.Server, DeviceTypeDetector.Detect(null, null, new[] { 22, 443 }));
    }

    [Fact]
    public void IconKey_MatchesResourceNamingConvention()
    {
        Assert.Equal("IconServer", DeviceTypeDetector.IconKey(DeviceType.Server));
        Assert.Equal("IconUnknown", DeviceTypeDetector.IconKey(DeviceType.Unknown));
    }
}
