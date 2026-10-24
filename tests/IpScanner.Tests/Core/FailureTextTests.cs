using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using IpScanner.Core;
using IpScanner.Core.Localization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("Localization")]
public class FailureTextTests
{
    public static TheoryData<Exception, string> Cases => new()
    {
        { new UnauthorizedAccessException(), "write access" },
        { new IOException(), "open in another program" },
        { new FileNotFoundException(), "open in another program" },
        { new InvalidDataException(), "expected format" },
        { new FormatException(), "expected format" },
        { new JsonException(), "expected format" },
        { new SqliteException("locked", 5), "database" },
        { new CryptographicException(), "different Windows account" },
        { new UpdateSignatureException(), "signature is invalid" },
        { new InvalidOperationException(), "failed unexpectedly" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Describe_WithPath_MapsEachExceptionType(Exception ex, string expectedFragment)
    {
        Loc.SetLanguage("en");
        var (cause, next) = FailureText.Describe(ex, @"C:\data\x.dat");
        Assert.Contains(expectedFragment, cause);
        Assert.False(string.IsNullOrWhiteSpace(next));
        Assert.DoesNotContain("{0}", cause);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Describe_WithoutPath_DropsTheFileName(Exception ex, string expectedFragment)
    {
        Loc.SetLanguage("en");
        foreach (var path in new string?[] { null, "", "  " })
        {
            var (cause, next) = FailureText.Describe(ex, path);
            Assert.Contains(expectedFragment, cause);
            Assert.DoesNotContain("{0}", cause);
            Assert.DoesNotContain("\"", cause);
            Assert.False(string.IsNullOrWhiteSpace(next));
        }
    }

    [Fact]
    public void Describe_UpdateSignature_WarnsAndNeverSendsTheUserToDownloadByHand()
    {
        Loc.SetLanguage("en");
        var (cause, next) = FailureText.Describe(new UpdateSignatureException(), null);
        Assert.Equal("The update was not installed because its signature is invalid.", cause);
        Assert.Equal("The download may have been tampered with; do not install it by hand.", next);
        Assert.DoesNotContain("download page", cause + next, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_UpdateSignature_GermanWording()
    {
        Loc.SetLanguage("de");
        try
        {
            var (cause, next) = FailureText.Describe(new UpdateSignatureException(), null);
            Assert.Equal("Das Update wurde nicht installiert, weil die Signatur ungültig ist.", cause);
            Assert.Equal("Der Download könnte manipuliert sein; installiere ihn nicht von Hand.", next);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Describe_WithPath_NamesThePathInTheSentence()
    {
        Loc.SetLanguage("en");
        Assert.Equal("No write access to \"D:\\out\".", FailureText.Describe(new UnauthorizedAccessException(), @"D:\out").Cause);
        Assert.Equal("The file \"a.json\" is not in the expected format.", FailureText.Describe(new JsonException(), "a.json").Cause);
    }

    [Fact]
    public void Describe_GermanWording_FollowsActiveLanguage()
    {
        Loc.SetLanguage("de");
        try
        {
            var (cause, next) = FailureText.Describe(new UnauthorizedAccessException(), "X");
            Assert.Equal("Keine Schreibrechte für „X“.", cause);
            Assert.Equal("Wähle einen anderen Ordner oder gib dir dort Schreibrechte.", next);
        }
        finally { Loc.SetLanguage("en"); }
    }
}
