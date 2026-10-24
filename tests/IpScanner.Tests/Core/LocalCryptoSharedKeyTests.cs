using System;
using System.IO;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class LocalCryptoSharedKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string KeyPath => Path.Combine(_dir, "scanner.key");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void TwoInstancesOnOneKeyFile_ShareTokensAndCanOpenEachOthersData()
    {
        var a = LocalCrypto.TryLoad(KeyPath)!;
        var b = LocalCrypto.TryLoad(KeyPath)!;
        Assert.Equal(a.Token("AA:BB:CC:00:11:22"), b.Token("aa:bb:cc:00:11:22"));
        Assert.Equal("hello", b.Open(a.Seal("hello")));
    }

    [Fact]
    public void ExistingLoad_AfterCreate_UsesTheSameKey()
    {
        var created = LocalCrypto.TryLoad(KeyPath)!;
        var again = LocalCrypto.TryLoadExisting(KeyPath)!;
        Assert.Equal(created.Token("x"), again.Token("x"));
    }

    [Fact]
    public void Token_IsLowercaseHexOf64Chars_AndDiffersPerValue()
    {
        var c = LocalCrypto.TryLoad(KeyPath)!;
        var t = c.Token("AA:BB:CC:00:11:22");
        Assert.Equal(64, t.Length);
        Assert.Matches("^[0-9a-f]{64}$", t);
        Assert.NotEqual(t, c.Token("AA:BB:CC:00:11:23"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Größe 日本語 ä")]
    [InlineData("line1\nline2\ttab")]
    public void SealOpen_RoundTripsAwkwardText(string text)
    {
        var c = LocalCrypto.TryLoad(KeyPath)!;
        Assert.Equal(text, c.Open(c.Seal(text)));
    }

    [Fact]
    public void Seal_SameTextTwice_GivesDifferentCiphertext()
    {
        var c = LocalCrypto.TryLoad(KeyPath)!;
        Assert.NotEqual(c.Seal("same"), c.Seal("same"));
    }

    [Fact]
    public void Open_TooShortBlob_ReturnsNull()
    {
        var c = LocalCrypto.TryLoad(KeyPath)!;
        Assert.Null(c.Open(new byte[5]));
    }
}
