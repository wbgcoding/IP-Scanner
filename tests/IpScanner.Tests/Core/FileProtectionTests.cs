using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class FileProtectionTests
{
    [Fact]
    public void IsProtectableDirectory_OwnSubfolder_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip-scanner-test-" + Guid.NewGuid().ToString("N"));
        Assert.True(FileProtection.IsProtectableDirectory(dir));
    }

    [Fact]
    public void IsProtectableDirectory_DriveRoot_ReturnsFalse()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(FileProtection.IsProtectableDirectory(root));
    }

    [Fact]
    public void IsProtectableDirectory_UserProfileItself_ReturnsFalse()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(FileProtection.IsProtectableDirectory(profile));
    }

    [Fact]
    public void IsProtectableDirectory_ParentOfUserProfile_ReturnsFalse()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.GetDirectoryName(profile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.NotNull(parent);
        Assert.False(FileProtection.IsProtectableDirectory(parent));
    }

    [Fact]
    public void IsProtectableDirectory_SubfolderOfProfile_ReturnsTrue()
    {
        // A folder the app itself created inside e.g. Documents is fine - only
        // the profile folder (or a drive root) is off-limits, not everything
        // under it.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sub = Path.Combine(profile, "ip-scanner-test-" + Guid.NewGuid().ToString("N"));
        Assert.True(FileProtection.IsProtectableDirectory(sub));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProtectableDirectory_NullOrBlank_ReturnsFalse(string? dir)
        => Assert.False(FileProtection.IsProtectableDirectory(dir));

    [Fact]
    public void RestrictToCurrentUser_UnprotectableDirectory_ReturnsFalse_DoesNotThrow()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(FileProtection.RestrictToCurrentUser(root));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip-scanner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FolderSddl(string dir)
        => new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static string[] ExpectedSids() => new[]
    {
        WindowsIdentity.GetCurrent().User!.Value,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
    }.Distinct().OrderBy(s => s).ToArray();

    private static string[] SidsOf(FileSystemSecurity security)
        => security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().OrderBy(s => s).ToArray();

    [Fact]
    public void NotOwnedFolder_OnlyTheFileGetsAnExplicitAcl_FolderAclUnchanged()
    {
        var dir = NewTempDir();
        try
        {
            var before = FolderSddl(dir);
            Assert.False(FileProtection.PrepareDirectory(dir));   // exists, no marker -> not ours
            var file = Path.Combine(dir, "data.bin");
            File.WriteAllText(file, "x");
            Assert.True(FileProtection.RestrictFile(file));

            Assert.Equal(before, FolderSddl(dir));
            var security = new FileInfo(file).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(ExpectedSids(), SidsOf(security));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AppCreatedFolder_DaclHoldsExactlyUserSystemAndAdministrators()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip-scanner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(FileProtection.PrepareDirectory(dir));   // created here -> ours
            Assert.True(File.Exists(Path.Combine(dir, FileProtection.OwnerMarkerName)));
            var security = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(ExpectedSids(), SidsOf(security));
            Assert.True(FileProtection.EnsureAppDirectory(dir));   // the marker keeps it ours on later runs
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureAppDirectory_ExistingFolderWithoutMarker_IsNotOwned()
    {
        var dir = NewTempDir();
        try { Assert.False(FileProtection.EnsureAppDirectory(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureAppDirectory_PerRunUpdateFolderUnderTemp_IsOwned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip-scanner-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.True(FileProtection.EnsureAppDirectory(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.MyDocuments)]
    [InlineData(Environment.SpecialFolder.Desktop)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    public void IsProtectableDirectory_WellKnownSharedFolder_IsRefusedButItsSubfolderIsNot(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) return;   // not defined on this machine
        Assert.False(FileProtection.IsProtectableDirectory(path));
        Assert.False(FileProtection.IsProtectableDirectory(path + Path.DirectorySeparatorChar));
        Assert.True(FileProtection.IsProtectableDirectory(Path.Combine(path, "ip-scanner-test")));
    }

    [Fact]
    public void ConfigSave_IntoExistingUnmarkedFolder_KeepsFolderAcl_AndRestrictsTheFile()
    {
        var dir = NewTempDir();
        try
        {
            var before = FolderSddl(dir);
            var conf = Path.Combine(dir, "ip_scanner.conf");
            Assert.True(ConfigManager.Save(conf, new ScanConfig()));
            Assert.Equal(before, FolderSddl(dir));
            var security = new FileInfo(conf).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(ExpectedSids(), SidsOf(security));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureAppDirectory_MarkerInAFolderSomeoneElseOwns_IsNotOwned()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, FileProtection.OwnerMarkerName), Array.Empty<byte>());
            Assert.True(FileProtection.EnsureAppDirectory(dir, _ => true));
            Assert.False(FileProtection.EnsureAppDirectory(dir, _ => false));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IsOwnedByCurrentUser_OwnFolderYes_SystemFolderNo()
    {
        var dir = NewTempDir();
        try
        {
            Assert.True(FileProtection.IsOwnedByCurrentUser(dir));
            Assert.False(FileProtection.IsOwnedByCurrentUser(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IsProtectableDirectory_DevicePrefixAndLinkedFolders_AreRefused()
    {
        Assert.False(FileProtection.IsProtectableDirectory(@"\\?\C:\ip-scanner-test\data"));
        var dir = NewTempDir();
        var link = dir + "-link";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); }
            if (!Directory.Exists(link)) return;   // junctions unavailable on this volume
            Assert.False(FileProtection.IsProtectableDirectory(link));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PrepareDirectory_FolderThatCouldNotBeLockedDown_IsNotReportedAsOwned()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(FileProtection.PrepareDirectory(root));
    }

    [Fact]
    public void CreatePrivateFile_StartsWithThePrivateAcl_AndReplacesAnExistingFile()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "data.tmp");
            File.WriteAllText(path, "planted");
            using (var stream = FileProtection.CreatePrivateFile(path))
            {
                var security = new FileInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                Assert.Equal(ExpectedSids(), SidsOf(security));
                Assert.Equal(0, stream.Length);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AtomicWrite_TempFileHeldOpenByAnotherParty_FailsWithoutLeakingContent()
    {
        var dir = NewTempDir();
        try
        {
            var target = Path.Combine(dir, "ip_scanner.conf");
            var planted = target + ".tmp";
            File.WriteAllText(planted, "");
            using var reader = new FileStream(planted, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.ThrowsAny<IOException>(() => AtomicFile.Write(target, new byte[] { 1, 2, 3 }));
            Assert.Equal(0, reader.Length);
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void WriteLegacyAcl(string dir, params SecurityIdentifier[] sids)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        foreach (var sid in sids)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void PrepareDirectory_FolderWithTheOldUserOnlyAcl_GetsSystemAndAdministratorsAdded()
    {
        var dir = NewTempDir();
        try
        {
            WriteLegacyAcl(dir, WindowsIdentity.GetCurrent().User!);
            Assert.True(FileProtection.HasLegacyPrivateAcl(dir));
            Assert.True(FileProtection.PrepareDirectory(dir));
            Assert.True(File.Exists(Path.Combine(dir, FileProtection.OwnerMarkerName)));
            Assert.Equal(ExpectedSids(), SidsOf(new DirectoryInfo(dir).GetAccessControl()));
            Assert.False(FileProtection.HasLegacyPrivateAcl(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PrepareDirectory_FolderWithADifferentCustomAcl_IsLeftAlone()
    {
        var dir = NewTempDir();
        try
        {
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            WriteLegacyAcl(dir, WindowsIdentity.GetCurrent().User!, users);
            Assert.False(FileProtection.HasLegacyPrivateAcl(dir));
            var before = FolderSddl(dir);
            Assert.False(FileProtection.PrepareDirectory(dir));
            Assert.Equal(before, FolderSddl(dir));
            Assert.False(File.Exists(Path.Combine(dir, FileProtection.OwnerMarkerName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureFilePrivate_FileWithAnInheritedAcl_GetsTheProtectedAcl()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "error.log");
            File.WriteAllText(file, "old entry");
            Assert.False(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
            Assert.True(FileProtection.EnsureFilePrivate(file));
            var security = new FileInfo(file).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(ExpectedSids(), SidsOf(security));
            Assert.Equal("old entry", File.ReadAllText(file));
            Assert.False(FileProtection.EnsureFilePrivate(Path.Combine(dir, "missing.log")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
