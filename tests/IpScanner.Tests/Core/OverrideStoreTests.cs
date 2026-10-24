using System;
using System.IO;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class OverrideStoreTests
{
    private static string TempPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.conf");

    [Fact]
    public void SetAndGet_ByMacAndIpFallback()
    {
        var s = new OverrideStore();
        s.Load(TempPath());

        s.SetHostname(null, "192.168.1.5", "printer");
        Assert.Equal("printer", s.Get(null, "192.168.1.5")?.Hostname);

        // Once the MAC is known, the entry migrates to the MAC key.
        s.SetHostname("aa:bb:cc:11:22:33", "192.168.1.5", "printer2");
        Assert.Equal("printer2", s.Get("AA:BB:CC:11:22:33", "10.0.0.9")?.Hostname);
        Assert.Null(s.Get(null, "192.168.1.5"));
    }

    [Fact]
    public void Reset_RemovesEntry_ColorAndHostnameIndependent()
    {
        var s = new OverrideStore();
        s.Load(TempPath());

        s.SetHostname(null, "192.168.1.7", "nas");
        s.SetColor(null, "192.168.1.7", "#A6E3A1");
        s.SetHostname(null, "192.168.1.7", null);    // reset hostname only
        Assert.Null(s.Get(null, "192.168.1.7")?.Hostname);
        Assert.Equal("#A6E3A1", s.Get(null, "192.168.1.7")?.Color);

        s.SetColor(null, "192.168.1.7", null);       // reset color -> entry gone
        Assert.Null(s.Get(null, "192.168.1.7"));
    }

    [Fact]
    public void Save_RoundTripsThroughLoad()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname("AA:BB:CC:00:11:22", "192.168.1.9", "cam");
        a.SetColor("AA:BB:CC:00:11:22", "192.168.1.9", "#89B4FA");

        var b = new OverrideStore();
        b.Load(path);
        var e = b.Get("AA:BB:CC:00:11:22", "192.168.1.9");
        Assert.Equal("cam", e?.Hostname);
        Assert.Equal("#89B4FA", e?.Color);
    }

    // "Stop looking up this name" - the flag must survive a restart
    // (Save/Load round trip) exactly like hostname/color do.
    [Fact]
    public void SetNoHostnameRetry_RoundTripsThroughLoad()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetNoHostnameRetry("AA:BB:CC:00:11:33", "192.168.1.11", true);

        var b = new OverrideStore();
        b.Load(path);
        Assert.True(b.Get("AA:BB:CC:00:11:33", "192.168.1.11")?.NoHostnameRetry);
    }

    [Fact]
    public void SetNoHostnameRetry_False_RemovesEntry_WhenNoOtherDataSet()
    {
        var s = new OverrideStore();
        s.Load(TempPath());
        s.SetNoHostnameRetry(null, "192.168.1.12", true);
        Assert.True(s.Get(null, "192.168.1.12")?.NoHostnameRetry);

        s.SetNoHostnameRetry(null, "192.168.1.12", false);
        Assert.Null(s.Get(null, "192.168.1.12"));
    }

    // "Mute device" - survives a restart exactly like NoHostnameRetry.
    [Fact]
    public void SetMuted_RoundTripsThroughLoad()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetMuted("AA:BB:CC:00:11:55", "192.168.1.13", true);

        var b = new OverrideStore();
        b.Load(path);
        Assert.True(b.Get("AA:BB:CC:00:11:55", "192.168.1.13")?.Muted);
    }

    [Fact]
    public void SetMuted_False_RemovesEntry_WhenNoOtherDataSet()
    {
        var s = new OverrideStore();
        s.Load(TempPath());
        s.SetMuted(null, "192.168.1.14", true);
        Assert.True(s.Get(null, "192.168.1.14")?.Muted);

        s.SetMuted(null, "192.168.1.14", false);
        Assert.Null(s.Get(null, "192.168.1.14"));
    }

    [Fact]
    public void SetNote_RoundTripsByMacAcrossIpChange_AndSanitizesControls()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetNote("AA:BB:CC:00:11:77", "192.168.1.15", " first\r\nline" + (char)0 + " two\t ");

        var b = new OverrideStore();
        b.Load(path);

        Assert.Equal("first\nline two", b.Get("AA:BB:CC:00:11:77", "10.0.0.25")?.Note);
    }

    [Fact]
    public void PromoteToMac_MakesIpFallbackNoteSurviveAddressChange()
    {
        var path = TempPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetNote(null, "192.168.1.15", "printer room");
        a.PromoteToMac("AA:BB:CC:00:11:78", "192.168.1.15");

        var b = new OverrideStore();
        b.Load(path);

        Assert.Equal("printer room", b.Get("AA:BB:CC:00:11:78", "10.0.0.26")?.Note);
        Assert.Null(b.Get(null, "192.168.1.15"));
    }

    [Fact]
    public void SetNote_OverLimitIsRejectedWithoutChangingStoredNote()
    {
        var s = new OverrideStore();
        s.Load(TempPath());
        s.SetNote(null, "192.168.1.16", "keep me");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            s.SetNote(null, "192.168.1.16", new string('x', OverrideStore.MaxNoteLength + 1)));
        Assert.Equal("keep me", s.Get(null, "192.168.1.16")?.Note);
    }

    [Fact]
    public void FiveColumnLegacyLine_MigratesToVersionedFormatIdempotently()
    {
        var datPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.dat");
        var legacyPath = Path.ChangeExtension(datPath, ".conf");
        File.WriteAllText(legacyPath, "AA:BB:CC:00:11:88\tprinter\tN/A\t1\t1\n");

        var first = new OverrideStore();
        first.Load(datPath);
        first.SetNote("AA:BB:CC:00:11:88", "192.168.1.17", "legacy device");
        var second = new OverrideStore();
        second.Load(datPath);
        var entry = second.Get("AA:BB:CC:00:11:88", "10.0.0.17");
        var third = new OverrideStore();
        third.Load(datPath);

        Assert.False(File.Exists(legacyPath));
        Assert.Equal("printer", entry?.Hostname);
        Assert.True(entry?.NoHostnameRetry);
        Assert.True(entry?.Muted);
        Assert.Equal("legacy device", entry?.Note);
        Assert.Equal(entry, third.Get("AA:BB:CC:00:11:88", "10.0.0.17"));
    }

    // An older overrides file only ever had three tab-separated columns
    // (mac/ip, hostname, color) - the legacy-migration path is the one place
    // that still reads a plain, unencrypted file, so it doubles as the easiest
    // way to feed the parser a genuinely 3-column line.
    [Fact]
    public void ThreeColumnLegacyLine_StillParses_NoHostnameRetryDefaultsFalse()
    {
        var datPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.dat");
        var legacyPath = Path.ChangeExtension(datPath, ".conf");
        File.WriteAllText(legacyPath, "AA:BB:CC:00:11:44\tprinter\tN/A\n");

        var s = new OverrideStore();
        s.Load(datPath);
        var e = s.Get("AA:BB:CC:00:11:44", "10.0.0.1");
        Assert.Equal("printer", e?.Hostname);
        Assert.False(e?.NoHostnameRetry);
    }

    private static string NewDatPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.dat");

    [Fact]
    public void LegacyMigration_FailedSave_KeepsTheLegacyFile()
    {
        var path = NewDatPath();
        var legacy = Path.ChangeExtension(path, ".conf");
        File.WriteAllText(legacy, "AA:BB:CC:00:11:22\tcam\t#89B4FA\n");
        Directory.CreateDirectory(path + ".tmp");   // the save cannot create its temp file

        var s = new OverrideStore();
        s.Load(path);

        Assert.True(File.Exists(legacy));
        Assert.False(File.Exists(path));
        Assert.Equal("cam", s.Get("AA:BB:CC:00:11:22", "10.0.0.1")?.Hostname);   // still usable in memory
    }

    [Fact]
    public void LegacyMigration_SuccessfulSave_DeletesTheLegacyFile()
    {
        var path = NewDatPath();
        var legacy = Path.ChangeExtension(path, ".conf");
        File.WriteAllText(legacy, "AA:BB:CC:00:11:22\tcam\t#89B4FA\n");

        var s = new OverrideStore();
        s.Load(path);

        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CorruptedFile_IsMovedAside_AndTheBackupLoads()
    {
        var path = NewDatPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname(null, "10.0.0.1", "first");
        a.SetHostname(null, "10.0.0.2", "second");   // second save: .bak now holds the first generation
        Assert.True(File.Exists(path + ".bak"));

        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 });

        var b = new OverrideStore();
        b.Load(path);

        Assert.Equal(LoadProblem.RestoredFromBackup, b.LoadProblem);
        Assert.Equal("first", b.Get(null, "10.0.0.1")?.Hostname);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "overrides.dat.unreadable-*"));
        Assert.True(File.Exists(path));   // restored content is the live file again

        var c = new OverrideStore();
        c.Load(path);
        Assert.Equal(LoadProblem.None, c.LoadProblem);
        Assert.Equal("first", c.Get(null, "10.0.0.1")?.Hostname);
    }

    [Fact]
    public void CorruptedFile_WithoutBackup_IsMovedAside_AndStartsEmpty()
    {
        var path = NewDatPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname(null, "10.0.0.1", "only");
        Assert.False(File.Exists(path + ".bak"));
        File.WriteAllBytes(path, new byte[40]);

        var b = new OverrideStore();
        b.Load(path);

        Assert.Equal(LoadProblem.Unreadable, b.LoadProblem);
        Assert.Null(b.Get(null, "10.0.0.1"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "overrides.dat.unreadable-*"));
    }

    [Fact]
    public void HealthyFile_ReportsNoProblem()
    {
        var path = NewDatPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname(null, "10.0.0.1", "x");
        var b = new OverrideStore();
        b.Load(path);
        Assert.Equal(LoadProblem.None, b.LoadProblem);
    }

    [Fact]
    public void KeyFileFromAnotherAccount_ReportsKeyUnavailable_AndLeavesTheFileUntouched()
    {
        var path = NewDatPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname(null, "10.0.0.1", "x");
        var before = File.ReadAllBytes(path);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, KnownDevicesDb.KeyFileName), new byte[] { 9, 9, 9 });   // not a DPAPI blob

        var b = new OverrideStore();
        b.Load(path);
        b.SetHostname(null, "10.0.0.2", "ignored");

        Assert.Equal(LoadProblem.KeyUnavailable, b.LoadProblem);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void LockedFile_CannotBeMovedAside_StaysReadOnlyAndKeepsBothGenerations()
    {
        var path = NewDatPath();
        var a = new OverrideStore();
        a.Load(path);
        a.SetHostname(null, "10.0.0.1", "first");
        a.SetHostname(null, "10.0.0.2", "second");
        var live = File.ReadAllBytes(path);
        var bak = File.ReadAllBytes(path + ".bak");

        OverrideStore b;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            b = new OverrideStore();
            b.Load(path);
            b.SetHostname(null, "10.0.0.3", "edit while locked");
        }

        Assert.NotEqual(LoadProblem.None, b.LoadProblem);
        Assert.Equal(live, File.ReadAllBytes(path));
        Assert.Equal(bak, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "overrides.dat.unreadable-*"));
    }

    [Fact]
    public void FileFromANewerBuild_IsKeptInPlace_ReportedAsNewerFormat_AndNeverOverwritten()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "overrides.dat");
        var crypto = LocalCrypto.TryLoad(Path.Combine(dir, KnownDevicesDb.KeyFileName))!;
        File.WriteAllBytes(path, crypto.Seal("# overrides-v3\n10.0.0.1\tfuture\tN/A\t0\t0\n"));
        var before = File.ReadAllBytes(path);

        var store = new OverrideStore();
        store.Load(path);
        store.SetHostname(null, "10.0.0.2", "ignored");

        Assert.Equal(LoadProblem.NewerFormat, store.LoadProblem);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir, "overrides.dat.unreadable-*"));
        Assert.False(File.Exists(path + ".bak"));
    }
}
