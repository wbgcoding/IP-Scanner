using System.IO;
using System.Text;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class LocalCryptoTests
{
    private static string TempKeyPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.key");

    [Fact]
    public void Seal_ThenOpen_RoundTrips()
    {
        var crypto = LocalCrypto.TryLoad(TempKeyPath());
        Assert.NotNull(crypto);
        var sealedValue = crypto!.Seal("192.168.1.1\tAA:BB:CC:11:22:33\trouter");
        Assert.Equal("192.168.1.1\tAA:BB:CC:11:22:33\trouter", crypto.Open(sealedValue));
    }

    [Fact]
    public void Open_TamperedCiphertext_ReturnsNullInsteadOfThrowing()
    {
        var crypto = LocalCrypto.TryLoad(TempKeyPath());
        var sealedValue = crypto!.Seal("secret");
        sealedValue[^1] ^= 0xFF;   // flip the last ciphertext byte
        Assert.Null(crypto.Open(sealedValue));
    }

    [Fact]
    public void Token_IsDeterministic_AndCaseInsensitive()
    {
        var crypto = LocalCrypto.TryLoad(TempKeyPath());
        Assert.Equal(crypto!.Token("aa:bb:cc:11:22:33"), crypto.Token("AA:BB:CC:11:22:33"));
    }

    [Fact]
    public void Token_DifferentKeys_ProduceDifferentTokens()
    {
        var a = LocalCrypto.TryLoad(TempKeyPath());
        var b = LocalCrypto.TryLoad(TempKeyPath());
        Assert.NotEqual(a!.Token("AA:BB:CC:11:22:33"), b!.Token("AA:BB:CC:11:22:33"));
    }

    [Fact]
    public void TryLoad_SecondCallSamePath_ReusesSameKey()
    {
        var path = TempKeyPath();
        var first = LocalCrypto.TryLoad(path);
        var second = LocalCrypto.TryLoad(path);
        Assert.Equal(first!.Token("x"), second!.Token("x"));
    }

    [Fact]
    public void TryLoad_UnreadableKeyFile_ReturnsNullInsteadOfThrowing()
    {
        // Simulates a key created under a different Windows account -
        // ProtectedData.Unprotect fails, TryLoad must report "unusable", not crash.
        var path = TempKeyPath();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a real DPAPI blob"));
        Assert.Null(LocalCrypto.TryLoad(path));
        Assert.Null(LocalCrypto.TryLoadExisting(path));
    }

    [Fact]
    public void TryLoadExisting_NoFile_ReturnsNullWithoutCreatingOne()
    {
        var path = TempKeyPath();
        Assert.Null(LocalCrypto.TryLoadExisting(path));
        Assert.False(File.Exists(path));
    }
}
