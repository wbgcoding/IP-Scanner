using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using IpScanner.Core.Data;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Backup;

/// <summary>Why a backup or restore was refused. The UI maps each value to a plain message.</summary>
public enum BackupFailure
{
    /// <summary>The file is not a ZIP, or not one this app wrote.</summary>
    NotABackup,
    /// <summary>The archive is encrypted and no password was given.</summary>
    PasswordRequired,
    /// <summary>The password does not open the archive.</summary>
    WrongPassword,
    /// <summary>Made by a newer version of the app.</summary>
    NewerFormat,
    /// <summary>Entries missing, changed or damaged (hash, size or database check failed).</summary>
    Corrupt,
    /// <summary>Reading or writing a file failed.</summary>
    Io,
    /// <summary>A backup made without a password under another Windows account: its key cannot be opened here.</summary>
    KeyForeign,
    /// <summary>The key of this installation cannot be opened, so a password-protected backup cannot carry it.</summary>
    KeyUnreadable,
}

public sealed class BackupException : Exception
{
    public BackupFailure Failure { get; }

    public BackupException(BackupFailure failure, string message, Exception? inner = null) : base(message, inner)
        => Failure = failure;
}

/// <summary>Every file the backup knows, with the place it lives in the running installation.</summary>
public sealed record BackupLayout(string Conf, string Database, string Overrides, string Key, string Settings, string Profiles)
{
    /// <summary>All files next to each other in one folder, the layout of a default install.</summary>
    public static BackupLayout ForDirectory(string dir) => new(
        Path.Combine(dir, BackupService.ConfName), Path.Combine(dir, BackupService.DatabaseName),
        Path.Combine(dir, BackupService.OverridesName), Path.Combine(dir, BackupService.KeyName),
        Path.Combine(dir, BackupService.SettingsName), Path.Combine(dir, BackupService.ProfilesName));

    internal IEnumerable<(string Name, string Path)> Entries()
    {
        yield return (BackupService.ConfName, Conf);
        yield return (BackupService.DatabaseName, Database);
        yield return (BackupService.OverridesName, Overrides);
        yield return (BackupService.KeyName, Key);
        yield return (BackupService.SettingsName, Settings);
        yield return (BackupService.ProfilesName, Profiles);
    }
}

public sealed record BackupManifestFile(string Name, long Size, string Sha256);

/// <summary>Description of an archive: no host name, no folder, no user.</summary>
/// <param name="KeyRaw">True when the key entry holds the raw key (only in password-protected archives)
/// instead of the copy tied to the Windows account that made the backup.</param>
public sealed record BackupManifest(int Format, string App, string AppVersion, DateTimeOffset CreatedUtc,
                                    List<BackupManifestFile> Files, bool KeyRaw = false);

/// <summary>How the installation key is tied to the current Windows account. The default is DPAPI for the
/// current user; tests swap in two fake accounts.</summary>
public sealed record KeyWrap(Func<byte[], byte[]> Protect, Func<byte[], byte[]> Unprotect)
{
    public static readonly KeyWrap Dpapi = new(
        raw => ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser),
        blob => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
}

/// <param name="Manifest">Null while the archive is encrypted and no password was given.</param>
public sealed record BackupInfo(bool Encrypted, BackupManifest? Manifest);

/// <summary>Full backup and restore: one ZIP with the config, the database, the overrides (which hold
/// the notes), the key and sealed settings that make them readable, and a manifest. With a password the
/// entries are encrypted with ZIP AES-256 (WinZip AE-2, readable by 7-Zip) and carry the raw key, so the backup
/// restores on any PC or account; without one it is a plain ZIP whose key is tied to the creating account.</summary>
public static class BackupService
{
    public const string ConfName = "ip_scanner.conf";
    public const string DatabaseName = "scanner.db";
    public const string OverridesName = "overrides.dat";
    public const string KeyName = "scanner.key";
    public const string SettingsName = "settings.dat";
    public const string ProfilesName = "profiles.json";
    public const string ManifestName = "manifest.json";
    public const string AppName = "IP-Scanner";
    public const int FormatVersion = 1;

    private const long MaxManifestBytes = 1 << 20;
    private const long MaxEntryBytes = 16L << 30;
    private const long MaxTotalBytes = 32L << 30;
    private const int KeyBytes = 32;
    private const long MaxKeyEntryBytes = 64 << 10;
    private const string SnapshotSuffix = ".backup-tmp";
    private const string StageSuffix = ".restore-tmp";
    private const string OldSuffix = ".restore-old";
    private const int KeepSafetyBackups = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Item(string Name, byte[]? Bytes, string? Path, long Size, string Sha256)
    {
        public Stream Open() => Bytes is not null ? new MemoryStream(Bytes, writable: false) : OpenShared(Path!);
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // ── create ──

    /// <summary>Writes the archive atomically (a half-written file never replaces an older backup).
    /// An empty password gives a plain ZIP. Missing optional files (no profiles, no key yet) are skipped.
    /// <paramref name="privateOutput"/> creates the archive readable by the
    /// current account only from the first byte on, for archives that stay on this machine.
    /// With a password the key travels raw inside the encrypted archive; without one it stays tied to this account.</summary>
    public static void Create(BackupLayout layout, string zipPath, string? password, string appVersion,
                              bool privateOutput = false, KeyWrap? keyWrap = null)
    {
        var full = Path.GetFullPath(zipPath);
        var tmp = full + ".tmp";
        string? dbSnapshot = null;
        byte[]? rawKey = null;
        var keyRaw = false;
        var encrypt = !string.IsNullOrEmpty(password);
        try
        {
            var items = new List<Item>();
            foreach (var (name, path) in layout.Entries())
            {
                if (!File.Exists(path)) continue;
                if (name == DatabaseName)
                {
                    dbSnapshot = path + SnapshotSuffix;
                    SnapshotDatabase(path, dbSnapshot);
                    items.Add(Describe(name, null, dbSnapshot));
                }
                else if (name == KeyName && encrypt)
                {
                    rawKey = RawKey(path, keyWrap ?? KeyWrap.Dpapi);
                    keyRaw = true;
                    items.Add(Describe(name, rawKey, null));
                }
                else
                {
                    items.Add(Describe(name, ReadShared(path), null));
                }
            }

            var manifest = new BackupManifest(FormatVersion, AppName, appVersion, DateTimeOffset.UtcNow,
                items.Select(i => new BackupManifestFile(i.Name, i.Size, i.Sha256)).ToList(), keyRaw);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);

            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            using (var file = privateOutput
                       ? FileProtection.CreatePrivateFile(tmp)
                       : new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipOutputStream(file))
            {
                zip.SetLevel(6);
                if (encrypt) zip.Password = password;
                PutEntry(zip, new Item(ManifestName, manifestBytes, null, manifestBytes.Length, ""), encrypt);
                foreach (var item in items) PutEntry(zip, item, encrypt);
                zip.Finish();
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZipException or SqliteException)
        {
            throw new BackupException(BackupFailure.Io, ex.Message, ex);
        }
        finally
        {
            TryDelete(tmp);
            if (dbSnapshot is not null) TryDelete(dbSnapshot);
            if (rawKey is not null) CryptographicOperations.ZeroMemory(rawKey);
        }
    }

    /// <summary>The installation key in the clear, for an archive that a password protects.</summary>
    private static byte[] RawKey(string path, KeyWrap wrap)
    {
        byte[]? raw = null;
        try
        {
            raw = wrap.Unprotect(ReadShared(path));
            if (raw.Length == KeyBytes) return raw;
        }
        catch (CryptographicException ex) { throw new BackupException(BackupFailure.KeyUnreadable, ex.Message, ex); }
        if (raw is not null) CryptographicOperations.ZeroMemory(raw);
        throw new BackupException(BackupFailure.KeyUnreadable, "key has an unexpected size");
    }

    /// <summary>The key entry holds the raw key: it is stored uncompressed and written straight from its
    /// buffer, so no deflater window or copy buffer keeps a second copy.</summary>
    private static void PutEntry(ZipOutputStream zip, Item item, bool encrypt)
    {
        var entry = new ZipEntry(item.Name) { DateTime = DateTime.Now, Size = item.Size, IsUnicodeText = true };
        if (encrypt) entry.AESKeySize = 256;
        if (item.Name == KeyName) entry.CompressionMethod = CompressionMethod.Stored;
        zip.PutNextEntry(entry);
        if (item.Bytes is not null)
        {
            zip.Write(item.Bytes, 0, item.Bytes.Length);
        }
        else
        {
            using var source = item.Open();
            source.CopyTo(zip);
        }
        zip.CloseEntry();
    }

    private static Item Describe(string name, byte[]? bytes, string? path)
    {
        using var stream = bytes is not null ? new MemoryStream(bytes, writable: false) : (Stream)OpenShared(path!);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new Item(name, bytes, path, stream.Length, hash);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = OpenShared(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Consistent copy of a live database: SQLite's online backup folds the write-ahead log in,
    /// so the snapshot is a single self-contained file. It lives beside the database as a private file,
    /// never next to the chosen output, and is deleted by the caller.</summary>
    private static void SnapshotDatabase(string source, string target)
    {
        TryDelete(target);
        FileProtection.CreatePrivateFile(target).Dispose();   // SQLite then fills the private empty file
        var sourceString = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString;
        var targetString = new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ConnectionString;
        using (var from = new SqliteConnection(sourceString))
        using (var to = new SqliteConnection(targetString))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
        TryDelete(target + "-wal");
        TryDelete(target + "-shm");
    }

    // ── inspect ──

    /// <summary>Whether the archive is encrypted and, when readable without a password or with the given
    /// one, its manifest. Touches no file of the installation.</summary>
    public static BackupInfo Inspect(string zipPath, string? password)
    {
        using var zip = OpenZip(zipPath);
        var encrypted = CheckEntries(zip);
        if (encrypted && string.IsNullOrEmpty(password)) return new BackupInfo(true, null);
        return new BackupInfo(encrypted, ReadManifest(zip, password, encrypted));
    }

    /// <summary>Whether the archive is encrypted. One crypted entry makes it so, and then every entry must
    /// be AES-256 like the ones this app writes: a plain entry beside an encrypted one could be swapped in
    /// without the password. Entry names must be unique (case-insensitive), as a lookup returns only the first.</summary>
    private static bool CheckEntries(ZipFile zip)
    {
        var entries = zip.Cast<ZipEntry>().ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (!names.Add(e.Name)) throw new BackupException(BackupFailure.Corrupt, "duplicate entry");
        var encrypted = entries.Any(e => e.IsCrypted);
        if (encrypted && entries.Any(e => !e.IsCrypted || e.AESKeySize != 256))
            throw new BackupException(BackupFailure.Corrupt, "entry is not AES-256 encrypted");
        return encrypted;
    }

    private static ZipFile OpenZip(string zipPath)
    {
        try { return new ZipFile(zipPath); }
        catch (ZipException ex) { throw new BackupException(BackupFailure.NotABackup, ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(BackupFailure.Io, ex.Message, ex);
        }
    }

    private static BackupManifest ReadManifest(ZipFile zip, string? password, bool encrypted)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new BackupException(BackupFailure.NotABackup, "manifest missing");
        if (entry.Size is < 0 or > MaxManifestBytes)
            throw new BackupException(BackupFailure.NotABackup, "manifest too large");
        zip.Password = password;
        byte[] bytes;
        try
        {
            using var stream = zip.GetInputStream(entry);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException or CryptographicException)
        {
            // The manifest is the first entry: a failure here with a password means the password is wrong.
            throw encrypted
                ? new BackupException(BackupFailure.WrongPassword, ex.Message, ex)
                : new BackupException(BackupFailure.Corrupt, ex.Message, ex);
        }

        BackupManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, Json); }
        catch (JsonException ex) { throw new BackupException(encrypted ? BackupFailure.WrongPassword : BackupFailure.NotABackup, ex.Message, ex); }
        if (manifest is null || manifest.App != AppName || manifest.Files is null)
            throw new BackupException(BackupFailure.NotABackup, "not an IP-Scanner backup");
        if (manifest.Format > FormatVersion) throw new BackupException(BackupFailure.NewerFormat, "newer backup format");
        if (manifest.Format < 1) throw new BackupException(BackupFailure.NotABackup, "unknown backup format");
        return manifest;
    }

    // ── restore ──

    /// <summary>An archive that passed every check and is staged beside its targets. Nothing of the
    /// installation has been touched yet; <see cref="Commit"/> replaces the files, disposing discards the staging.</summary>
    public sealed class PreparedRestore : IDisposable
    {
        private readonly BackupLayout _layout;
        private readonly Dictionary<string, string> _staged;
        private bool _done;

        internal PreparedRestore(BackupLayout layout, BackupManifest manifest, Dictionary<string, string> staged)
        {
            _layout = layout;
            Manifest = manifest;
            _staged = staged;
        }

        public BackupManifest Manifest { get; }

        /// <summary>Writes a plain safety backup of the current state, then swaps the files in. A failure
        /// while swapping puts the previous files back. Returns the safety backup path (null when there was nothing to save).</summary>
        public string? Commit(string appVersion)
        {
            if (_done) throw new InvalidOperationException();
            _done = true;
            try
            {
                var safety = WriteSafetyBackup(_layout, appVersion);
                SqliteConnection.ClearAllPools();
                SwapIn(_layout, _staged);
                KnownDevicesDb.ForgetInitializedFiles();
                return safety;
            }
            finally { Cleanup(); }
        }

        public void Dispose() => Cleanup();

        private void Cleanup()
        {
            foreach (var path in _staged.Values) TryDelete(path);
            _staged.Clear();
        }
    }

    /// <summary>Opens the archive, checks the manifest, the password and every entry (size, SHA-256, and
    /// an integrity check of the database) while writing each file to a staging name beside its target.
    /// Throws before any real file is replaced; the staging is removed on failure.</summary>
    public static PreparedRestore Prepare(string zipPath, string? password, BackupLayout layout, KeyWrap? keyWrap = null)
    {
        using var zip = OpenZip(zipPath);
        var encrypted = CheckEntries(zip);
        if (encrypted && string.IsNullOrEmpty(password))
            throw new BackupException(BackupFailure.PasswordRequired, "password required");
        var manifest = ReadManifest(zip, password, encrypted);

        var targets = layout.Entries().ToDictionary(e => e.Name, e => e.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var file in manifest.Files)
        {
            if (file?.Name is null || !targets.ContainsKey(file.Name) || !seen.Add(file.Name)
                || file.Size < 0 || file.Sha256 is not { Length: 64 })
                throw new BackupException(BackupFailure.Corrupt, "unexpected manifest entry");
            if (file.Size > MaxEntryBytes || (totalSize += file.Size) > MaxTotalBytes)
                throw new BackupException(BackupFailure.Corrupt, "entry size out of range");
        }
        if (manifest.KeyRaw && !encrypted) throw new BackupException(BackupFailure.Corrupt, "raw key in a plain archive");
        if (!seen.Contains(KeyName) && (manifest.KeyRaw || seen.Contains(OverridesName) || seen.Contains(SettingsName)))
            throw new BackupException(BackupFailure.Corrupt, "sealed data without its key");
        CheckFreeSpace(layout, manifest);
        if (!seen.Contains(ConfName) && !seen.Contains(DatabaseName))
            throw new BackupException(BackupFailure.Corrupt, "archive holds no data");
        if (zip.Cast<ZipEntry>().Any(e => e.Name != ManifestName && !seen.Contains(e.Name)))
            throw new BackupException(BackupFailure.Corrupt, "unlisted entry");

        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            zip.Password = password;
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry(file.Name) ?? throw new BackupException(BackupFailure.Corrupt, "entry missing");
                var stage = StageFor(targets[file.Name]);
                staged[file.Name] = stage;
                if (file.Name == KeyName) StageKey(zip, entry, file, stage, manifest.KeyRaw, keyWrap ?? KeyWrap.Dpapi);
                else Extract(zip, entry, file, stage);
            }
            if (staged.TryGetValue(DatabaseName, out var db)) CheckDatabase(db);
            return new PreparedRestore(layout, manifest, staged);
        }
        catch
        {
            foreach (var path in staged.Values) TryDelete(path);
            throw;
        }
    }

    /// <summary>Staging copies sit beside their targets, so the drive that holds them must have room for all.</summary>
    private static void CheckFreeSpace(BackupLayout layout, BackupManifest manifest)
    {
        var targets = layout.Entries().ToDictionary(e => e.Name, e => e.Path, StringComparer.Ordinal);
        foreach (var group in manifest.Files.GroupBy(f => Path.GetPathRoot(Path.GetFullPath(targets[f.Name])) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            long free;
            try { free = new DriveInfo(group.Key).AvailableFreeSpace; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { continue; }
            if (group.Sum(f => f.Size) > free) throw new BackupException(BackupFailure.Io, "not enough free space");
        }
    }

    /// <summary>Stages the key entry without the raw key ever reaching a file: it is read into memory, checked
    /// against the manifest, and only the form that opens for the current account is written. A raw key
    /// (password-protected archive) is protected for this account first; a key from a plain archive must
    /// already open for this account.</summary>
    private static void StageKey(ZipFile zip, ZipEntry entry, BackupManifestFile file, string stage, bool keyRaw, KeyWrap wrap)
    {
        byte[]? bytes = null;
        byte[]? blob = null;
        try
        {
            bytes = ReadKeyEntry(zip, entry, file);
            if (keyRaw)
            {
                if (bytes.Length != KeyBytes) throw new BackupException(BackupFailure.Corrupt, "key has an unexpected size");
                blob = wrap.Protect(bytes);
            }
            else
            {
                var raw = wrap.Unprotect(bytes);
                var ok = raw.Length == KeyBytes;
                CryptographicOperations.ZeroMemory(raw);
                if (!ok) throw new BackupException(BackupFailure.Corrupt, "key has an unexpected size");
                blob = bytes;
            }
            using var output = FileProtection.CreatePrivateFile(stage);
            output.Write(blob, 0, blob.Length);
        }
        catch (CryptographicException ex)
        {
            throw new BackupException(keyRaw ? BackupFailure.KeyUnreadable : BackupFailure.KeyForeign, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(BackupFailure.Io, ex.Message, ex);
        }
        finally
        {
            if (keyRaw && bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] ReadKeyEntry(ZipFile zip, ZipEntry entry, BackupManifestFile file)
    {
        if (file.Size > MaxKeyEntryBytes) throw new BackupException(BackupFailure.Corrupt, "key entry too large");
        var buffer = new byte[file.Size];
        try
        {
            using var input = zip.GetInputStream(entry);
            var filled = 0;
            int read;
            while (filled < buffer.Length && (read = input.Read(buffer, filled, buffer.Length - filled)) > 0) filled += read;
            if (filled != buffer.Length || input.ReadByte() != -1)
                throw new BackupException(BackupFailure.Corrupt, "key entry size differs from the listing");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(buffer)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BackupException(BackupFailure.Corrupt, "checksum mismatch");
            return buffer;
        }
        catch (BackupException) { CryptographicOperations.ZeroMemory(buffer); throw; }
        catch (Exception ex) when (ex is ZipException or InvalidDataException or CryptographicException)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new BackupException(BackupFailure.Corrupt, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new BackupException(BackupFailure.Io, ex.Message, ex);
        }
    }

    private static string StageFor(string target)
    {
        var full = Path.GetFullPath(target);
        var dir = Path.GetDirectoryName(full)!;
        FileProtection.PrepareDirectory(dir);
        return full + StageSuffix;
    }

    private static void Extract(ZipFile zip, ZipEntry entry, BackupManifestFile file, string stage)
    {
        try
        {
            using var input = zip.GetInputStream(entry);
            using var output = FileProtection.CreatePrivateFile(stage);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > file.Size) throw new BackupException(BackupFailure.Corrupt, "entry larger than listed");
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (total != file.Size) throw new BackupException(BackupFailure.Corrupt, "entry smaller than listed");
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BackupException(BackupFailure.Corrupt, "checksum mismatch");
        }
        catch (BackupException) { throw; }
        catch (Exception ex) when (ex is ZipException or InvalidDataException or CryptographicException)
        {
            throw new BackupException(BackupFailure.Corrupt, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(BackupFailure.Io, ex.Message, ex);
        }
    }

    private static void CheckDatabase(string path)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check";
                if (!string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new BackupException(BackupFailure.Corrupt, "database check failed");
            }
        }
        catch (SqliteException ex) { throw new BackupException(BackupFailure.Corrupt, ex.Message, ex); }
        finally
        {
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    private static string? WriteSafetyBackup(BackupLayout layout, string appVersion)
    {
        if (!layout.Entries().Any(e => File.Exists(e.Path))) return null;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(layout.Database))!, "Backups");
        FileProtection.PrepareDirectory(dir);
        var path = Path.Combine(dir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.zip");
        Create(layout, path, null, appVersion, privateOutput: true);   // holds the key: private like the files it was made from
        FileProtection.RestrictFile(path);
        try
        {
            foreach (var old in new DirectoryInfo(dir).GetFiles("pre-restore-*.zip").OrderByDescending(f => f.Name).Skip(KeepSafetyBackups))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        return path;
    }

    private sealed record Step(string Target, string? Staged, bool HadTarget);

    /// <summary>Replaces every managed file with its staged copy (or removes it when the archive has none, except the key),
    /// keeping each original as a sibling until all are done; on any failure the originals come back.</summary>
    private static void SwapIn(BackupLayout layout, Dictionary<string, string> staged)
    {
        var steps = new List<Step>();
        foreach (var (name, target) in layout.Entries())
        {
            var full = Path.GetFullPath(target);
            if (name == KeyName && !staged.ContainsKey(name)) continue;   // an archive without a key leaves the current one
            if (name == DatabaseName)
            {
                steps.Add(new Step(full + "-wal", null, File.Exists(full + "-wal")));
                steps.Add(new Step(full + "-shm", null, File.Exists(full + "-shm")));
            }
            steps.Add(new Step(full, staged.GetValueOrDefault(name), File.Exists(full)));
        }

        var applied = new List<Step>();
        try
        {
            foreach (var step in steps)
            {
                TryDelete(step.Target + OldSuffix);
                if (step.Staged is null)
                {
                    if (step.HadTarget) File.Move(step.Target, step.Target + OldSuffix);
                }
                else if (step.HadTarget)
                {
                    File.Replace(step.Staged, step.Target, step.Target + OldSuffix);
                }
                else
                {
                    File.Move(step.Staged, step.Target);
                }
                applied.Add(step);
            }
        }
        catch (Exception ex)
        {
            for (int i = applied.Count - 1; i >= 0; i--) Revert(applied[i]);
            if (ex is IOException or UnauthorizedAccessException) throw new BackupException(BackupFailure.Io, ex.Message, ex);
            throw;
        }
        foreach (var step in applied) TryDelete(step.Target + OldSuffix);
        foreach (var step in applied.Where(s => s.Staged is not null && !s.HadTarget)) FileProtection.RestrictFile(step.Target);
    }

    private static void Revert(Step step)
    {
        try
        {
            var old = step.Target + OldSuffix;
            if (step.HadTarget) File.Move(old, step.Target, overwrite: true);
            else TryDelete(step.Target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing more to try */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }
}
