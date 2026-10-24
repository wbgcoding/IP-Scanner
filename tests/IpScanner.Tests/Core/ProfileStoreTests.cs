using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class ProfileStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IpScannerTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Dictionary<string, ProfileData> Sample(string subnet) => new()
    {
        ["Buero"] = new ProfileData { Subnets = new() { subnet }, PinnedIps = new() { "10.0.0.1" }, InternetHosts = new() { "8.8.8.8 Google" } },
        [""] = new ProfileData { Subnets = new() { "192.168.1.0/24" } },   // default profile's stash
    };

    [Fact]
    public void Load_NoFile_ReturnsEmptyDictionary()
    {
        Assert.Empty(ProfileStore.LoadPlain(TempDir(), out var problem));
        Assert.Equal(LoadProblem.None, problem);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProfileData()
    {
        var dir = TempDir();
        ProfileStore.SavePlain(dir, Sample("10.0.0.0/24"));

        var loaded = ProfileStore.LoadPlain(dir, out var problem);
        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(new[] { "10.0.0.0/24" }, loaded["Buero"].Subnets);
        Assert.Equal(new[] { "10.0.0.1" }, loaded["Buero"].PinnedIps);
        Assert.Equal(new[] { "8.8.8.8 Google" }, loaded["Buero"].InternetHosts);
        Assert.Equal(new[] { "192.168.1.0/24" }, loaded[""].Subnets);
    }

    [Fact]
    public void Save_WritesVersion1Root()
    {
        var dir = TempDir();
        ProfileStore.SavePlain(dir, Sample("10.0.0.0/24"));
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfileStore.PathFor(dir)));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.True(doc.RootElement.GetProperty("profiles").TryGetProperty("Buero", out _));
    }

    [Fact]
    public void Load_BareMapWithoutVersion_IsReadOnce_AndWrittenBackAsVersion1()
    {
        var dir = TempDir();
        File.WriteAllText(ProfileStore.PathFor(dir),
            """{ "Buero": { "Subnets": ["10.1.0.0/24"], "PinnedIps": [], "InternetHosts": [] } }""");

        var loaded = ProfileStore.LoadPlain(dir, out var problem);
        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal(new[] { "10.1.0.0/24" }, loaded["Buero"].Subnets);

        ProfileStore.SavePlain(dir, loaded);
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfileStore.PathFor(dir)));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Load_TruncatedFile_FallsBackToTheBackup_AndMovesTheBadFileAside()
    {
        var dir = TempDir();
        ProfileStore.SavePlain(dir, Sample("10.0.0.0/24"));
        ProfileStore.SavePlain(dir, Sample("10.9.9.0/24"));   // .bak holds the first generation
        var path = ProfileStore.PathFor(dir);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text[..(text.Length / 2)]);

        var loaded = ProfileStore.LoadPlain(dir, out var problem);

        Assert.Equal(LoadProblem.RestoredFromBackup, problem);
        Assert.Equal(new[] { "10.0.0.0/24" }, loaded["Buero"].Subnets);
        Assert.Single(Directory.GetFiles(dir, "profiles.json.unreadable-*"));
        Assert.Equal(LoadProblem.None, LoadAgain(dir));   // the restored copy is the live file
    }

    private static LoadProblem LoadAgain(string dir)
    {
        ProfileStore.LoadPlain(dir, out var problem);
        return problem;
    }

    [Fact]
    public void Load_CorruptFileWithoutBackup_ReturnsEmpty_MovesItAside_AndReportsUnreadable()
    {
        var dir = TempDir();
        File.WriteAllText(ProfileStore.PathFor(dir), "{ not valid json");

        var loaded = ProfileStore.LoadPlain(dir, out var problem);

        Assert.Empty(loaded);
        Assert.Equal(LoadProblem.Unreadable, problem);
        Assert.Single(Directory.GetFiles(dir, "profiles.json.unreadable-*"));
        Assert.False(File.Exists(ProfileStore.PathFor(dir)));
    }

    [Fact]
    public void Load_FutureVersion_IsNotSilentlyAccepted()
    {
        var dir = TempDir();
        File.WriteAllText(ProfileStore.PathFor(dir), """{ "version": 99, "profiles": {} }""");
        var loaded = ProfileStore.LoadPlain(dir, out var problem);
        Assert.Empty(loaded);
        Assert.Equal(LoadProblem.NewerFormat, problem);
        Assert.True(File.Exists(ProfileStore.PathFor(dir)));   // left for the newer build
        Assert.Empty(Directory.GetFiles(dir, "profiles.json.unreadable-*"));
    }

    // ---- sealed storage ----

    [Fact]
    public void SealedSaveThenLoad_RoundTrips_AndWritesNoPlainProfilesFile()
    {
        var dir = TempDir();
        Assert.True(ProfileStore.Save(dir, Sample("10.0.0.0/24")));

        var loaded = ProfileStore.Load(dir, dir, out var problem);

        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal(new[] { "10.0.0.0/24" }, loaded["Buero"].Subnets);
        Assert.False(File.Exists(ProfileStore.PathFor(dir)));
        Assert.DoesNotContain("10.0.0.0", File.ReadAllText(Path.Combine(dir, SealedSettings.FileName), System.Text.Encoding.Latin1));
    }

    [Fact]
    public void Load_PlainProfilesJson_IsMigratedIntoTheSealedFile_AndDeleted()
    {
        var dir = TempDir();
        ProfileStore.SavePlain(dir, Sample("10.2.0.0/24"));
        ProfileStore.SavePlain(dir, Sample("10.2.0.0/24"));   // creates a plain .bak too

        var loaded = ProfileStore.Load(dir, dir, out var problem);

        Assert.Equal(LoadProblem.None, problem);
        Assert.Equal(new[] { "10.2.0.0/24" }, loaded["Buero"].Subnets);
        Assert.False(File.Exists(ProfileStore.PathFor(dir)));
        Assert.False(File.Exists(ProfileStore.PathFor(dir) + ".bak"));
        Assert.Equal(new[] { "10.2.0.0/24" }, ProfileStore.Load(dir, dir, out _)["Buero"].Subnets);   // now served from the sealed file
    }

    [Fact]
    public void Load_UnavailableKey_ReturnsEmpty_AndLeavesEverythingUntouched()
    {
        var dir = TempDir();
        ProfileStore.Save(dir, Sample("10.0.0.0/24"));
        var sealedPath = Path.Combine(dir, SealedSettings.FileName);
        var before = File.ReadAllBytes(sealedPath);
        File.WriteAllBytes(Path.Combine(dir, KnownDevicesDb.KeyFileName), new byte[] { 7, 7, 7 });   // not a DPAPI blob
        ProfileStore.SavePlain(dir, Sample("10.3.0.0/24"));

        var loaded = ProfileStore.Load(dir, dir, out var problem);

        Assert.Empty(loaded);
        Assert.Equal(LoadProblem.KeyUnavailable, problem);
        Assert.False(ProfileStore.Save(dir, Sample("10.9.0.0/24")));
        Assert.Equal(before, File.ReadAllBytes(sealedPath));
        Assert.True(File.Exists(ProfileStore.PathFor(dir)));   // the plain original survives
    }

    [Fact]
    public void TrySwitch_SaveFails_LeavesConfigAndMapUnchanged()
    {
        var cfg = new IpScanner.Core.Models.ScanConfig { Subnets = new() { "10.1.0.0/24" }, PinnedIps = new() { "10.1.0.5" }, InternetHosts = new() { "1.1.1.1" } };
        var profiles = new Dictionary<string, ProfileData> { ["Buero"] = new ProfileData { Subnets = new() { "10.7.0.0/24" } } };

        Assert.False(ProfileStore.TrySwitch(cfg, profiles, "Buero", () => false));

        Assert.Equal("", cfg.ActiveProfile);
        Assert.Equal(new[] { "10.1.0.0/24" }, cfg.Subnets);
        Assert.Equal(new[] { "10.1.0.5" }, cfg.PinnedIps);
        Assert.Equal(new[] { "1.1.1.1" }, cfg.InternetHosts);
        Assert.Equal(new[] { "Buero" }, profiles.Keys);   // no stash for the default was left behind
        Assert.Equal(new[] { "10.7.0.0/24" }, profiles["Buero"].Subnets);
    }

    [Fact]
    public void TrySwitch_SaveWorks_StashesOutgoingListsAndLoadsIncoming()
    {
        var cfg = new IpScanner.Core.Models.ScanConfig { Subnets = new() { "10.1.0.0/24" } };
        var profiles = new Dictionary<string, ProfileData> { ["Buero"] = new ProfileData { Subnets = new() { "10.7.0.0/24" } } };

        Assert.True(ProfileStore.TrySwitch(cfg, profiles, "Buero", () => true));

        Assert.Equal("Buero", cfg.ActiveProfile);
        Assert.Equal(new[] { "10.7.0.0/24" }, cfg.Subnets);
        Assert.Equal(new[] { "10.1.0.0/24" }, profiles[""].Subnets);
    }

    [Fact]
    public void TryEdit_SaveFails_RestoresTheMap()
    {
        var profiles = new Dictionary<string, ProfileData> { ["A"] = new ProfileData(), ["B"] = new ProfileData() };
        Assert.False(ProfileStore.TryEdit(profiles, () => profiles.Remove("A"), () => false));
        Assert.Equal(new[] { "A", "B" }, profiles.Keys.OrderBy(k => k));
    }

    [Fact]
    public void TrySwitch_WithSealedStoreKeyUnavailable_DoesNotLoseTheDefaultLists()
    {
        var dir = TempDir();
        SealedSettings.Save(dir, new SealedSettingsData());
        File.WriteAllBytes(Path.Combine(dir, KnownDevicesDb.KeyFileName), new byte[] { 5, 5, 5 });
        var cfg = new IpScanner.Core.Models.ScanConfig { Subnets = new() { "10.1.0.0/24" } };
        var profiles = ProfileStore.Load(dir, dir, out _);

        Assert.False(ProfileStore.TrySwitch(cfg, profiles, "X", () => ProfileStore.Save(dir, profiles)));
        Assert.Equal(new[] { "10.1.0.0/24" }, cfg.Subnets);
        Assert.Equal("", cfg.ActiveProfile);
    }
}
