using System.Security.Cryptography;
using System.Text;

// Release signing tool for IP-Scanner updates.
//   init          create the signing key pair; prints only the public key (base64 SubjectPublicKeyInfo)
//   sign <exe>    write <exe>.sig (base64 ECDSA P-256 / SHA-256 signature over the raw exe bytes)
//                 and <exe>.sha256 (hex digest)
//   verify <exe> <publicKey>   check <exe>.sig against a public key (base64 SubjectPublicKeyInfo)
// The private key lives DPAPI-protected (current user) in %APPDATA%\IP-Scanner-signing\key.bin,
// outside the repository, and is never printed.

return Run(args);

static string KeyPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IP-Scanner-signing", "key.bin");

static int Run(string[] args)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("This tool needs Windows (DPAPI).");
        return 1;
    }
    switch (args.Length > 0 ? args[0] : "")
    {
        case "init" when args.Length == 1: return Init();
        case "sign" when args.Length == 2: return Sign(args[1]);
        case "verify" when args.Length == 3: return Verify(args[1], args[2]);
        default:
            Console.Error.WriteLine("Usage: ReleaseSigner init | sign <exe> | verify <exe> <publicKey>");
            return 1;
    }
}

static int Init()
{
    var path = KeyPath();
    if (File.Exists(path))
    {
        Console.Error.WriteLine("A signing key already exists; init refuses to replace it. Nothing was changed.");
        return 2;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    byte[] protectedKey;
    var plain = key.ExportPkcs8PrivateKey();
    try { protectedKey = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
    finally { CryptographicOperations.ZeroMemory(plain); }
    // CreateNew: a concurrent init can never overwrite an existing key.
    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        fs.Write(protectedKey);
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Sign(string exePath)
{
    var path = KeyPath();
    if (!File.Exists(exePath)) { Console.Error.WriteLine("Exe not found: " + exePath); return 1; }
    if (!File.Exists(path)) { Console.Error.WriteLine("No signing key; run init first."); return 2; }

    byte[] pkcs8;
    try { pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser); }
    catch (CryptographicException)
    {
        Console.Error.WriteLine("The signing key cannot be opened with this Windows account.");
        return 2;
    }
    using var key = ECDsa.Create();
    try { key.ImportPkcs8PrivateKey(pkcs8, out _); }
    finally { CryptographicOperations.ZeroMemory(pkcs8); }

    // The exe is read once; digest, signature and self-check all cover exactly these bytes, so a
    // concurrent rewrite of the file cannot produce a .sig and a .sha256 for different content.
    var data = File.ReadAllBytes(exePath);
    var hash = SHA256.HashData(data);
    var signature = key.SignHash(hash);

    // Self-check before anything is written: the signature must verify with the matching public key.
    if (!key.VerifyHash(hash, signature))
    {
        Console.Error.WriteLine("Signature self-check failed; nothing was written.");
        return 3;
    }

    var ascii = new UTF8Encoding(false);
    File.WriteAllText(exePath + ".sig", Convert.ToBase64String(signature), ascii);
    File.WriteAllText(exePath + ".sha256", Convert.ToHexString(hash) + "  " + Path.GetFileName(exePath), ascii);
    Console.WriteLine("Signed " + Path.GetFileName(exePath) + " (" + Path.GetFileName(exePath) + ".sig, " + Path.GetFileName(exePath) + ".sha256)");
    return 0;
}

static int Verify(string exePath, string publicKey)
{
    try
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var signature = Convert.FromBase64String(File.ReadAllText(exePath + ".sig").Trim());
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ok = key.VerifyData(fs, signature, HashAlgorithmName.SHA256);
        Console.WriteLine(ok ? "Signature valid." : "Signature INVALID.");
        return ok ? 0 : 3;
    }
    catch (Exception ex) when (ex is FormatException or CryptographicException or IOException)
    {
        Console.Error.WriteLine("Signature INVALID or unreadable.");
        return 3;
    }
}
