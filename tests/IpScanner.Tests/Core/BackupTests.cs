using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using IpScanner.Core.Backup;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class BackupTests
{
    private const string Password = "correct horse 7!";
    private const string Network = "AA:BB:CC:00:00:01";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static string NewDir() => Directory.CreateTempSubdirectory("ipsbackup").FullName;

    /// <summary>A populated installation: config, sealed database with devices and an archived run,
    /// overrides holding a note, and the profiles file.</summary>
    private static BackupLayout Populate(string dir, string note = "kitchen printer")
    {
        var layout = BackupLayout.ForDirectory(dir);
        File.WriteAllText(layout.Conf, "{\"subnets\":[\"192.0.2.0/24\"],\"timeout\":900}");
        File.WriteAllText(layout.Profiles, "[{\"name\":\"home\"}]");
        var db = new KnownDevicesDb(layout.Database);
        var devices = new[]
        {
            new Device("192.0.2.5") { Mac = "AA:00:00:00:00:05", Hostname = "box" },
            new Device("192.0.2.6") { Mac = "AA:00:00:00:00:06", Hostname = "tv" },
        };
        db.Save(Network, devices, "2026-10-01T08:00:00Z");
        foreach (var d in devices) d.RecordPing(new PingResult(true, 3, 64));
        db.SaveScanArchive(T0, T0.AddMinutes(1), "192.0.2.0/24", ["192.0.2.0/24"], "{}", devices, []);
        // Written after the database: a sealed file beside a missing key would keep the key from being created.
        File.WriteAllBytes(layout.Overrides, Encoding.UTF8.GetBytes("overrides-v1:" + note));
        // Close pooled connections now: a pool cleared later by a parallel test would checkpoint the WAL
        // and change the database bytes under a before/after comparison.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return layout;
    }

    private static string DeviceList(BackupLayout layout)
        => string.Join("|", new KnownDevicesDb(layout.Database).Load(Network)
            .OrderBy(d => d.Ip, StringComparer.Ordinal).Select(d => $"{d.Ip}/{d.Mac}/{d.Hostname}"));

    private static string Fingerprint(BackupLayout layout)
        => string.Join(";", layout.Entries().Select(e => e.Name + "=" + (File.Exists(e.Path)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ReadShared(e.Path))) : "-")));

    private static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        f.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Wipe(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
    }

    private static void Restore(string zip, string? password, BackupLayout layout)
    {
        using var prepared = BackupService.Prepare(zip, password, layout);
        prepared.Commit("test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void RoundTrip_RestoresConfigDevicesOverridesAndNotesAfterWipe(string? password)
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var devicesBefore = DeviceList(layout);
        var archivesBefore = new KnownDevicesDb(layout.Database).ListScanArchive().Count;
        var confBefore = File.ReadAllBytes(layout.Conf);
        var overridesBefore = File.ReadAllBytes(layout.Overrides);
        var zip = Path.Combine(NewDir(), "backup.zip");

        BackupService.Create(layout, zip, password, "9.9.9");
        Wipe(dir);
        Assert.False(File.Exists(layout.Conf));
        KnownDevicesDb.ForgetInitializedFiles();
        Restore(zip, password, layout);

        Assert.Equal(confBefore, File.ReadAllBytes(layout.Conf));
        Assert.Equal(overridesBefore, File.ReadAllBytes(layout.Overrides));
        Assert.Contains("kitchen printer", Encoding.UTF8.GetString(File.ReadAllBytes(layout.Overrides)));
        Assert.Equal("[{\"name\":\"home\"}]", File.ReadAllText(layout.Profiles));
        Assert.Equal(devicesBefore, DeviceList(layout));
        Assert.NotEqual("", devicesBefore);
        Assert.Equal(archivesBefore, new KnownDevicesDb(layout.Database).ListScanArchive().Count);
        Assert.Empty(Directory.GetFiles(dir, "*.restore-*"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(zip)!, "*.tmp"));
    }

    [Fact]
    public void Create_HoldsExpectedEntriesAndNoHostOrUserInManifest()
    {
        var layout = Populate(NewDir());
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.2.3");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(BackupService.ManifestName, archive.Entries[0].FullName);
        Assert.Equal(new[] { "ip_scanner.conf", "manifest.json", "overrides.dat", "profiles.json", "scanner.db", "scanner.key" },
            archive.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal));
        string manifestText;
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open())) manifestText = reader.ReadToEnd();
        using var doc = JsonDocument.Parse(manifestText);
        Assert.Equal(1, doc.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("1.2.3", doc.RootElement.GetProperty("appVersion").GetString());
        Assert.DoesNotContain(Environment.MachineName, manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), manifestText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two fake Windows accounts: a blob made for one cannot be opened by the other.</summary>
    private static KeyWrap Account(byte mask) => new(
        raw => new byte[] { mask }.Concat(raw.Select(b => (byte)(b ^ mask))).ToArray(),
        blob => blob.Length > 0 && blob[0] == mask
            ? blob.Skip(1).Select(b => (byte)(b ^ mask)).ToArray()
            : throw new System.Security.Cryptography.CryptographicException("foreign account"));

    private static byte[] RawKeyBytes() => Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    private static BackupLayout PopulateFor(KeyWrap account)
    {
        var layout = Populate(NewDir());
        File.WriteAllBytes(layout.Key, account.Protect(RawKeyBytes()));
        return layout;
    }

    [Fact]
    public void PasswordBackup_CarriesTheRawKeyAndRestoresUnderAnotherAccount()
    {
        var a = Account(0xA5);
        var b = Account(0x5B);
        var layout = PopulateFor(a);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0", keyWrap: a);

        var target = BackupLayout.ForDirectory(NewDir());
        using (var prepared = BackupService.Prepare(zip, Password, target, b)) prepared.Commit("test");

        Assert.Equal(b.Protect(RawKeyBytes()), File.ReadAllBytes(target.Key));
        Assert.Equal(DeviceList(layout), DeviceList(target));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => a.Unprotect(File.ReadAllBytes(target.Key)));
    }

    [Fact]
    public void Create_NeverStagesTheDatabaseBesideTheChosenOutput()
    {
        var layout = Populate(NewDir());
        var outDir = NewDir();
        var zip = Path.Combine(outDir, "backup.zip");
        // The old snapshot name beside the output is blocked; a snapshot there would fail the backup.
        Directory.CreateDirectory(zip + ".db.tmp");

        BackupService.Create(layout, zip, Password, "1.0.0");

        Assert.Equal(new[] { "backup.zip", "backup.zip.db.tmp" },
            Directory.GetFileSystemEntries(outDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "backup.zip" }, Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(layout.Database)!, "*.backup-tmp*"));
        Assert.Equal(DeviceList(layout), DeviceList(Prepared(zip, Password)));
    }

    [Fact]
    public void Restore_NeverWritesTheRawKeyToDisk_AndZeroesItsBuffer()
    {
        var a = Account(0xA5);
        var layout = PopulateFor(a);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0", keyWrap: a);

        var target = BackupLayout.ForDirectory(NewDir());
        var dir = Path.GetDirectoryName(target.Conf)!;
        var raw = RawKeyBytes();
        var onDisk = false;
        byte[]? seen = null;
        var wrap = new KeyWrap(r =>
        {
            seen = r;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (File.ReadAllBytes(f).AsSpan().IndexOf(raw) >= 0) onDisk = true;
            return a.Protect(r);
        }, a.Unprotect);

        using (BackupService.Prepare(zip, Password, target, wrap))
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                Assert.True(File.ReadAllBytes(f).AsSpan().IndexOf(raw) < 0, "raw key found in " + Path.GetFileName(f));
        }

        Assert.False(onDisk);
        Assert.NotNull(seen);
        Assert.All(seen!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void PlainBackup_NeverHoldsTheRawKey_AndRefusesAForeignAccount()
    {
        var a = Account(0xA5);
        var b = Account(0x5B);
        var layout = PopulateFor(a);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.0.0", keyWrap: a);

        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.GetEntry("scanner.key")!;
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            Assert.NotEqual(RawKeyBytes(), ms.ToArray());
            Assert.Equal(a.Protect(RawKeyBytes()), ms.ToArray());
        }

        var target = BackupLayout.ForDirectory(NewDir());
        var ex = Assert.Throws<BackupException>(() => BackupService.Prepare(zip, null, target, b));
        Assert.Equal(BackupFailure.KeyForeign, ex.Failure);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target.Key)!, "*.restore-tmp"));
        using (var same = BackupService.Prepare(zip, null, BackupLayout.ForDirectory(NewDir()), a)) Assert.NotNull(same);
    }

    [Fact]
    public void PasswordBackup_StoresTheKeyEntryRawAndFlagsItInTheManifest()
    {
        var a = Account(0xA5);
        var layout = PopulateFor(a);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0", keyWrap: a);

        var info = BackupService.Inspect(zip, Password);
        Assert.True(info.Encrypted);
        Assert.True(info.Manifest!.KeyRaw);
        var plainZip = Path.Combine(NewDir(), "plain.zip");
        BackupService.Create(layout, plainZip, null, "1.0.0", keyWrap: a);
        Assert.False(BackupService.Inspect(plainZip, null).Manifest!.KeyRaw);
    }

    [Fact]
    public void PasswordBackup_WithUnreadableKey_IsRefusedAndLeavesNoArchive()
    {
        var layout = PopulateFor(Account(0x11));
        var zip = Path.Combine(NewDir(), "backup.zip");
        var ex = Assert.Throws<BackupException>(() => BackupService.Create(layout, zip, Password, "1.0.0", keyWrap: Account(0x22)));
        Assert.Equal(BackupFailure.KeyUnreadable, ex.Failure);
        Assert.False(File.Exists(zip));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(zip)!));
    }

    [Fact]
    public void RawKeyFlag_InAPlainArchive_IsCorrupt()
    {
        var key = "k";
        var manifest = JsonSerializer.Serialize(new
        {
            format = 1, app = "IP-Scanner", appVersion = "1", createdUtc = DateTimeOffset.UtcNow, keyRaw = true,
            files = new[] { new { name = "scanner.key", size = key.Length, sha256 = Sha(key) } }
        });
        var zip = MakeZip(("manifest.json", manifest), ("scanner.key", key));
        var ex = Assert.Throws<BackupException>(() => BackupService.Prepare(zip, null, BackupLayout.ForDirectory(NewDir())));
        Assert.Equal(BackupFailure.Corrupt, ex.Failure);
    }

    [Fact]
    public void SealedDataWithoutAKeyEntry_IsCorrupt()
    {
        var layout = BackupLayout.ForDirectory(NewDir());
        var sealedNoKey = MakeZip(("manifest.json", Manifest(1, ("ip_scanner.conf", "x"), ("overrides.dat", "o"))),
            ("ip_scanner.conf", "x"), ("overrides.dat", "o"));
        Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(() => BackupService.Prepare(sealedNoKey, null, layout)).Failure);

        var rawNoKey = MakeMixedZip(
            ("manifest.json", JsonSerializer.Serialize(new
            {
                format = 1, app = "IP-Scanner", appVersion = "1", createdUtc = DateTimeOffset.UtcNow, keyRaw = true,
                files = new[] { new { name = "ip_scanner.conf", size = 1, sha256 = Sha("x") } }
            }), true),
            ("ip_scanner.conf", "x", true));
        Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(() => BackupService.Prepare(rawNoKey, Password, layout)).Failure);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(layout.Key)!));
    }

    [Fact]
    public void BackupWithoutAKeyEntry_KeepsTheCurrentKey()
    {
        var layout = Populate(NewDir());
        var keyBefore = File.ReadAllBytes(layout.Key);
        var zip = MakeZip(("manifest.json", Manifest(1, ("ip_scanner.conf", "{}"))), ("ip_scanner.conf", "{}"));

        Restore(zip, null, layout);

        Assert.Equal(keyBefore, File.ReadAllBytes(layout.Key));
        Assert.Equal("{}", File.ReadAllText(layout.Conf));
    }

    /// <summary>Entries written one by one; <c>Aes</c> entries are encrypted with <see cref="Password"/>, the others stay plain.</summary>
    private static string MakeMixedZip(params (string Name, string Content, bool Aes)[] entries)
    {
        var path = Path.Combine(NewDir(), "mixed.zip");
        using var file = File.Create(path);
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(file);
        foreach (var (name, content, aes) in entries)
        {
            zip.Password = aes ? Password : null;
            var entry = new ICSharpCode.SharpZipLib.Zip.ZipEntry(name);
            if (aes) entry.AESKeySize = 256;
            zip.PutNextEntry(entry);
            var bytes = Encoding.UTF8.GetBytes(content);
            zip.Write(bytes, 0, bytes.Length);
            zip.CloseEntry();
        }
        zip.Finish();
        return path;
    }

    private static string RawKeyManifest(string conf, string key, string? extraName = null, string? extra = null)
    {
        var files = new List<object>
        {
            new { name = "ip_scanner.conf", size = conf.Length, sha256 = Sha(conf) },
            new { name = "scanner.key", size = key.Length, sha256 = Sha(key) },
        };
        if (extraName is not null) files.Add(new { name = extraName, size = extra!.Length, sha256 = Sha(extra) });
        return JsonSerializer.Serialize(new
        {
            format = 1, app = "IP-Scanner", appVersion = "1", createdUtc = DateTimeOffset.UtcNow, keyRaw = true, files
        });
    }

    [Theory]
    [InlineData(Password)]
    [InlineData("some other password")]
    [InlineData(null)]
    public void PlainEntriesBesideAnAesJunkEntry_AreRefusedWithAnyPassword(string? password)
    {
        const string conf = "attacker conf";
        var key = new string('k', 32);
        // Plain manifest, plain config and plain key, plus one AES entry that makes the archive look encrypted.
        var zip = MakeMixedZip(
            ("manifest.json", RawKeyManifest(conf, key, "profiles.json", "[]"), false),
            ("ip_scanner.conf", conf, false), ("scanner.key", key, false),
            ("profiles.json", "[]", true));
        var layout = BackupLayout.ForDirectory(NewDir());

        Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(() => BackupService.Prepare(zip, password, layout)).Failure);
        Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(() => BackupService.Inspect(zip, password)).Failure);
        Assert.False(File.Exists(layout.Key));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(layout.Key)!));
    }

    [Fact]
    public void DuplicateEntryNames_AreRefused_EvenWhenCaseDiffers()
    {
        const string conf = "attacker conf";
        var key = new string('k', 32);
        var manifest = RawKeyManifest(conf, key);
        foreach (var second in new[] { "ip_scanner.conf", "IP_SCANNER.CONF" })
        {
            var plainDuplicate = MakeMixedZip(("manifest.json", manifest, false), ("ip_scanner.conf", conf, false),
                ("scanner.key", key, false), (second, "junk", false));
            var ex = Assert.Throws<BackupException>(() => BackupService.Prepare(plainDuplicate, null, BackupLayout.ForDirectory(NewDir())));
            Assert.Equal(BackupFailure.Corrupt, ex.Failure);
            Assert.Equal("duplicate entry", ex.Message);
        }

        var aesDuplicate = MakeMixedZip(("manifest.json", manifest, false), ("ip_scanner.conf", conf, false),
            ("scanner.key", key, false), ("ip_scanner.conf", "junk", true));
        Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(
            () => BackupService.Prepare(aesDuplicate, Password, BackupLayout.ForDirectory(NewDir()))).Failure);
    }

    [Fact]
    public void GenuineEncryptedBackup_StillRestoresAfterTheEntryChecks()
    {
        var layout = Populate(NewDir());
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0");
        Assert.Equal(DeviceList(layout), DeviceList(Prepared(zip, Password)));
    }

    [Fact]
    public void ManifestEntry_WithoutName_OrWithAbsurdSize_IsCorrupt()
    {
        var noName = "{\"format\":1,\"app\":\"IP-Scanner\",\"appVersion\":\"1\",\"createdUtc\":\"2026-10-01T00:00:00Z\",\"files\":[{\"size\":1,\"sha256\":\"" + Sha("x") + "\"}]}";
        var ex = Assert.Throws<BackupException>(() => BackupService.Prepare(MakeZip(("manifest.json", noName), ("ip_scanner.conf", "x")), null, BackupLayout.ForDirectory(NewDir())));
        Assert.Equal(BackupFailure.Corrupt, ex.Failure);

        var huge = JsonSerializer.Serialize(new
        {
            format = 1, app = "IP-Scanner", appVersion = "1", createdUtc = DateTimeOffset.UtcNow,
            files = new[] { new { name = "ip_scanner.conf", size = long.MaxValue / 2, sha256 = Sha("x") } }
        });
        ex = Assert.Throws<BackupException>(() => BackupService.Prepare(MakeZip(("manifest.json", huge), ("ip_scanner.conf", "x")), null, BackupLayout.ForDirectory(NewDir())));
        Assert.Equal(BackupFailure.Corrupt, ex.Failure);
    }

    [Fact]
    public void RestoreGate_RefusesWhileScanningOrAlreadyRestoring_AndAllowsAgainAfterLeaving()
    {
        var gate = new RestoreGate();
        Assert.False(gate.TryEnter(scanning: true));
        Assert.False(gate.Active);

        Assert.True(gate.TryEnter(scanning: false));
        Assert.True(gate.Active);
        Assert.False(gate.TryEnter(scanning: false));

        Assert.True(gate.CanCommit(scanning: false));
        Assert.False(gate.CanCommit(scanning: true));   // a scan that started while the archive was staged

        gate.Leave();
        Assert.False(gate.Active);
        Assert.False(gate.CanCommit(scanning: false));
        Assert.True(gate.TryEnter(scanning: false));
    }

    [Fact]
    public async System.Threading.Tasks.Task RestoreGate_WaitsForTheLastConfigWrite_EvenWhenItFailed()
    {
        var gate = new RestoreGate();
        var release = new System.Threading.Tasks.TaskCompletionSource();
        gate.Track(release.Task);
        var drain = gate.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.SetException(new IOException("disk full"));
        await drain;

        gate.Track(System.Threading.Tasks.Task.CompletedTask);
        await gate.DrainAsync();
    }

    [Fact]
    public async System.Threading.Tasks.Task RestoreGate_WaitsForEveryPendingConfigWrite_NotOnlyTheLast()
    {
        var gate = new RestoreGate();
        var slow = new System.Threading.Tasks.TaskCompletionSource();
        gate.Track(slow.Task);
        gate.Track(System.Threading.Tasks.Task.CompletedTask);
        var drain = gate.DrainAsync();
        await System.Threading.Tasks.Task.Delay(50);
        Assert.False(drain.IsCompleted);
        slow.SetResult();
        await drain;
    }

    [Fact]
    public void PlainBackup_OpensWithStandardZipReader()
    {
        var layout = Populate(NewDir());
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, "", "1.0.0");

        var info = BackupService.Inspect(zip, null);
        Assert.False(info.Encrypted);
        Assert.Equal(1, info.Manifest!.Format);
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("ip_scanner.conf")!.Open());
        Assert.Contains("192.0.2.0/24", reader.ReadToEnd());
    }

    [Fact]
    public void EncryptedBackup_UsesWinZipAes256Method99()
    {
        var layout = Populate(NewDir());
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0");

        var headers = CentralHeaders(File.ReadAllBytes(zip));
        Assert.True(headers.Count >= 5);
        foreach (var h in headers)
        {
            Assert.Equal(99, h.Method);                 // WinZip AES
            Assert.Equal(0x9901, h.AesExtraId);
            Assert.Equal(2, h.AesVendorVersion);        // AE-2
            Assert.Equal(3, h.AesStrength);             // 3 = AES-256
        }
        Assert.True(BackupService.Inspect(zip, null).Encrypted);
        Assert.Null(BackupService.Inspect(zip, null).Manifest);
        Assert.Equal(1, BackupService.Inspect(zip, Password).Manifest!.Format);
    }

    private sealed record Header(int Method, int AesExtraId, int AesVendorVersion, int AesStrength);

    private static List<Header> CentralHeaders(byte[] b)
    {
        var result = new List<Header>();
        for (int i = 0; i + 46 < b.Length; i++)
        {
            if (b[i] != 0x50 || b[i + 1] != 0x4b || b[i + 2] != 1 || b[i + 3] != 2) continue;
            int method = BitConverter.ToUInt16(b, i + 10);
            int nameLen = BitConverter.ToUInt16(b, i + 28), extraLen = BitConverter.ToUInt16(b, i + 30);
            int p = i + 46 + nameLen, end = p + extraLen;
            int id = 0, version = 0, strength = 0;
            while (p + 4 <= end)
            {
                int tag = BitConverter.ToUInt16(b, p), size = BitConverter.ToUInt16(b, p + 2);
                if (tag == 0x9901) { id = tag; version = BitConverter.ToUInt16(b, p + 4); strength = b[p + 8]; }
                p += 4 + size;
            }
            result.Add(new Header(method, id, version, strength));
            i += 45;
        }
        return result;
    }

    [Fact]
    public void WrongPassword_IsRejectedBeforeAnyFileIsReplaced()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0");
        File.WriteAllText(layout.Conf, "changed after the backup");
        var before = Fingerprint(layout);

        var wrong = Assert.Throws<BackupException>(() => BackupService.Prepare(zip, "not the password", layout));
        var missing = Assert.Throws<BackupException>(() => BackupService.Prepare(zip, null, layout));
        var inspectWrong = Assert.Throws<BackupException>(() => BackupService.Inspect(zip, "nope"));

        Assert.Equal(BackupFailure.WrongPassword, wrong.Failure);
        Assert.Equal(BackupFailure.PasswordRequired, missing.Failure);
        Assert.Equal(BackupFailure.WrongPassword, inspectWrong.Failure);
        Assert.Equal(before, Fingerprint(layout));
        Assert.Empty(Directory.GetFiles(dir, "*.restore-*"));
        Assert.False(Directory.Exists(Path.Combine(dir, "Backups")));
    }

    [Fact]
    public void Commit_KeepsASafetyBackupOfTheStateItReplaces()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.0.0");
        File.WriteAllText(layout.Conf, "state before the restore");

        using var prepared = BackupService.Prepare(zip, null, layout);
        var safety = prepared.Commit("1.0.0");

        Assert.NotNull(safety);
        Assert.StartsWith(Path.Combine(dir, "Backups"), safety);
        using var archive = ZipFile.OpenRead(safety!);
        using var reader = new StreamReader(archive.GetEntry("ip_scanner.conf")!.Open());
        Assert.Equal("state before the restore", reader.ReadToEnd());
        Assert.Contains("192.0.2.0/24", File.ReadAllText(layout.Conf));
    }

    [Fact]
    public void SafetyBackup_IsCreatedPrivateAndHoldsTheCurrentState()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.0.0");

        using var prepared = BackupService.Prepare(zip, null, layout);
        var safety = prepared.Commit("1.0.0");

        Assert.NotNull(safety);
        Assert.True(new FileInfo(safety!).GetAccessControl().AreAccessRulesProtected);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(safety)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(safety)!, "*.db.tmp"));
    }

    [Fact]
    public void Create_WithPrivateOutput_ProtectsTheArchiveAndLeavesNoSnapshot()
    {
        var layout = Populate(NewDir());
        var outDir = NewDir();
        var zip = Path.Combine(outDir, "private.zip");
        var open = Path.Combine(outDir, "open.zip");

        BackupService.Create(layout, zip, null, "1.0.0", privateOutput: true);
        BackupService.Create(layout, open, null, "1.0.0");

        Assert.True(new FileInfo(zip).GetAccessControl().AreAccessRulesProtected);
        Assert.False(new FileInfo(open).GetAccessControl().AreAccessRulesProtected);
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
        Assert.Equal(DeviceList(layout), DeviceList(Prepared(zip)));
    }

    private static BackupLayout Prepared(string zip, string? password = null)
    {
        var target = BackupLayout.ForDirectory(NewDir());
        Restore(zip, password, target);
        return target;
    }

    [Fact]
    public void NotABackup_AndNewerFormat_AreRefused()
    {
        var layout = Populate(NewDir());
        var before = Fingerprint(layout);
        var text = Path.Combine(NewDir(), "notes.zip");
        File.WriteAllText(text, "plain text, not an archive");
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupService.Prepare(text, null, layout)).Failure);

        var noManifest = MakeZip(("ip_scanner.conf", "x"));
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupService.Prepare(noManifest, null, layout)).Failure);

        var newer = MakeZip(("manifest.json", Manifest(2, ("ip_scanner.conf", "x"))), ("ip_scanner.conf", "x"));
        Assert.Equal(BackupFailure.NewerFormat, Assert.Throws<BackupException>(() => BackupService.Prepare(newer, null, layout)).Failure);

        var foreign = MakeZip(("manifest.json", "{\"format\":1,\"app\":\"Other\",\"files\":[]}"));
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupService.Prepare(foreign, null, layout)).Failure);
        Assert.Equal(before, Fingerprint(layout));
    }

    [Fact]
    public void TamperedOrHostileArchives_AreRefusedWithoutTouchingFiles()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var before = Fingerprint(layout);

        var wrongHash = MakeZip(("manifest.json", Manifest(1, ("ip_scanner.conf", "expected"))), ("ip_scanner.conf", "tampered"));
        var traversal = MakeZip(("manifest.json", Manifest(1, ("..\\evil.txt", "x"), ("ip_scanner.conf", "x"))),
            ("..\\evil.txt", "x"), ("ip_scanner.conf", "x"));
        var unlisted = MakeZip(("manifest.json", Manifest(1, ("ip_scanner.conf", "x"))), ("ip_scanner.conf", "x"), ("extra.bin", "y"));
        var badDb = MakeZip(("manifest.json", Manifest(1, ("scanner.db", "this is not a sqlite database at all"))),
            ("scanner.db", "this is not a sqlite database at all"));
        var empty = MakeZip(("manifest.json", Manifest(1)));

        foreach (var zip in new[] { wrongHash, traversal, unlisted, badDb, empty })
            Assert.Equal(BackupFailure.Corrupt, Assert.Throws<BackupException>(() => BackupService.Prepare(zip, null, layout)).Failure);
        Assert.Equal(before, Fingerprint(layout));
        Assert.Empty(Directory.GetFiles(dir, "*.restore-*"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil.txt")));
    }

    [Fact]
    public void Commit_FailureMidSwap_PutsTheOriginalFilesBack()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.0.0");
        File.WriteAllText(layout.Conf, "current config");
        File.WriteAllText(layout.Overrides, "current overrides");
        using var prepared = BackupService.Prepare(zip, null, layout);

        // The overrides file is swapped after the config; holding it open makes that swap fail.
        using (new FileStream(layout.Overrides, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<BackupException>(() => prepared.Commit("1.0.0"));
            Assert.Equal(BackupFailure.Io, ex.Failure);
        }

        Assert.Equal("current config", File.ReadAllText(layout.Conf));
        Assert.Equal("current overrides", File.ReadAllText(layout.Overrides));
        Assert.Empty(Directory.GetFiles(dir, "*.restore-*"));
    }

    [Fact]
    public void Restore_RemovesManagedFilesTheBackupDoesNotHave()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        File.Delete(layout.Profiles);
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, null, "1.0.0");
        File.WriteAllText(layout.Profiles, "later profiles");

        Restore(zip, null, layout);

        Assert.False(File.Exists(layout.Profiles));
        Assert.True(File.Exists(layout.Conf));
    }

    [Fact]
    public void Create_SnapshotsADatabaseThatHasUncheckpointedWrites()
    {
        var dir = NewDir();
        var layout = Populate(dir);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = layout.Database }.ConnectionString;
        using var open = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        open.Open();
        using (var cmd = open.CreateCommand()) { cmd.CommandText = "PRAGMA wal_autocheckpoint=0"; cmd.ExecuteNonQuery(); }
        new KnownDevicesDb(layout.Database).Save(Network, [new Device("192.0.2.77") { Mac = "AA:00:00:00:00:77", Hostname = "late" }], "2026-10-02T08:00:00Z");
        var expected = DeviceList(layout);
        var zip = Path.Combine(NewDir(), "backup.zip");

        BackupService.Create(layout, zip, null, "1.0.0");
        var other = NewDir();
        var target = BackupLayout.ForDirectory(other);
        Restore(zip, null, target);

        Assert.Contains("late", expected);
        Assert.Equal(expected, DeviceList(target));
    }

    [Fact]
    public void EncryptedBackup_OpensInSevenZipWithTheSamePassword()
    {
        const string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (!File.Exists(sevenZip)) return;   // only checked where 7-Zip is installed
        var layout = Populate(NewDir());
        var zip = Path.Combine(NewDir(), "backup.zip");
        BackupService.Create(layout, zip, Password, "1.0.0");

        Assert.Equal(0, RunSevenZip(sevenZip, "t", "-p" + Password, zip));
        Assert.NotEqual(0, RunSevenZip(sevenZip, "t", "-pwrong-password", zip));
    }

    private static int RunSevenZip(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return p.ExitCode;
    }

    private static string Sha(string content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Manifest(int format, params (string Name, string Content)[] files)
        => JsonSerializer.Serialize(new
        {
            format, app = "IP-Scanner", appVersion = "1", createdUtc = DateTimeOffset.UtcNow,
            files = files.Select(f => new { name = f.Name, size = Encoding.UTF8.GetByteCount(f.Content), sha256 = Sha(f.Content) }).ToArray()
        });

    private static string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(NewDir(), "made.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }
}
