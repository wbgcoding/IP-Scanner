using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class PrivateMacVendorTests
{
    // Second-least-significant bit of the first octet set (0x02): a random, self-assigned address.
    private const string PrivateMac = "DA:A1:19:00:00:01";
    // A global (not locally administered) address in no registered block.
    private const string UnregisteredGlobalMac = "FC:FF:FF:FF:FF:FE";

    private static DeviceViewModel Row(string mac, bool online = true)
    {
        var d = new Device("192.0.2.40") { Mac = mac };
        if (online) d.RecordPing(new PingResult(true, 1.0, 64));
        return new DeviceViewModel(d);
    }

    [Fact]
    public void PrivateMacWithoutVendor_ShowsPrivateAddressAndATooltip()
    {
        var vm = Row(PrivateMac);

        Assert.Equal("–", vm.Vendor);
        Assert.True(vm.IsPrivateMac);
        Assert.Equal(Loc.PrivateAddress, vm.VendorDisplay);
        Assert.Equal(Loc.PrivateAddressTip, vm.VendorTip);
    }

    [Fact]
    public void TextsAreTheDecidedWording()
    {
        Assert.Contains(Loc.PrivateAddress, new[] { "Private Adresse", "Private address" });
        Assert.Contains(Loc.PrivateAddressTip, new[]
        {
            "Zufällige, vom Gerät selbst vergebene MAC-Adresse (Datenschutzfunktion von Handys und Laptops).",
            "Random MAC address the device picked itself (privacy feature of phones and laptops).",
        });
    }

    [Fact]
    public void GlobalMacWithoutVendor_KeepsTheDash()
    {
        var vm = Row(UnregisteredGlobalMac);

        Assert.False(ConflictDetector.IsLocallyAdministered(UnregisteredGlobalMac));
        Assert.False(vm.IsPrivateMac);
        Assert.Equal(vm.Vendor, vm.VendorDisplay);
        Assert.Null(vm.VendorTip);
    }

    [Fact]
    public void RowWithoutAMac_IsNotPrivate()
    {
        var vm = Row(Device.Unknown);
        Assert.False(vm.IsPrivateMac);
        Assert.Equal("–", vm.VendorDisplay);
    }

    [Fact]
    public void UnknownQuickFilter_StillIncludesAPrivateMacDevice()
    {
        var vm = Row(PrivateMac);

        Assert.True(vm.IsRogue);
        Assert.True(DeviceFilter.PassesQuickFilters(onlyOnline: false, onlyPinned: false, onlyUnknown: true,
            vm.IsOnline, vm.IsPinned, vm.IsRogue));
    }

    [Fact]
    public void TextFilter_StillSeesTheRawVendorNotTheNote()
    {
        var vm = Row(PrivateMac);
        Assert.False(DeviceFilter.Matches("Private", vm.Ip, vm.Hostname, vm.Mac, vm.Vendor, new int[0]));
    }
}
