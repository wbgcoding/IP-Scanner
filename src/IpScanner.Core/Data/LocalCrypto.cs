using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IpScanner.Core.Data;

/// <summary>Seals device data (scanner.db, overrides) with a per-installation
/// key, itself protected by DPAPI (tied to the current Windows account, no password ever
/// asked). AesGcm gives confidentiality + integrity for the sealed payload; a separate
/// deterministic HMAC token lets a column stay searchable/indexable without decrypting
/// every row just to find one.</summary>
public sealed class LocalCrypto
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    private LocalCrypto(byte[] key) => _key = key;

    /// <summary>Loads the key from <paramref name="keyPath"/>, generating and storing a
    /// fresh one on first use. Returns null (never throws) if the file exists but can't be
    /// unprotected - almost always because it was created under a different Windows
    /// account; the caller offers "start a new database?" instead of crashing.</summary>
    public static LocalCrypto? TryLoad(string keyPath)
    {
        var existing = TryLoadExisting(keyPath);
        if (existing is not null) return existing;
        if (File.Exists(keyPath)) return null;   // present but unusable -> foreign account
        // A fresh key could never open sealed files that already exist; they would be reported as damaged.
        if (SealedFilesExistBeside(keyPath)) return null;
        try
        {
            var raw = RandomNumberGenerator.GetBytes(KeyBytes);
            var dir = Path.GetDirectoryName(keyPath);
            var owned = string.IsNullOrEmpty(dir) || FileProtection.PrepareDirectory(dir);
            File.WriteAllBytes(keyPath, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
            if (!owned) FileProtection.RestrictFile(keyPath);
            return new LocalCrypto(raw);
        }
        catch (CryptographicException ex) { WarnOnce(keyPath, "Decrypting stored data failed", ex); return null; }
        catch (IOException ex) { WarnOnce(keyPath, "Reading the key file failed", ex); return null; }
        catch (UnauthorizedAccessException ex) { WarnOnce(keyPath, "Reading the key file was denied", ex); return null; }
    }

    // The key file is probed on every load of the database and the sealed stores; a key from a foreign
    // account would otherwise write the same warning on each of those calls.
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    private static void WarnOnce(string keyPath, string message, Exception ex)
    {
        bool first;
        lock (Warned) first = Warned.Add(keyPath + "|" + message);
        if (first) Log.Warn(LogArea.Config, message, ex);
    }

    private static bool SealedFilesExistBeside(string keyPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(keyPath);
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(dir, SealedSettings.FileName))
                || File.Exists(Path.Combine(dir, Models.ScanConfig.OverridesFileName));
        }
        catch (Exception ex) { Log.Warn(LogArea.Config, "Checking the key file failed", ex); return false; }
    }

    /// <summary>Read-only counterpart of <see cref="TryLoad"/> - never creates a key file.
    /// Used to open a foreign database's own <c>scanner.key</c> during a merge/import: the
    /// same Windows account can unprotect it regardless of which folder it lives in (DPAPI
    /// is account-scoped, not per-installation), so this succeeds whenever the two
    /// databases were made under the same account, and fails cleanly otherwise.</summary>
    public static LocalCrypto? TryLoadExisting(string keyPath)
    {
        if (!File.Exists(keyPath)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
            return raw.Length == KeyBytes ? new LocalCrypto(raw) : null;
        }
        catch (CryptographicException ex) { WarnOnce(keyPath, "Decrypting stored data failed", ex); return null; }
        catch (IOException ex) { WarnOnce(keyPath, "Reading the key file failed", ex); return null; }
        catch (UnauthorizedAccessException ex) { WarnOnce(keyPath, "Reading the key file was denied", ex); return null; }
    }

    /// <summary>Encrypts <paramref name="value"/> to <c>nonce|tag|ciphertext</c>.</summary>
    public byte[] Seal(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceBytes + TagBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, result, NonceBytes, TagBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceBytes + TagBytes, ciphertext.Length);
        return result;
    }

    /// <summary>Decrypts a <see cref="Seal"/>-produced blob; null on any tamper/format/key
    /// mismatch (wrong installation's key, corrupted row) rather than throwing.</summary>
    public string? Open(byte[] sealedValue)
    {
        if (sealedValue.Length < NonceBytes + TagBytes) return null;
        try
        {
            var nonce = sealedValue.AsSpan(0, NonceBytes);
            var tag = sealedValue.AsSpan(NonceBytes, TagBytes);
            var ciphertext = sealedValue.AsSpan(NonceBytes + TagBytes);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex) { Log.Warn(LogArea.Config, "Decrypting stored data failed", ex); return null; }
    }

    /// <summary>Deterministic lookup token for <paramref name="value"/> (case-insensitive) -
    /// same input always yields the same token under this key, so it can be an indexed
    /// column, but it reveals nothing about the value without the key.</summary>
    public string Token(string value)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))
            .ToLowerInvariant();
    }
}
