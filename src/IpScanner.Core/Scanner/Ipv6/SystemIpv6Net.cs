using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>ICMPv6 echo through iphlpapi Icmp6SendEcho2 (no elevation needed).</summary>
public sealed class SystemIpv6Net : IIpv6Net
{
    private const ushort AfInet6 = 23;
    private const int SockAddrSize = 28;
    // ICMPV6_ECHO_REPLY: IPV6_ADDRESS_EX (26 bytes, packed), 2 bytes padding, Status at 28, RoundTripTime at 32.
    internal const int ReplySize = 36;
    private static readonly byte[] Payload = System.Text.Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwabcdefghi");

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr Icmp6CreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint Icmp6SendEcho2(
        IntPtr handle, IntPtr ev, IntPtr apcRoutine, IntPtr apcContext,
        byte[] source, byte[] destination, byte[] requestData, short requestSize,
        IntPtr requestOptions, byte[] replyBuffer, uint replySize, uint timeoutMs);

    public Task<IReadOnlyList<Ipv6EchoReply>> EchoAsync(Ipv6Address target, int timeoutMs, CancellationToken ct)
        => Task.Run(() => Echo(target, timeoutMs), ct);

    private static IReadOnlyList<Ipv6EchoReply> Echo(Ipv6Address target, int timeoutMs)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Icmp6CreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return Array.Empty<Ipv6EchoReply>();
            var source = SockAddr(Ipv6Address.Unspecified);
            var dest = SockAddr(target);
            var reply = new byte[ReplySize * 8 + Payload.Length + 8];
            var sw = Stopwatch.StartNew();
            uint n = Icmp6SendEcho2(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, source, dest,
                Payload, (short)Payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)Math.Max(1, timeoutMs));
            return n == 0 ? Array.Empty<Ipv6EchoReply>() : ParseReplies(reply, (int)n, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(LogArea.Scan, "ICMPv6 echo unavailable: " + ex.GetType().Name);
            return Array.Empty<Ipv6EchoReply>();
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) IcmpCloseHandle(handle);
        }
    }

    /// <summary>A SOCKADDR_IN6 image: family, port 0, flow 0, the address bytes and the scope id.</summary>
    internal static byte[] SockAddr(Ipv6Address a)
    {
        var b = new byte[SockAddrSize];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), AfInet6);
        a.GetBytes().CopyTo(b, 8);
        if (a.Zone is not null && uint.TryParse(a.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out var scope))
            BitConverter.TryWriteBytes(b.AsSpan(24, 4), scope);
        return b;
    }

    /// <summary>Decodes up to <paramref name="count"/> ICMPV6_ECHO_REPLY records; only successful ones count.</summary>
    internal static IReadOnlyList<Ipv6EchoReply> ParseReplies(byte[] buffer, int count, double fallbackRttMs)
    {
        var list = new List<Ipv6EchoReply>();
        for (int i = 0; i < count; i++)
        {
            int at = i * ReplySize;
            if (at + ReplySize > buffer.Length) break;
            var rec = buffer.AsSpan(at, ReplySize);
            if (BitConverter.ToUInt32(rec[28..32]) != 0) continue;
            uint scope = BitConverter.ToUInt32(rec[22..26]);
            var from = Ipv6Address.FromBytes(rec.Slice(6, 16), scope == 0 ? null : scope.ToString(CultureInfo.InvariantCulture));
            uint rtt = BitConverter.ToUInt32(rec[32..36]);
            list.Add(new Ipv6EchoReply(from, rtt == 0 ? fallbackRttMs : rtt));
        }
        return list;
    }
}
