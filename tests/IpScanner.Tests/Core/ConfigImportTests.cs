using System.Collections.Generic;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class ConfigImportTests
{
    private static ScanConfig Current() => new()
    {
        DatabasePath = @".\Data\mine.db", OutputDirectory = @".\MyScans",
        ProfileNames = new List<string> { "Home" }, ActiveProfile = "Home",
    };

    [Fact]
    public void DriveRootDatabasePath_KeepsTheCurrentPathAndIsListed()
    {
        var imported = new ScanConfig { DatabasePath = @"C:\" };

        var (result, kept) = ConfigImport.Sanitize(Current(), imported);

        Assert.Equal(@".\Data\mine.db", result.DatabasePath);
        Assert.Contains("database_path", kept);
    }

    [Fact]
    public void ProtectedOutputFolderAndBadConfigFolder_AreKept()
    {
        var imported = new ScanConfig
        {
            OutputDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ConfigDirectory = "bad|path?",
        };

        var (result, kept) = ConfigImport.Sanitize(Current(), imported);

        Assert.Equal(@".\MyScans", result.OutputDirectory);
        Assert.Equal(Current().ConfigDirectory, result.ConfigDirectory);
        Assert.Equal(new[] { "output_directory", "config_directory" }, kept);
    }

    [Fact]
    public void ValidRelativePathsAndAnEmptyConfigFolder_AreAccepted()
    {
        var imported = new ScanConfig
        {
            DatabasePath = @".\Other\scanner.db", OutputDirectory = @".\OtherScans", ConfigDirectory = "",
        };

        var (result, kept) = ConfigImport.Sanitize(Current(), imported);

        Assert.Equal(@".\Other\scanner.db", result.DatabasePath);
        Assert.Equal(@".\OtherScans", result.OutputDirectory);
        Assert.Empty(kept);
    }

    [Fact]
    public void LocalProfiles_SurviveAnImportThatNamesOthersOrNone()
    {
        var current = Current();
        current.ProfileNames = new List<string> { "Home", "Lab" };

        var none = ConfigImport.Sanitize(current, new ScanConfig());
        Assert.Equal(new[] { "Home", "Lab" }, none.Result.ProfileNames);
        Assert.Equal("Home", none.Result.ActiveProfile);
        Assert.DoesNotContain("profile_names", none.KeptKeys);
        Assert.DoesNotContain("active_profile", none.KeptKeys);

        var foreign = ConfigImport.Sanitize(current, new ScanConfig
        {
            ProfileNames = new List<string> { "Office-From-Other-PC" }, ActiveProfile = "Office-From-Other-PC",
            Subnets = new List<string> { "10.9.0.0/24" },
        });
        Assert.Equal(new[] { "Home", "Lab" }, foreign.Result.ProfileNames);
        Assert.Equal("Home", foreign.Result.ActiveProfile);
        Assert.Contains("profile_names", foreign.KeptKeys);
        Assert.Contains("active_profile", foreign.KeptKeys);
        Assert.Equal(new[] { "10.9.0.0/24" }, foreign.Result.Subnets);
    }

    [Theory]
    [InlineData(@"\\attacker\share\x")]
    [InlineData("//attacker/share/x")]
    [InlineData(@"\\?\UNC\attacker\share\x")]
    [InlineData(@"\\.\C:\x")]
    public void NetworkPaths_AreRefusedForEveryPathKey(string bad)
    {
        var imported = new ScanConfig { DatabasePath = bad + @"\scanner.db", OutputDirectory = bad, ConfigDirectory = bad };

        var (result, kept) = ConfigImport.Sanitize(Current(), imported);

        Assert.Equal(@".\Data\mine.db", result.DatabasePath);
        Assert.Equal(@".\MyScans", result.OutputDirectory);
        Assert.Equal(Current().ConfigDirectory, result.ConfigDirectory);
        Assert.Equal(new[] { "database_path", "output_directory", "config_directory" }, kept);
    }

    [Fact]
    public void LocalDrivePaths_AreNotTakenForNetworkPaths()
    {
        Assert.False(ConfigImport.IsNetworkPath(@".\Scans"));
        Assert.False(ConfigImport.IsNetworkPath(System.IO.Path.GetTempPath()));
    }

    [Fact]
    public void OversizedLists_AreCutAtTheCapAndListed()
    {
        var imported = new ScanConfig
        {
            PinnedIps = Enumerable.Range(0, 5000).Select(i => $"10.{i / 250}.{i % 250}.1").ToList(),
            Subnets = new List<string> { "10.1.0.0/24" },
        };

        var (result, kept) = ConfigImport.Sanitize(Current(), imported);

        Assert.Equal(ConfigImport.MaxListEntries, result.PinnedIps.Count);
        Assert.Contains("pinned_ips", kept);
        Assert.DoesNotContain("subnet", kept);
        Assert.Single(result.Subnets);
    }

    [Fact]
    public void ImportedScanPresets_AreCutAtTheirOwnCap()
    {
        var imported = new ScanConfig
        {
            ScanPresets = Enumerable.Range(0, 80).Select(i => $"P{i} | -T4").ToList(),
        };
        var (result, kept) = ConfigImport.Sanitize(Current(), imported);
        Assert.Equal(IpScanner.Core.Cli.ScanPresets.MaxUserPresets, result.ScanPresets.Count);
        Assert.Contains("scan_presets", kept);
    }

    [Fact]
    public void ResultIsACopy_NotTheImportedInstance()
    {
        var imported = new ScanConfig { Subnets = new List<string> { "10.1.0.0/24" } };
        var (result, _) = ConfigImport.Sanitize(Current(), imported);
        result.Subnets.Add("x");
        Assert.Single(imported.Subnets);
    }
}
