using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", true)]
    [InlineData("aa-bb-cc-dd-ee-ff", true)]
    [InlineData("AA:BB:CC:DD:EE", false)]           // too short
    [InlineData("AA:BB:CC:DD:EE:FF:00", false)]     // too long
    [InlineData("ZZ:BB:CC:DD:EE:FF", false)]        // non-hex
    [InlineData("AABBCCDDEEFF", false)]             // missing separators
    [InlineData(null, false)]
    public void IsValidMac_AcceptsOnlyStrictColonOrDashHex(string? mac, bool expected)
        => Assert.Equal(expected, WakeOnLan.IsValidMac(mac));

    [Fact]
    public void Send_InvalidMac_NeverOpensASocket()
        => Assert.False(WakeOnLan.Send("not-a-mac"));

    [Fact]
    public async Task SendAsync_InvalidMac_ReturnsFalseWithoutSending()
        => Assert.False(await WakeOnLan.SendAsync("not-a-mac", null));

    [Fact]
    public async Task SendAsync_ValidMac_SendsToGeneralAndDirectedBroadcast()
    {
        bool sent = await WakeOnLan.SendAsync("AA:BB:CC:DD:EE:FF", IPAddress.Parse("192.168.1.255"), repeats: 1);
        Assert.True(sent);
    }

    [Fact]
    public async Task SendAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WakeOnLan.SendAsync("AA:BB:CC:DD:EE:FF", null, repeats: 5, ct: cts.Token));
    }
}
