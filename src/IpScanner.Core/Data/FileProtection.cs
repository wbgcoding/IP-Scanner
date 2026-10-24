using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IpScanner.Core.Data;

/// <summary>Keeps the app's files private to the current Windows user, SYSTEM and Administrators.
/// A folder is only re-ACL'd when the app created it (it holds the marker file); files in any
/// other folder get their own private ACL and the folder keeps its permissions. Best-effort:
/// a non-NTFS volume or missing rights to change ACLs leaves the OS default in place.</summary>
[SupportedOSPlatform("windows")]
public static class FileProtection
{
    /// <summary>Empty file the app drops into folders it created; its presence means the app
    /// may lock the folder down.</summary>
    public const string OwnerMarkerName = ".ip-scanner";

    private const string UpdateFolderPrefix = "ip-scanner-update-";

    private static string TrimSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Folders that are shared with other programs or accounts and must never be
    /// re-ACL'd themselves (their subfolders may be).</summary>
    private static readonly Environment.SpecialFolder[] SharedFolders =
    {
        Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
    };

    /// <summary>A configurable data path could point at a drive root, the user's profile, a
    /// well-known shared folder such as Documents or Desktop, or anything above the profile.
    /// Those are refused; subfolders of the well-known folders stay allowed.</summary>
    public static bool IsProtectableDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var full = TrimSeparators(Path.GetFullPath(directory));
            // Device-style prefixes and links would let the comparisons below be sidestepped.
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            if (Directory.Exists(full) && new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var root = Path.GetPathRoot(full) is { } r ? TrimSeparators(r) : null;
            if (string.IsNullOrEmpty(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false; // drive root - shared by every account and program on the volume

            var profile = TrimSeparators(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(profile) &&
                (profile + Path.DirectorySeparatorChar).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false; // the user's profile folder itself, or a parent of it

            foreach (var special in SharedFolders)
            {
                var known = TrimSeparators(Environment.GetFolderPath(special));
                if (known.Length > 0 && string.Equals(full, known, StringComparison.OrdinalIgnoreCase))
                    return false; // exact match only
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>Makes sure <paramref name="dir"/> exists. Returns true when the app owns it: it
    /// was created here (a marker file is dropped into it), already carries the marker, or is a
    /// per-run update folder under the system temp path. An existing folder only counts when the
    /// current user (or, when elevated, the Administrators group) is its owner, so a marker
    /// dropped into a folder another account created is ignored.</summary>
    public static bool EnsureAppDirectory(string dir) => EnsureAppDirectory(dir, IsOwnedByCurrentUser);

    internal static bool EnsureAppDirectory(string dir, Func<string, bool> isOwnedByUs)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                var marker = Path.Combine(full, OwnerMarkerName);
                File.WriteAllBytes(marker, Array.Empty<byte>());
                File.SetAttributes(marker, FileAttributes.Hidden);
                return true;
            }
            if (File.Exists(Path.Combine(full, OwnerMarkerName))) return isOwnedByUs(full);
            if (HasLegacyPrivateAcl(full) && isOwnedByUs(full))
            {
                // An earlier version locked this folder to the current user alone and left no
                // marker; adopting it lets the caller repair the ACL (SYSTEM + Administrators).
                var marker = Path.Combine(full, OwnerMarkerName);
                File.WriteAllBytes(marker, Array.Empty<byte>());
                File.SetAttributes(marker, FileAttributes.Hidden);
                return true;
            }

            var trimmed = TrimSeparators(full);
            var temp = TrimSeparators(Path.GetFullPath(Path.GetTempPath()));
            return string.Equals(Path.GetDirectoryName(trimmed), temp, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(trimmed).StartsWith(UpdateFolderPrefix, StringComparison.Ordinal)
                && isOwnedByUs(full);
        }
        catch { return false; /* best-effort: the caller's own write reports a real problem */ }
    }

    /// <summary>True when the folder's DACL is exactly what earlier versions wrote: protected, one
    /// explicit full-control entry for the current user and nothing else.</summary>
    internal static bool HasLegacyPrivateAcl(string directory)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user) return false;
            return IsLegacyPrivateAcl(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access), user);
        }
        catch { return false; }
    }

    internal static bool IsLegacyPrivateAcl(FileSystemSecurity security, SecurityIdentifier user)
    {
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        return rules.Length == 1
            && !rules[0].IsInherited
            && rules[0].AccessControlType == AccessControlType.Allow
            && rules[0].IdentityReference.Equals(user)
            && rules[0].FileSystemRights == FileSystemRights.FullControl
            && rules[0].InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
            && rules[0].PropagationFlags == PropagationFlags.None;
    }

    /// <summary>True when the folder's owner is the current user, or the Administrators group while
    /// the process runs elevated (folders an elevated process creates belong to that group).</summary>
    internal static bool IsOwnedByCurrentUser(string directory)
    {
        try
        {
            var owner = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null) return false;
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } user && owner.Equals(user)) return true;
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                && owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }
        catch { return false; }
    }

    /// <summary>Creates <paramref name="dir"/> if needed and locks it down when the app owns it.
    /// Returns whether it owns the folder; when false, the caller writes its file and then calls
    /// <see cref="RestrictFile"/> on it.</summary>
    public static bool PrepareDirectory(string dir)
    {
        return EnsureAppDirectory(dir) && RestrictToCurrentUser(dir);
    }

    /// <summary>Best-effort; returns false (unchanged) for a directory
    /// <see cref="IsProtectableDirectory"/> refuses to touch, or if the ACL call itself fails.
    /// Inheritance is switched off and exactly the current user, SYSTEM and Administrators
    /// keep full control.</summary>
    public static bool RestrictToCurrentUser(string directory)
    {
        if (!IsProtectableDirectory(directory)) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            if (!ApplyPrivateRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) return false;
            info.SetAccessControl(security);
            return true;
        }
        catch { return false; /* best-effort */ }
    }

    /// <summary>Gives a single file the private ACL (no inheritance; current user, SYSTEM and
    /// Administrators only) without touching the folder it sits in. Best-effort.</summary>
    public static bool RestrictFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var security = info.GetAccessControl();
            if (!ApplyPrivateRules(security, InheritanceFlags.None)) return false;
            info.SetAccessControl(security);
            return true;
        }
        catch { return false; /* best-effort */ }
    }

    /// <summary>Gives an existing file the private ACL unless it already has a protected DACL (a file
    /// written by an earlier version keeps an inherited one). Returns false when the file is missing
    /// or the ACL could not be changed.</summary>
    public static bool EnsureFilePrivate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected) return true;
            return RestrictFile(path);
        }
        catch { return false; /* best-effort */ }
    }

    /// <summary>Creates a brand-new file that carries the private ACL from its first byte on, so
    /// nothing is ever written into a file whose permissions the app did not set. A file that
    /// already exists is deleted first (this throws when it cannot be removed); a name another
    /// account re-creates in between makes the create fail rather than reuse their file.</summary>
    public static FileStream CreatePrivateFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) File.Delete(full);
        var security = new FileSecurity();
        if (!ApplyPrivateRules(security, InheritanceFlags.None)) throw new UnauthorizedAccessException();
        return new FileInfo(full).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize,
            FileShare.None, 4096, FileOptions.None, security);
    }

    /// <summary>Writes a report file. Inside a folder the app owns a plain write is enough; anywhere
    /// else the file is created private from the start (see <see cref="CreatePrivateFile"/>).</summary>
    public static void WriteReport(string path, string content, System.Text.Encoding encoding, bool ownedDir)
    {
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
        if (ownedDir) { File.WriteAllBytes(path, bytes); return; }
        using var stream = CreatePrivateFile(path);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool ApplyPrivateRules(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null) return false;
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        foreach (var sid in new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        return true;
    }
}
