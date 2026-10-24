using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetworkTextTests
{
    [Fact]
    public void Sanitize_PlainText_Unchanged()
        => Assert.Equal("nginx/1.18.0", NetworkText.Sanitize("nginx/1.18.0"));

    [Fact]
    public void Sanitize_StripsControlCharacters()
    {
        // A NUL (0x00) and a bell (0x07) spliced into an otherwise normal banner -
        // built from char casts, not embedded literally, so the source file itself
        // never carries a raw control byte.
        var dirty = "SSH-2.0-" + (char)0 + (char)7 + "OpenSSH_8.9evil";
        Assert.Equal("SSH-2.0-OpenSSH_8.9evil", NetworkText.Sanitize(dirty));
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var result = NetworkText.Sanitize(new string('x', 200), maxLen: 60);
        Assert.Equal(60, result.Length);
    }

    [Fact]
    public void Sanitize_OnlyControlCharacters_ReturnsEmpty()
    {
        var dirty = "" + (char)1 + (char)2 + (char)3;
        Assert.Equal("", NetworkText.Sanitize(dirty));
    }

    [Fact]
    public void Sanitize_TrimsWhitespaceAfterStripping()
        => Assert.Equal("host", NetworkText.Sanitize("  host  "));

    [Fact]
    public void Sanitize_AllowedLineBreaks_PreservesLfButStripsOtherControls()
        => Assert.Equal("first\nsecond", NetworkText.Sanitize("first\r\nsec" + (char)0 + "ond\t", 2000, allowLineBreaks: true));
}
