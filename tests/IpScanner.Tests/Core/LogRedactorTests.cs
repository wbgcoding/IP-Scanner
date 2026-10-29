using System;
using IpScanner.Core;
using Xunit;

namespace IpScanner.Tests.Core;

public class LogRedactorTests
{
    [Theory]
    [InlineData("No such host is known: nas-home.fritz.box", "No such host is known: <host>.box")]
    [InlineData("lookup of Printer-2.office.lan failed", "lookup of <host>.lan failed")]
    [InlineData("ping tv.home.arpa timed out", "ping <host>.home.arpa timed out")]
    [InlineData("No such host is known. (nas-home:443)", "No such host is known. (<host>:443)")]
    public void DeviceHostNames_AreRedacted_KeepingTheSuffixOrPort(string text, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(text, null, null));

    [Theory]
    [InlineData("System.Net.Sockets.SocketException: No route to host")]
    [InlineData("at IpScanner.Core.Scanner.IcmpPinger.PingAsync in IP-Scanner.exe")]
    [InlineData("GET https://api.github.com/repos/x/y/releases/latest failed")]
    [InlineData("Build 1.2.3 (Main:12 is not a host)")]
    [InlineData("tcp 10.0.0.5:443")]
    public void TypeNamesFilesAndPublicHosts_AreLeftAlone(string text) =>
        Assert.Equal(text.Replace("10.0.0.5", "x.x.x.5"), LogRedactor.Redact(text, null, null));

    [Fact]
    public void UserProfilePath_IsReplaced()
    {
        var text = @"Access to C:\Users\alice\AppData\x.dat denied";
        Assert.Equal(@"Access to %USERPROFILE%\AppData\x.dat denied",
            LogRedactor.Redact(text, @"C:\Users\alice", "alice"));
    }

    [Fact]
    public void UserName_IsReplaced_AsAWholeWordOnly()
    {
        Assert.Equal("owner <user> and alicewonderland", LogRedactor.Redact("owner alice and alicewonderland", @"C:\Users\alice", "alice"));
    }

    [Fact]
    public void Ipv4_KeepsOnlyTheLastOctet()
    {
        Assert.Equal("connect to x.x.x.20 failed", LogRedactor.Redact("connect to 192.168.1.20 failed", null, null));
        Assert.Equal("a x.x.x.1, b x.x.x.254", LogRedactor.Redact("a 10.0.0.1, b 172.16.5.254", null, null));
    }

    [Fact]
    public void Loopback_StaysAsIs()
        => Assert.Equal("bound 127.0.0.1:161", LogRedactor.Redact("bound 127.0.0.1:161", null, null));

    [Fact]
    public void VersionNumbersAndOutOfRangeGroups_StayAsIs()
        => Assert.Equal("build 1.2.3.4567 and 300.1.1.1", LogRedactor.Redact("build 1.2.3.4567 and 300.1.1.1", null, null));

    [Fact]
    public void RealProfileAndUserName_AreHiddenInTheDefaultOverload()
    {
        var text = $"path {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\f by {Environment.UserName}";
        var redacted = LogRedactor.Redact(text);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), redacted);
        Assert.Contains("%USERPROFILE%", redacted);
    }

    [Fact]
    public void EmptyText_IsReturned()
        => Assert.Equal("", LogRedactor.Redact(""));

    [Fact]
    public void ShortProfilePath_IsReplaced()
        => Assert.Equal(@"copy %USERPROFILE%\AppData\Local\Temp\x failed",
            LogRedactor.Redact(@"copy C:\Users\JOHANN~1\AppData\Local\Temp\x failed", @"C:\Users\Johannes", "Johannes", @"C:\Users\JOHANN~1"));

    [Fact]
    public void MachineName_IsReplaced_AsAWholeWordOnly()
    {
        Assert.Equal("reached <host> but not <host>.lan2 or LABPCX",
            LogRedactor.Redact("reached LABPC but not labpc.lan2 or LABPCX", null, "someone", null, "LABPC"));
    }

    [Fact]
    public void MacAddress_KeepsOnlyTheLastByte()
    {
        Assert.Equal("arp xx:xx:xx:xx:xx:5F and xx:xx:xx:xx:xx:11",
            LogRedactor.Redact("arp aa:bb:cc:dd:ee:5F and AA-BB-CC-DD-EE-11", null, null));
    }

    [Theory]
    [InlineData("peer fe80::1ff:fe23:4567:890a failed", "peer <ipv6> failed")]
    [InlineData("peer 2001:db8:85a3:0:0:8a2e:370:7334 failed", "peer <ipv6> failed")]
    [InlineData("bound ::1 ok", "bound ::1 ok")]
    [InlineData("at 12:34:56 done", "at 12:34:56 done")]
    public void Ipv6_IsHidden_ButTimesAndLoopbackStay(string input, string expected)
        => Assert.Equal(expected, LogRedactor.Redact(input, null, null));
}
