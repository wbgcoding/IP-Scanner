using System;
using System.IO;
using System.Linq;
using System.Text;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class SealedSettingsTests
{
    private static string NewDir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var dir = NewDir();
        var data = new SealedSettingsData
        {
            SnmpCommunity = "secretcomm",
            RecentWakeTargets = new() { "AA:BB:CC:00:11:22 Printer" },
            Profiles = new() { ["Buero"] = new ProfileData { Subnets = new() { "10.7.0.0/24" } } },
        };
        Assert.True(SealedSettings.Save(dir, data));

        var loaded = SealedSettings.Load(dir, out var problem);

        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal("secretcomm", loaded.SnmpCommunity);
        Assert.Equal(new[] { "AA:BB:CC:00:11:22 Printer" }, loaded.RecentWakeTargets);
        Assert.Equal(new[] { "10.7.0.0/24" }, loaded.Profiles["Buero"].Subnets);
    }

    [Fact]
    public void File_IsSealed_NoReadableText()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "secretcomm", RecentWakeTargets = new() { "AA:BB:CC:00:11:22 Printer" } });
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(SealedSettings.PathFor(dir)));
        Assert.DoesNotContain("secretcomm", text);
        Assert.DoesNotContain("AA:BB:CC", text);
    }

    [Fact]
    public void Update_KeepsFieldsTheCallerDidNotTouch()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "a", Profiles = new() { ["P"] = new ProfileData() } });
        Assert.True(SealedSettings.Update(dir, d => d.RecentWakeTargets = new() { "AA:BB:CC:00:11:22 X" }));

        var loaded = SealedSettings.Load(dir, out _);
        Assert.Equal("a", loaded.SnmpCommunity);
        Assert.Contains("P", loaded.Profiles.Keys);
        Assert.Single(loaded.RecentWakeTargets);
    }

    [Fact]
    public void ConfWithBothLegacyKeys_MigratesIntoTheSealedFile_AndTheKeysLeaveTheConfOnSave()
    {
        var dir = NewDir();
        var conf = Path.Combine(dir, "ip_scanner.conf");
        File.WriteAllText(conf, "ping_count = 4\nsnmp_community = legacycomm\nrecent_wake_targets = AA:BB:CC:00:11:22 Printer, 11:22:33:44:55:66 Nas\n");
        var cfg = ConfigManager.Load(conf);
        Assert.Equal("legacycomm", cfg.SnmpCommunity);

        SealedSettings.ApplyTo(cfg, dir, out var problem);

        Assert.Equal(LoadProblem.None, problem);
        var sealedData = SealedSettings.Load(dir, out _);
        Assert.Equal("legacycomm", sealedData.SnmpCommunity);
        Assert.Equal(2, sealedData.RecentWakeTargets.Count);
        Assert.Equal("legacycomm", cfg.SnmpCommunity);   // runtime value stays usable

        Assert.True(ConfigManager.Save(conf, cfg));
        var text = File.ReadAllText(conf);
        Assert.DoesNotContain("snmp_community", text);
        Assert.DoesNotContain("recent_wake_targets", text);
        Assert.DoesNotContain("legacycomm", text);
        Assert.Contains("ping_count = 4", text);
    }

    [Fact]
    public void ApplyTo_SealedValuesWinOverLegacyConfValues()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "sealedcomm", RecentWakeTargets = new() { "AA:BB:CC:00:11:22 Keep" } });
        var cfg = new ScanConfig { SnmpCommunity = "oldconf", RecentWakeTargets = new() { "11:22:33:44:55:66 Drop" } };

        SealedSettings.ApplyTo(cfg, dir, out _);

        Assert.Equal("sealedcomm", cfg.SnmpCommunity);
        Assert.Equal(new[] { "AA:BB:CC:00:11:22 Keep" }, cfg.RecentWakeTargets);
    }

    [Fact]
    public void ApplyTo_OverlongLegacyCommunity_IsClamped()
    {
        var dir = NewDir();
        var cfg = new ScanConfig { SnmpCommunity = new string('x', 100) };
        SealedSettings.ApplyTo(cfg, dir, out _);
        Assert.Equal(ScanConfig.Bounds.SnmpCommunityMaxLength, cfg.SnmpCommunity.Length);
    }

    [Fact]
    public void KeyUnavailable_FallsBackToDefaults_AndNeverWritesOverTheSealedFile()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "sealedcomm" });
        var before = File.ReadAllBytes(SealedSettings.PathFor(dir));
        File.WriteAllBytes(Path.Combine(dir, KnownDevicesDb.KeyFileName), new byte[] { 5, 5, 5 });
        var cfg = new ScanConfig();

        var safe = SealedSettings.ApplyTo(cfg, dir, out var problem);

        Assert.True(safe);   // nothing legacy to protect
        Assert.Equal(LoadProblem.KeyUnavailable, problem);
        Assert.Equal("public", cfg.SnmpCommunity);
        Assert.False(SealedSettings.Update(dir, d => d.SnmpCommunity = "x"));
        Assert.Equal(before, File.ReadAllBytes(SealedSettings.PathFor(dir)));
    }

    [Fact]
    public void CorruptedFile_IsMovedAside_AndTheBackupLoads()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "first" });
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "second" });
        File.WriteAllBytes(SealedSettings.PathFor(dir), new byte[40]);

        var loaded = SealedSettings.Load(dir, out var problem);

        Assert.Equal(LoadProblem.RestoredFromBackup, problem);
        Assert.Equal("first", loaded.SnmpCommunity);
        Assert.Single(Directory.GetFiles(dir, "settings.dat.unreadable-*"));
    }

    [Fact]
    public void Relocate_CarriesEverythingToTheNewFolderUnderItsOwnKey_AndLeavesTheOldOneAlone()
    {
        var from = NewDir();
        var to = NewDir();
        SealedSettings.Save(from, new SealedSettingsData
        {
            SnmpCommunity = "secretcomm",
            RecentWakeTargets = new() { "AA:BB:CC:00:11:22 Printer" },
            Profiles = new() { ["Buero"] = new ProfileData { Subnets = new() { "10.7.0.0/24" } } },
        });
        var before = File.ReadAllBytes(SealedSettings.PathFor(from));

        Assert.True(SealedSettings.Relocate(from, to));

        var loaded = SealedSettings.Load(to, out var problem);
        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal("secretcomm", loaded.SnmpCommunity);
        Assert.Equal(new[] { "AA:BB:CC:00:11:22 Printer" }, loaded.RecentWakeTargets);
        Assert.Equal(new[] { "10.7.0.0/24" }, loaded.Profiles["Buero"].Subnets);
        Assert.Equal(before, File.ReadAllBytes(SealedSettings.PathFor(from)));
        Assert.True(File.Exists(Path.Combine(to, KnownDevicesDb.KeyFileName)));
    }

    [Fact]
    public void Relocate_ValuesAlreadyInTheNewFolderWin_ProfilesAreMerged()
    {
        var from = NewDir();
        var to = NewDir();
        SealedSettings.Save(from, new SealedSettingsData
        {
            SnmpCommunity = "old",
            RecentWakeTargets = new() { "AA:BB:CC:00:11:22 Old" },
            Profiles = new() { ["A"] = new ProfileData { Subnets = new() { "10.1.0.0/24" } }, ["B"] = new ProfileData { Subnets = new() { "10.2.0.0/24" } } },
        });
        SealedSettings.Save(to, new SealedSettingsData
        {
            SnmpCommunity = "new",
            Profiles = new() { ["B"] = new ProfileData { Subnets = new() { "10.9.0.0/24" } } },
        });

        Assert.True(SealedSettings.Relocate(from, to));

        var loaded = SealedSettings.Load(to, out _);
        Assert.Equal("new", loaded.SnmpCommunity);
        Assert.Equal(new[] { "AA:BB:CC:00:11:22 Old" }, loaded.RecentWakeTargets);
        Assert.Equal(new[] { "10.1.0.0/24" }, loaded.Profiles["A"].Subnets);
        Assert.Equal(new[] { "10.9.0.0/24" }, loaded.Profiles["B"].Subnets);
    }

    [Fact]
    public void Relocate_SourceKeyUnavailable_ReturnsFalseAndWritesNothing()
    {
        var from = NewDir();
        var to = NewDir();
        SealedSettings.Save(from, new SealedSettingsData { SnmpCommunity = "x" });
        File.WriteAllBytes(Path.Combine(from, KnownDevicesDb.KeyFileName), new byte[] { 5, 5, 5 });

        Assert.False(SealedSettings.Relocate(from, to));
        Assert.False(File.Exists(SealedSettings.PathFor(to)));
    }

    [Fact]
    public void Relocate_NothingSealedYet_IsANoOpSuccess()
    {
        Assert.True(SealedSettings.Relocate(NewDir(), NewDir()));
    }

    private static ScanConfig LegacyConf(string dir, out string conf)
    {
        conf = Path.Combine(dir, "ip_scanner.conf");
        File.WriteAllText(conf, "snmp_community = legacycomm\nrecent_wake_targets = AA:BB:CC:00:11:22 Printer\n");
        return ConfigManager.Load(conf);
    }

    [Fact]
    public void LegacyValues_FailedSealedWrite_StayInTheConfUntilASealedWriteWorks()
    {
        var dir = NewDir();
        var cfg = LegacyConf(dir, out var conf);
        Directory.CreateDirectory(SealedSettings.PathFor(dir) + ".tmp");   // the sealed write cannot create its temp file

        var safe = SealedSettings.ApplyTo(cfg, dir, out _);
        cfg.LegacySealedPending = !safe;

        Assert.False(safe);
        Assert.Equal("legacycomm", cfg.SnmpCommunity);
        Assert.True(ConfigManager.Save(conf, cfg));
        var text = File.ReadAllText(conf);
        Assert.Contains("snmp_community = legacycomm", text);
        Assert.Contains("recent_wake_targets = AA:BB:CC:00:11:22 Printer", text);

        // Once the sealed write works, the next save drops the plain copies.
        Directory.Delete(SealedSettings.PathFor(dir) + ".tmp");
        Assert.True(SealedSettings.Update(dir, d => { d.SnmpCommunity = cfg.SnmpCommunity; d.RecentWakeTargets = cfg.RecentWakeTargets; }));
        cfg.LegacySealedPending = false;
        Assert.True(ConfigManager.Save(conf, cfg));
        Assert.DoesNotContain("legacycomm", File.ReadAllText(conf));
        Assert.DoesNotContain("recent_wake_targets", File.ReadAllText(conf));
    }

    [Fact]
    public void LegacyValues_KeyUnavailable_AreKeptInMemoryAndInTheConf()
    {
        var dir = NewDir();
        var cfg = LegacyConf(dir, out var conf);
        File.WriteAllBytes(Path.Combine(dir, KnownDevicesDb.KeyFileName), new byte[] { 5, 5, 5 });

        var safe = SealedSettings.ApplyTo(cfg, dir, out var problem);
        cfg.LegacySealedPending = !safe;

        Assert.False(safe);
        Assert.Equal(LoadProblem.KeyUnavailable, problem);
        Assert.Equal("legacycomm", cfg.SnmpCommunity);
        Assert.Single(cfg.RecentWakeTargets);
        Assert.True(ConfigManager.Save(conf, cfg));
        Assert.Contains("snmp_community = legacycomm", File.ReadAllText(conf));
    }

    [Fact]
    public void LockedFile_DoesNotRotateTheGoodCopyAway_AndRecoversOnceUnlocked()
    {
        var dir = NewDir();
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "first" });
        SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "second" });
        var path = SealedSettings.PathFor(dir);
        var live = File.ReadAllBytes(path);
        var bak = File.ReadAllBytes(path + ".bak");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = SealedSettings.Load(dir, out var problem);
            Assert.Equal(LoadProblem.Unreadable, problem);
            Assert.Null(loaded.SnmpCommunity);
            Assert.False(SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "overwrite" }));
        }

        // The lock is gone: nothing was rotated, so the newest copy loads and writes work again.
        Assert.Equal(live, File.ReadAllBytes(path));
        Assert.Equal(bak, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir, "settings.dat.unreadable-*"));
        Assert.Equal("second", SealedSettings.Load(dir, out var again).SnmpCommunity);
        Assert.Equal(LoadProblem.None, again);
        Assert.True(SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "third" }));
    }

    [Fact]
    public void FileFromANewerBuild_IsKeptInPlace_ReportedAsNewerFormat_AndNeverOverwritten()
    {
        var dir = NewDir();
        var crypto = LocalCrypto.TryLoad(Path.Combine(dir, KnownDevicesDb.KeyFileName))!;
        var path = SealedSettings.PathFor(dir);
        File.WriteAllBytes(path, crypto.Seal("""{ "version": 99, "snmpCommunity": "future" }"""));
        var before = File.ReadAllBytes(path);

        var loaded = SealedSettings.Load(dir, out var problem);

        Assert.Equal(LoadProblem.NewerFormat, problem);
        Assert.Null(loaded.SnmpCommunity);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir, "settings.dat.unreadable-*"));
        Assert.False(SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "overwrite" }));
        Assert.False(SealedSettings.Update(dir, d => d.SnmpCommunity = "overwrite"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingKeyFile_WithSealedSettingsPresent_ReportsKeyUnavailable_AndCreatesNoNewKey()
    {
        var dir = NewDir();
        Assert.True(SealedSettings.Save(dir, new SealedSettingsData { SnmpCommunity = "keepme" }));
        var keyPath = Path.Combine(dir, KnownDevicesDb.KeyFileName);
        var before = File.ReadAllBytes(SealedSettings.PathFor(dir));
        File.Delete(keyPath);

        SealedSettings.Load(dir, out var problem);

        Assert.Equal(LoadProblem.KeyUnavailable, problem);
        Assert.False(File.Exists(keyPath));
        Assert.Equal(before, File.ReadAllBytes(SealedSettings.PathFor(dir)));
        Assert.Empty(Directory.GetFiles(dir, "settings.dat.unreadable-*"));
    }
}
