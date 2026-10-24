using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IpScanner.Core.Scanner;

/// <summary>Sends a Wake-on-LAN magic packet to a MAC address (UDP broadcast).</summary>
public static class WakeOnLan
{
    private static readonly Regex MacPattern = new(
        "^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    /// <summary>A strict check instead of leaving malformed input to a
    /// broad try/catch around Convert.ToByte - an invalid MAC never opens a socket.</summary>
    public static bool IsValidMac(string? mac) => mac is not null && MacPattern.IsMatch(mac);

    private static byte[] BuildPacket(string mac)
    {
        var m = mac.Split(':', '-').Select(h => Convert.ToByte(h, 16)).ToArray();
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 6; i < packet.Length; i += 6) Array.Copy(m, 0, packet, i, 6);
        return packet;
    }

    /// <summary>Single broadcast packet on port 9 - kept for the existing caller.
    /// New code should use <see cref="SendAsync"/> instead: one packet on
    /// one port is routinely dropped and never reaches a different subnet.</summary>
    public static bool Send(string? mac)
    {
        if (!IsValidMac(mac)) return false;
        try
        {
            var packet = BuildPacket(mac!);
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            return true;
        }
        catch (Exception ex) { Log.Warn(LogArea.Tools, "Building the magic packet failed", ex); return false; }
    }

    /// <summary>Sends to the card's own directed broadcast (reaches a
    /// different subnet through a router that allows it) and to the general
    /// broadcast, on both traditional WoL ports (7 and 9), repeated for
    /// reliability - a single packet on one port is routinely dropped.</summary>
    public static async Task<bool> SendAsync(string mac, IPAddress? directedBroadcast,
        int repeats = 3, CancellationToken ct = default)
    {
        if (!IsValidMac(mac)) return false;
        byte[] packet;
        try { packet = BuildPacket(mac); }
        catch (Exception ex) { Log.Warn(LogArea.Tools, "Listing broadcast targets failed", ex); return false; }

        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (directedBroadcast is not null && !targets.Contains(directedBroadcast))
            targets.Add(directedBroadcast);
        int[] ports = { 7, 9 };

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            for (int i = 0; i < repeats; i++)
            {
                foreach (var target in targets)
                    foreach (var port in ports)
                    {
                        ct.ThrowIfCancellationRequested();
                        await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, port)).ConfigureAwait(false);
                    }
                if (i < repeats - 1) await Task.Delay(150, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn(LogArea.Tools, "Sending the magic packet failed", ex); return false; }
    }
}
