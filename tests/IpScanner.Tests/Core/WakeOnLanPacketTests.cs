using System.Linq;
using System.Reflection;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class WakeOnLanPacketTests
{
    private static byte[] Build(string mac)
        => (byte[])typeof(WakeOnLan).GetMethod("BuildPacket", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { mac })!;

    [Fact]
    public void MagicPacket_Is102Bytes_SixFfThenMacSixteenTimes()
    {
        var p = Build("AA:BB:CC:11:22:33");
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33 };
        for (int i = 0; i < 16; i++)
            Assert.Equal(mac, p.Skip(6 + i * 6).Take(6).ToArray());
    }

    [Theory]
    [InlineData("aa-bb-cc-11-22-33")]
    [InlineData("aa:bb:cc:11:22:33")]
    [InlineData("AA-BB-CC-11-22-33")]
    public void MagicPacket_SeparatorAndCase_DoNotChangeTheBytes(string mac)
        => Assert.Equal(Build("AA:BB:CC:11:22:33"), Build(mac));

    [Theory]
    [InlineData("AA:BB:CC:11:22:3")]
    [InlineData("AA:BB:CC:11:22:33 ")]
    [InlineData("AA:BB-CC:11:22:33x")]
    [InlineData("")]
    [InlineData("GG:BB:CC:11:22:33")]
    public void IsValidMac_RejectsMalformed(string mac)
        => Assert.False(WakeOnLan.IsValidMac(mac));

    [Fact]
    public void Send_NullMac_ReturnsFalse() => Assert.False(WakeOnLan.Send(null));
}
