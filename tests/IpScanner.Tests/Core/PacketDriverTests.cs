using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PacketDriverState")]
public class PacketDriverTests
{
    private sealed class FakeProbe : IPacketDriverProbe
    {
        public string SystemDirectory => @"C:\Windows\System32";
        public HashSet<string> Files { get; } = new();
        public HashSet<string> Keys { get; } = new();
        public int? AdminOnly { get; set; }
        public int? ServiceStateValue { get; set; } = 4;
        public int? StartType { get; set; } = 2;
        public bool Elevated { get; set; }
        public bool FileExists(string path) => Files.Contains(path);
        public string FileVersion(string path) => "1.79";
        public bool RegistryKeyExists(string subKey) => Keys.Contains(subKey);
        public int? RegistryDword(string subKey, string name) => name == "Start" ? StartType : AdminOnly;
        public int? ServiceState(string serviceName) => ServiceStateValue;
        public bool IsElevated() => Elevated;

        public static FakeProbe Installed()
        {
            var p = new FakeProbe();
            p.Files.Add(Path.Combine(p.SystemDirectory, "Npcap", "wpcap.dll"));
            p.Files.Add(Path.Combine(p.SystemDirectory, "Npcap", "Packet.dll"));
            p.Keys.Add(PacketDriverDetector.NpcapKey);
            return p;
        }
    }

    [Fact]
    public void NothingInstalled_IsMissing()
    {
        var s = PacketDriverDetector.Detect(new FakeProbe());
        Assert.False(s.Available);
        Assert.Equal(PacketDriver.ReasonMissing, s.Reason);
    }

    [Fact]
    public void FilesWithoutRegistryKey_IsMissing()
    {
        var p = FakeProbe.Installed();
        p.Keys.Clear();
        Assert.Equal(PacketDriver.ReasonMissing, PacketDriverDetector.Detect(p).Reason);
    }

    [Fact]
    public void OneDllMissing_IsMissing()
    {
        var p = FakeProbe.Installed();
        p.Files.Remove(Path.Combine(p.SystemDirectory, "Npcap", "wpcap.dll"));
        Assert.False(PacketDriverDetector.Detect(p).Available);
    }

    [Fact]
    public void Installed_IsAvailableWithVersion()
    {
        var s = PacketDriverDetector.Detect(FakeProbe.Installed());
        Assert.True(s.Available);
        Assert.Null(s.Reason);
        Assert.Equal("1.79", s.Version);
    }

    [Fact]
    public void AdminOnlyInstall_NeedsElevation()
    {
        var p = FakeProbe.Installed();
        p.AdminOnly = 1;
        var s = PacketDriverDetector.Detect(p);
        Assert.False(s.Available);
        Assert.Equal(PacketDriver.ReasonAdmin, s.Reason);
        p.Elevated = true;
        Assert.True(PacketDriverDetector.Detect(p).Available);
    }

    [Fact]
    public void InstalledAndRunning_IsAvailable()
    {
        var p = FakeProbe.Installed();
        p.ServiceStateValue = 4;
        Assert.True(PacketDriverDetector.Detect(p).Available);
    }

    [Fact]
    public void InstalledButServiceStopped_IsReportedAsStopped()
    {
        var p = FakeProbe.Installed();
        p.ServiceStateValue = PacketDriverDetector.ServiceStopped;
        var s = PacketDriverDetector.Detect(p);
        Assert.False(s.Available);
        Assert.Equal(PacketDriver.ReasonStopped, s.Reason);
        Assert.Equal("1.79", s.Version);
    }

    [Fact]
    public void StoppedDemandStartService_IsStillAvailable()
    {
        var p = FakeProbe.Installed();
        p.ServiceStateValue = PacketDriverDetector.ServiceStopped;
        p.StartType = 3;
        Assert.True(PacketDriverDetector.Detect(p).Available);
    }

    [Fact]
    public void UnreadableServiceState_DoesNotBlock()
    {
        var p = FakeProbe.Installed();
        p.ServiceStateValue = null;
        Assert.True(PacketDriverDetector.Detect(p).Available);
    }

    [Fact]
    public void NotInstalled_StaysMissingEvenWithAStoppedState()
    {
        var p = new FakeProbe { ServiceStateValue = PacketDriverDetector.ServiceStopped };
        Assert.Equal(PacketDriver.ReasonMissing, PacketDriverDetector.Detect(p).Reason);
    }

    [Fact]
    public void NoticeMessage_DiffersPerReason()
    {
        var stopped = IpScanner.Core.Localization.Loc.DriverBarFor(PacketDriver.ReasonStopped);
        Assert.NotEqual(IpScanner.Core.Localization.Loc.DriverBarFor(PacketDriver.ReasonMissing), stopped);
        Assert.NotEqual(IpScanner.Core.Localization.Loc.DriverBarFor(PacketDriver.ReasonAdmin), stopped);
        Assert.Equal(IpScanner.Core.Localization.Loc.CapReasonStopped, IpScanner.Core.Localization.Loc.CapReasonText(PacketDriver.ReasonStopped));
    }

    [Fact]
    public void OverrideForTests_ReplacesDetection()
    {
        PacketDriver.OverrideForTests(new PacketDriverStatus(true, null, "x"));
        try
        {
            Assert.True(PacketDriver.Available);
            Assert.Equal("x", PacketDriver.Version);
        }
        finally { PacketDriver.OverrideForTests(null); }
    }

    private static Task NoDelay(TimeSpan _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private static readonly AsyncLocal<bool> OwnFlow = new();

    // Other test classes read the shared status in parallel; only this test's own calls reach the fake.
    private static Func<PacketDriverStatus> Own(Func<PacketDriverStatus> detect)
    {
        OwnFlow.Value = true;
        return () => OwnFlow.Value ? detect() : PacketDriverStatus.Missing;
    }

    private static Func<PacketDriverStatus> Script(params PacketDriverStatus[] steps)
    {
        int i = 0;
        return Own(() => steps[Math.Min(i++, steps.Length - 1)]);
    }

    [Fact]
    public async Task WaitForInstall_ReplacesTheCachedStatusOnceTheDriverShowsUp()
    {
        PacketDriver.ResetForTests();
        try
        {
            var installed = new PacketDriverStatus(true, null, "1.79");
            PacketDriver.DetectOverride = Script(PacketDriverStatus.Missing, PacketDriverStatus.Missing, installed, installed, installed);
            Assert.False(PacketDriver.Available);

            Assert.True(await PacketDriver.WaitForInstallAsync(CancellationToken.None, delay: NoDelay));

            Assert.True(PacketDriver.Available);
            Assert.Equal("1.79", PacketDriver.Version);
        }
        finally { PacketDriver.ResetForTests(); }
    }

    [Fact]
    public async Task WaitForInstall_NeedsTheDriverOnTwoPollsInARow()
    {
        PacketDriver.ResetForTests();
        try
        {
            var installed = new PacketDriverStatus(true, null, "1.79");
            // A single sighting while the installer is still copying files does not count.
            PacketDriver.DetectOverride = Script(installed, PacketDriverStatus.Missing, PacketDriverStatus.Missing);
            Assert.False(await PacketDriver.WaitForInstallAsync(CancellationToken.None, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), NoDelay));
            Assert.False(PacketDriver.Available);
        }
        finally { PacketDriver.ResetForTests(); }
    }

    [Fact]
    public async Task WaitForInstall_GivesUpAfterTheTimeout()
    {
        PacketDriver.ResetForTests();
        try
        {
            int polls = 0;
            PacketDriver.DetectOverride = Own(() => { polls++; return PacketDriverStatus.Missing; });
            Assert.False(await PacketDriver.WaitForInstallAsync(CancellationToken.None, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10), NoDelay));
            Assert.Equal(300, polls);
        }
        finally { PacketDriver.ResetForTests(); }
    }

    [Fact]
    public async Task WaitForInstall_StopsWhenCancelled()
    {
        PacketDriver.ResetForTests();
        try
        {
            using var cts = new CancellationTokenSource();
            int polls = 0;
            PacketDriver.DetectOverride = Own(() => { if (++polls == 3) cts.Cancel(); return PacketDriverStatus.Missing; });
            Assert.False(await PacketDriver.WaitForInstallAsync(cts.Token, delay: NoDelay));
            Assert.Equal(3, polls);
        }
        finally { PacketDriver.ResetForTests(); }
    }

    [Fact]
    public async Task WaitForInstall_AlsoEndsWhenTheInstallNeedsAdministratorRights()
    {
        PacketDriver.ResetForTests();
        try
        {
            var admin = new PacketDriverStatus(false, PacketDriver.ReasonAdmin, "1.79");
            PacketDriver.DetectOverride = Script(PacketDriverStatus.Missing, admin, admin);
            Assert.True(await PacketDriver.WaitForInstallAsync(CancellationToken.None, delay: NoDelay));
            Assert.Equal(PacketDriver.ReasonAdmin, PacketDriver.Status.Reason);
        }
        finally { PacketDriver.ResetForTests(); }
    }
}
