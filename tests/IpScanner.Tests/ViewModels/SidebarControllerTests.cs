using System;
using System.Collections.Generic;
using System.Linq;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

// The sidebar's add / edit / delete / arm / undo logic runs here without a window.
public class SidebarControllerTests
{
    private sealed class Rig
    {
        public ScanConfig Config = new() { Subnets = ["192.168.1.0/24 Office"], PinnedIps = ["10.0.0.5 Printer"], InternetHosts = ["8.8.8.8 Google"], PingCount = 0 };
        public int ConfigChanged, RefreshNetworks, RefreshPinned, RestartHosts, Persisted;
        public List<(EntryKind Kind, NetEntry Entry)> Removed = [];
        public List<Action> ScheduledTimeouts = [];
        public SidebarController Ctl;

        public Rig()
        {
            Ctl = new SidebarController(() => Config, new SidebarHooks
            {
                ConfigChanged = () => ConfigChanged++,
                RefreshNetworks = () => RefreshNetworks++,
                RefreshPinned = () => RefreshPinned++,
                RestartInternetHosts = () => RestartHosts++,
                Persist = () => Persisted++,
                EntryRemoved = (k, e) => Removed.Add((k, e)),
            }, new ArmThenConfirm(TimeSpan.FromSeconds(3), (a, _) => ScheduledTimeouts.Add(a)));
        }
    }

    private static NetworkInfoViewModel Net(bool primary, string? source, string name = "", string color = "") =>
        new(2, new NetworkInfo { Ip = "192.168.1.9", SubnetMask = "255.255.255.0" }, "#888888",
            primary: primary, name: name, sourceTarget: source, rawColor: color, prefix: "192.168.1");

    // Networks

    [Theory]
    [InlineData("2001:db8::/64")]
    [InlineData("fe80::/10")]
    public void Network_HugeIpv6Prefix_IsRefusedWithItsOwnError(string target)
    {
        var r = new Rig();
        var res = r.Ctl.Save(EntryKind.Network, null, false, target, "Lab", "");
        Assert.False(res.Saved);
        Assert.Equal(EntryError.PrefixTooLarge, res.Error);
        Assert.Single(r.Config.Subnets);
    }

    [Fact]
    public void Network_SmallIpv6_IsStoredShortAndGarbageStaysInvalid()
    {
        var r = new Rig();
        var ok = r.Ctl.Save(EntryKind.Network, null, false, "2001:0db8:0:0:0:0:0:0/120", "", "");
        Assert.True(ok.Saved);
        Assert.Contains(r.Config.Subnets, e => e.StartsWith("2001:db8::/120"));
        Assert.Equal(EntryError.Invalid, r.Ctl.Save(EntryKind.Network, null, false, "not-a-net", "", "").Error);
    }

    [Fact]
    public void Network_Add_StoresEntryAndRefreshesNetworks()
    {
        var r = new Rig();
        var res = r.Ctl.Save(EntryKind.Network, null, false, " 10.1.0.0/16 ", "Lab", "#112233");
        Assert.True(res.Saved);
        Assert.Contains("10.1.0.0/16 Lab ~112233", r.Config.Subnets);
        Assert.Equal(2, r.Config.Subnets.Count);
        Assert.Equal((1, 1, 1), (r.ConfigChanged, r.RefreshNetworks, r.Persisted));
        Assert.Equal(0, r.RefreshPinned + r.RestartHosts);
    }

    [Fact]
    public void Network_Edit_RenamesInPlaceWithoutDuplicate()
    {
        var r = new Rig();
        var res = r.Ctl.Save(EntryKind.Network, "192.168.1.0/24", false, "192.168.1.0/24", "HQ", "");
        Assert.True(res.Saved);
        Assert.Equal(["192.168.1.0/24 HQ"], r.Config.Subnets);
    }

    [Fact]
    public void Network_Edit_ChangingTheTargetReplacesTheOldEntry()
    {
        var r = new Rig();
        Assert.True(r.Ctl.Save(EntryKind.Network, "192.168.1.0/24", false, "172.16.0.0/24", "Office", "").Saved);
        Assert.Equal(["172.16.0.0/24 Office"], r.Config.Subnets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a subnet")]
    [InlineData("300.1.1.0/24")]
    public void Network_InvalidTarget_IsRefusedAndNothingChanges(string input)
    {
        var r = new Rig();
        var res = r.Ctl.Save(EntryKind.Network, null, false, input, "x", "");
        Assert.Equal(EntryError.Invalid, res.Error);
        Assert.Equal(["192.168.1.0/24 Office"], r.Config.Subnets);
        Assert.Equal(0, r.Persisted + r.ConfigChanged);
    }

    [Fact]
    public void Network_DuplicateTarget_IsRefused_ButSameEntryMaySaveAgain()
    {
        var r = new Rig();
        r.Config.Subnets.Add("10.2.0.0/24 Second");
        var dup = r.Ctl.Save(EntryKind.Network, "10.2.0.0/24", false, "192.168.1.0/24", "x", "");
        Assert.Equal(EntryError.Duplicate, dup.Error);
        Assert.Equal(2, r.Config.Subnets.Count);
        Assert.True(r.Ctl.Save(EntryKind.Network, "10.2.0.0/24", false, "10.2.0.0/24", "Renamed", "").Saved);
    }

    [Fact]
    public void Network_DuplicateName_OnlyHints()
    {
        var r = new Rig();
        var res = r.Ctl.Save(EntryKind.Network, null, false, "10.9.0.0/24", "office", "");
        Assert.True(res.Saved);
        Assert.True(res.DuplicateName);
        // Re-saving an entry under its own name is no clash.
        Assert.False(new Rig().Ctl.Save(EntryKind.Network, "192.168.1.0/24", false, "192.168.1.0/24", "Office", "").DuplicateName);
    }

    [Fact]
    public void PrimaryNetwork_KeepsItsOwnTargetAndBlankSaveAddsNothing()
    {
        var r = new Rig();
        r.Config.Subnets.Clear();
        // Read-only target: the typed text is ignored, the source key wins.
        var res = r.Ctl.Save(EntryKind.Network, "172.20.0.0/24", true, "garbage", "", "");
        Assert.True(res.Saved);
        Assert.Empty(r.Config.Subnets);
        Assert.True(r.Ctl.Save(EntryKind.Network, "172.20.0.0/24", true, "garbage", "Home", "#AABBCC").Saved);
        Assert.Equal(["172.20.0.0/24 Home ~AABBCC"], r.Config.Subnets);
    }

    [Fact]
    public void Network_Delete_RemovesOffersUndoAndRestoreBringsItBack()
    {
        var r = new Rig();
        r.Ctl.Delete(EntryKind.Network, "192.168.1.0/24");
        Assert.Empty(r.Config.Subnets);
        var (kind, entry) = Assert.Single(r.Removed);
        Assert.Equal(EntryKind.Network, kind);
        Assert.Equal(new NetEntry("192.168.1.0/24", "Office", ""), entry);
        Assert.Equal(1, r.Persisted);

        r.Ctl.Restore(kind, entry);
        Assert.Equal(["192.168.1.0/24 Office"], r.Config.Subnets);
    }

    [Fact]
    public void Delete_OfAnUnknownKey_ChangesNothingAndOffersNoUndo()
    {
        var r = new Rig();
        r.Ctl.Delete(EntryKind.Pinned, "1.2.3.4");
        Assert.Equal(["10.0.0.5 Printer"], r.Config.PinnedIps);
        Assert.Empty(r.Removed);
    }

    // Pinned IPs

    [Theory]
    [InlineData("10.0.0.77", true)]
    [InlineData("nas.local", true)]
    [InlineData("bad host name", false)]
    [InlineData("-bad.local", false)]
    public void Pinned_AcceptsAddressOrHostname(string input, bool ok)
    {
        var r = new Rig();
        Assert.Equal(ok, r.Ctl.Save(EntryKind.Pinned, null, false, input, "n", "").Saved);
    }

    [Fact]
    public void Pinned_Add_RefreshesPinnedOnly_AndKeepsColor()
    {
        var r = new Rig();
        Assert.True(r.Ctl.Save(EntryKind.Pinned, null, false, "10.0.0.6", "Cam", "#445566").Saved);
        Assert.Contains("10.0.0.6 Cam ~445566", r.Config.PinnedIps);
        Assert.Equal((1, 0, 0), (r.RefreshPinned, r.RefreshNetworks, r.RestartHosts));
    }

    [Fact]
    public void Pinned_PinThenUnpin_RoundTrips()
    {
        var r = new Rig();
        r.Ctl.Pin("10.0.0.8");
        Assert.Contains("10.0.0.8", r.Config.PinnedIps);
        r.Ctl.Delete(EntryKind.Pinned, "10.0.0.8");
        Assert.DoesNotContain("10.0.0.8", r.Config.PinnedIps);
    }

    [Fact]
    public void Pinned_DuplicateTarget_IsRefused()
    {
        var r = new Rig();
        Assert.Equal(EntryError.Duplicate, r.Ctl.Save(EntryKind.Pinned, null, false, "10.0.0.5", "Again", "").Error);
    }

    // Internet hosts

    [Fact]
    public void Host_Add_RestartsThePingLoop_AndNeverStoresAColor()
    {
        var r = new Rig();
        Assert.True(r.Ctl.Save(EntryKind.Host, null, false, "1.1.1.1", "Cloudflare", "#123456").Saved);
        Assert.Contains("1.1.1.1 Cloudflare", r.Config.InternetHosts);
        Assert.Equal((1, 0, 0), (r.RestartHosts, r.RefreshNetworks, r.RefreshPinned));
    }

    [Fact]
    public void Host_RejectsHostnames_AndBadAddresses()
    {
        var r = new Rig();
        Assert.Equal(EntryError.Invalid, r.Ctl.Save(EntryKind.Host, null, false, "dns.google", "x", "").Error);
        Assert.Equal(EntryError.Invalid, r.Ctl.Save(EntryKind.Host, null, false, "8.8.8", "x", "").Error);
    }

    [Fact]
    public void Host_DeleteAndRestore_KeepTheName()
    {
        var r = new Rig();
        r.Ctl.Delete(EntryKind.Host, "8.8.8.8");
        Assert.Empty(r.Config.InternetHosts);
        r.Ctl.Restore(EntryKind.Host, r.Removed[0].Entry);
        Assert.Equal(["8.8.8.8 Google"], r.Config.InternetHosts);
    }

    // Arm / disarm

    [Fact]
    public void DeleteRow_FirstClickArms_SecondClickRemoves()
    {
        var r = new Rig();
        var row = new PinnedIpViewModel("10.0.0.5", "Printer", "");
        Assert.True(r.Ctl.DeleteRow(row));
        Assert.True(row.IsDeleteArmed);
        Assert.Equal(["10.0.0.5 Printer"], r.Config.PinnedIps);

        Assert.True(r.Ctl.DeleteRow(row));
        Assert.False(row.IsDeleteArmed);
        Assert.Empty(r.Config.PinnedIps);
        Assert.Single(r.Removed);
    }

    [Fact]
    public void DeleteRow_TimeoutDisarms_SoTheNextClickArmsAgain()
    {
        var r = new Rig();
        var row = new InternetHostViewModel("Google", "8.8.8.8");
        r.Ctl.DeleteRow(row);
        Assert.Single(r.ScheduledTimeouts);
        r.ScheduledTimeouts[0]();
        Assert.False(row.IsDeleteArmed);
        r.Ctl.DeleteRow(row);
        Assert.Equal(["8.8.8.8 Google"], r.Config.InternetHosts);
        Assert.True(row.IsDeleteArmed);
    }

    [Fact]
    public void DeleteRow_ClickOnAnotherRow_MovesTheArm()
    {
        var r = new Rig();
        r.Config.PinnedIps.Add("10.0.0.6");
        var a = new PinnedIpViewModel("10.0.0.5", "Printer", "");
        var b = new PinnedIpViewModel("10.0.0.6", "", "");
        r.Ctl.DeleteRow(a);
        r.Ctl.DeleteRow(b);
        Assert.False(a.IsDeleteArmed);
        Assert.True(b.IsDeleteArmed);
        Assert.Equal(2, r.Config.PinnedIps.Count);
    }

    [Fact]
    public void Disarm_ClearsTheArmedRow()
    {
        var r = new Rig();
        var row = new PinnedIpViewModel("10.0.0.5", "Printer", "");
        r.Ctl.DeleteRow(row);
        r.Ctl.Disarm();
        Assert.False(row.IsDeleteArmed);
    }

    [Fact]
    public void DeleteRow_PrimaryNetworkAndUnknownRows_AreNotHandled()
    {
        var r = new Rig();
        var primary = Net(primary: true, source: null);
        Assert.False(r.Ctl.DeleteRow(primary));
        Assert.False(primary.IsDeleteArmed);
        Assert.False(r.Ctl.DeleteRow("something else"));
        Assert.False(r.Ctl.DeleteRow(null));
    }

    [Fact]
    public void DeleteRow_Network_ArmsThenRemovesBySourceTarget()
    {
        var r = new Rig();
        var net = Net(primary: false, source: "192.168.1.0/24", name: "Office");
        r.Ctl.DeleteRow(net);
        r.Ctl.DeleteRow(net);
        Assert.Empty(r.Config.Subnets);
        Assert.Equal(EntryKind.Network, r.Removed[0].Kind);
    }

    // Edit popup specs

    [Fact]
    public void EditSpec_PrimaryNetwork_HasReadOnlyTargetAndNoDelete()
    {
        var r = new Rig();
        var spec = r.Ctl.EditSpecFor(Net(primary: true, source: null, name: "Home", color: "#112233"))!;
        Assert.Equal(EntryKind.Network, spec.Kind);
        Assert.True(spec.TargetReadOnly);
        Assert.False(spec.CanDelete);
        Assert.Equal("192.168.1.0/24", spec.Target);
        Assert.Equal(("Home", "#112233"), (spec.Name, spec.Color));
    }

    [Fact]
    public void EditSpec_ForEachRowKind_CarriesItsKeyAndDeletable()
    {
        var r = new Rig();
        var net = r.Ctl.EditSpecFor(Net(primary: false, source: "192.168.1.0/24"))!;
        Assert.True(net.CanDelete);
        Assert.Equal("192.168.1.0/24", net.SourceKey);

        var pin = r.Ctl.EditSpecFor(new PinnedIpViewModel("10.0.0.5", "Printer", "#010203"))!;
        Assert.Equal((EntryKind.Pinned, "10.0.0.5", "Printer"), (pin.Kind, pin.SourceKey, pin.Name));

        var host = r.Ctl.EditSpecFor(new InternetHostViewModel("Google", "8.8.8.8"))!;
        Assert.Equal((EntryKind.Host, "8.8.8.8", ""), (host.Kind, host.SourceKey, host.Color));

        Assert.Null(r.Ctl.EditSpecFor("nope"));
    }

    [Fact]
    public void AddSpec_IsEmptyWithNoSourceKeyAndNoDelete()
    {
        var r = new Rig();
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var spec = r.Ctl.AddSpecFor(kind);
            Assert.Equal(kind, spec.Kind);
            Assert.Null(spec.SourceKey);
            Assert.False(spec.CanDelete);
            Assert.False(spec.TargetReadOnly);
            Assert.Equal("", spec.Target);
            Assert.False(string.IsNullOrWhiteSpace(spec.Title));
        }
    }

    // Import

    [Fact]
    public void ReplaceAll_SetsTheListAndRefreshesOnce()
    {
        var r = new Rig();
        r.Ctl.ReplaceAll(EntryKind.Pinned, ["10.9.9.9 A", "10.9.9.10"]);
        Assert.Equal(["10.9.9.9 A", "10.9.9.10"], r.Config.PinnedIps);
        Assert.Equal((1, 1, 1), (r.ConfigChanged, r.RefreshPinned, r.Persisted));
    }

    [Fact]
    public void TheControllerFollowsAReplacedConfig()
    {
        var r = new Rig();
        r.Config = new ScanConfig { PinnedIps = [] };   // e.g. a profile switch swaps the whole object
        r.Ctl.Pin("10.7.7.7");
        Assert.Equal(["10.7.7.7"], r.Config.PinnedIps);
    }
}
