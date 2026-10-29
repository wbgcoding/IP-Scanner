using System;
using System.Linq;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class OpenWithBuilderTests
{
    private const string Win = @"C:\Windows";

    public static TheoryData<string?> HostileAddresses => new()
    {
        null, "", " ", " 192.168.1.5", "192.168.1.5 ", "192.168.1.5\n", "192.168.1.5:80",
        "192.168.1.5 & calc", "192.168.1.5;calc", "192.168.1.5|calc", "192.168.1.5\"", "192.168.1.5'",
        "-oProxyCommand=calc", "/v:evil", "//evil/share", @"\\evil\share", "file:///c:/windows",
        "http://evil.example", "evil.example", "printer.local", "AA:BB:CC:DD:EE:FF",
        "127.1", "0x7f.0.0.1", "0177.0.0.1", "010.0.0.1", "2130706433", "192.168.1", "192.168.1.256",
        "192.168.001.005", "0.0.0.0", "255.255.255.255", "224.0.0.1", "239.255.255.250",
        "::", "ff02::1", "[fe80::1]", "fe80::1%", "1::2::3", new string('1', 200),
    };

    [Theory]
    [MemberData(nameof(HostileAddresses))]
    public void NonCanonicalOrHostileAddress_YieldsNoCommand(string? address)
    {
        foreach (var kind in Enum.GetValues<OpenWithKind>())
            Assert.Null(OpenWithBuilder.Build(kind, address, Win));
    }

    [Fact]
    public void Browser_BuildsAPlainUrlStartedThroughTheShell()
    {
        var http = OpenWithBuilder.Build(OpenWithKind.BrowserHttp, "192.168.1.5", Win)!;
        Assert.Equal("http://192.168.1.5/", http.FileName);
        Assert.True(http.UseShellExecute);
        Assert.Empty(http.Arguments);

        var https = OpenWithBuilder.Build(OpenWithKind.BrowserHttps, "10.0.0.1", Win)!;
        Assert.Equal("https://10.0.0.1/", https.FileName);
        Assert.True(https.UseShellExecute);
    }

    [Fact]
    public void Browser_Ipv6IsBracketed()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.BrowserHttps, "fe80::1", Win)!;
        Assert.Equal("https://[fe80::1]/", c.FileName);
    }

    [Fact]
    public void Browser_Ipv6ScopeIdIsPercentEncodedAndStaysOneHost()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.BrowserHttp, "fe80::1%5", Win);
        Assert.NotNull(c);
        var uri = new Uri(c!.FileName);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal("/", uri.AbsolutePath);
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
        Assert.DoesNotContain(" ", c.FileName);
        Assert.Equal("http://[fe80::1]/", c.FileName);
        Assert.Equal("[fe80::1]", uri.Host);
    }

    [Theory]
    [InlineData("fe80::1", "/v:[fe80::1]")]
    [InlineData("fe80::1%5", "/v:[fe80::1%5]")]
    [InlineData("2001:db8::7", "/v:[2001:db8::7]")]
    [InlineData("192.168.1.5", "/v:192.168.1.5")]
    public void RemoteDesktop_BracketsAnIpv6Address(string address, string expected)
        => Assert.Equal(new[] { expected }, OpenWithBuilder.Build(OpenWithKind.RemoteDesktop, address, Win)!.Arguments);

    [Theory]
    [InlineData(OpenWithKind.BrowserHttp)]
    [InlineData(OpenWithKind.BrowserHttps)]
    public void Browser_OnlyEverStartsAnHttpUrl(OpenWithKind kind)
    {
        var c = OpenWithBuilder.Build(kind, "192.168.1.5", Win)!;
        Assert.True(c.FileName.StartsWith("http://") || c.FileName.StartsWith("https://"));
    }

    [Fact]
    public void RemoteDesktop_UsesMstscFromTheWindowsFolderAndNoShell()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.RemoteDesktop, "192.168.1.5", Win)!;
        Assert.Equal(@"C:\Windows\System32\mstsc.exe", c.FileName);
        Assert.False(c.UseShellExecute);
        Assert.Equal(new[] { "/v:192.168.1.5" }, c.Arguments);
    }

    [Fact]
    public void Ssh_UsesTheSystemSshAndTheAddressIsTheOnlyArgument()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.Ssh, "192.168.1.5", Win)!;
        Assert.Equal(@"C:\Windows\System32\OpenSSH\ssh.exe", c.FileName);
        Assert.False(c.UseShellExecute);
        Assert.Equal(new[] { "192.168.1.5" }, c.Arguments);
        Assert.False(c.Arguments[0].StartsWith('-'));
    }

    [Fact]
    public void Shares_OpensTheUncRootInExplorer()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.Shares, "192.168.1.5", Win)!;
        Assert.Equal(@"C:\Windows\explorer.exe", c.FileName);
        Assert.False(c.UseShellExecute);
        Assert.Equal(new[] { @"\\192.168.1.5" }, c.Arguments);
    }

    [Fact]
    public void Shares_Ipv6UsesTheLiteralHostForm()
    {
        var c = OpenWithBuilder.Build(OpenWithKind.Shares, "fe80::1", Win)!;
        Assert.Equal(new[] { @"\\fe80--1.ipv6-literal.net" }, c.Arguments);
        c = OpenWithBuilder.Build(OpenWithKind.Shares, "fe80::1%5", Win)!;
        Assert.Equal(new[] { @"\\fe80--1s5.ipv6-literal.net" }, c.Arguments);
    }

    [Fact]
    public void Shares_Ipv4MappedAddressHasNoEntry()
        => Assert.Null(OpenWithBuilder.Build(OpenWithKind.Shares, "::ffff:192.168.1.5", Win));

    [Theory]
    [InlineData(OpenWithKind.RemoteDesktop)]
    [InlineData(OpenWithKind.Ssh)]
    [InlineData(OpenWithKind.Shares)]
    public void ProgramEntries_NeverUseTheShellAndStartAnAbsolutePath(OpenWithKind kind)
    {
        var c = OpenWithBuilder.Build(kind, "10.1.2.3", Win)!;
        Assert.False(c.UseShellExecute);
        Assert.True(System.IO.Path.IsPathRooted(c.FileName));
        Assert.StartsWith(Win, c.FileName);
        var psi = c.ToStartInfo();
        Assert.Equal(c.Arguments.Count, psi.ArgumentList.Count);
        Assert.True(psi.ArgumentList.SequenceEqual(c.Arguments));
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void ToStartInfo_UsesShellOnlyForUrls()
    {
        var psi = OpenWithBuilder.Build(OpenWithKind.BrowserHttp, "10.1.2.3", Win)!.ToStartInfo();
        Assert.True(psi.UseShellExecute);
        Assert.Equal("http://10.1.2.3/", psi.FileName);
        Assert.Empty(psi.ArgumentList);
    }

    [Fact]
    public void Ipv4AddressesKeepTheirCanonicalText()
    {
        foreach (var kind in Enum.GetValues<OpenWithKind>())
            Assert.NotNull(OpenWithBuilder.Build(kind, "192.168.0.1", Win));
    }

    [Theory]
    [InlineData(OpenWithKind.BrowserHttp, 80)]
    [InlineData(OpenWithKind.BrowserHttps, 443)]
    [InlineData(OpenWithKind.RemoteDesktop, 3389)]
    [InlineData(OpenWithKind.Ssh, 22)]
    [InlineData(OpenWithKind.Shares, 445)]
    public void PortOf_NamesTheServicePort(OpenWithKind kind, int port)
        => Assert.Equal(port, OpenWithBuilder.PortOf(kind));
}
