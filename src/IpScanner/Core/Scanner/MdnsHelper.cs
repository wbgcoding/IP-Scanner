using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IpScanner.Core.Scanner;

/// <summary>
/// mDNS (multicast DNS) reverse lookup — resolves .local hostnames of devices
/// that have no DNS PTR record and no NetBIOS (IoT, Android, Linux, Apple).
/// Sends a PTR query for x.x.x.x.in-addr.arpa with the QU (unicast response)
/// bit to the mDNS group and to the device itself.
/// </summary>
public static class MdnsHelper
{
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    public static string? Resolve(string ip, int timeoutMs = 1500)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        try
        {
            var query = BuildPtrQuery(ip);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Send(query, query.Length, new IPEndPoint(MdnsGroup, MdnsPort));
            udp.Send(query, query.Length, new IPEndPoint(addr, MdnsPort));

            var sw = Stopwatch.StartNew();
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                udp.Client.ReceiveTimeout = Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds);
                byte[] resp;
                try { resp = udp.Receive(ref remote); }
                catch (SocketException) { return null; }    // timed out
                var name = ParsePtrAnswer(resp);
                if (name is not null) return name;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>DNS PTR query for "d.c.b.a.in-addr.arpa" with the QU bit set.</summary>
    internal static byte[] BuildPtrQuery(string ip)
    {
        var o = ip.Split('.');
        var name = $"{o[3]}.{o[2]}.{o[1]}.{o[0]}.in-addr.arpa";
        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0); W16(0); W16(1); W16(0); W16(0); W16(0);     // header: 1 question
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }
        ms.WriteByte(0);
        W16(12);        // QTYPE = PTR
        W16(0x8001);    // QCLASS = IN, QU bit (unicast response requested)
        return ms.ToArray();
    }

    /// <summary>Extract the first PTR answer name; strips a ".local" suffix.</summary>
    internal static string? ParsePtrAnswer(byte[] p)
    {
        try
        {
            if (p.Length < 12) return null;
            int qd = (p[4] << 8) | p[5];
            int an = (p[6] << 8) | p[7];
            if (an == 0) return null;
            int pos = 12;
            for (int i = 0; i < qd; i++) { SkipName(p, ref pos); pos += 4; }
            for (int i = 0; i < an; i++)
            {
                SkipName(p, ref pos);
                int type = (p[pos] << 8) | p[pos + 1];
                pos += 8;                                   // type + class + ttl
                int rdlen = (p[pos] << 8) | p[pos + 1];
                pos += 2;
                if (type == 12)
                {
                    int rp = pos;
                    var name = ReadName(p, ref rp);
                    if (!string.IsNullOrEmpty(name))
                        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                            ? name[..^".local".Length] : name;
                }
                pos += rdlen;
            }
        }
        catch { /* malformed packet */ }
        return null;
    }

    private static void SkipName(byte[] p, ref int pos)
    {
        while (pos < p.Length)
        {
            int len = p[pos];
            if (len == 0) { pos++; return; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return; }  // compression pointer
            pos += len + 1;
        }
    }

    private static string ReadName(byte[] p, ref int pos)
    {
        var sb = new StringBuilder();
        int jumps = 0;
        int cur = pos;
        bool jumped = false;
        while (cur < p.Length)
        {
            int len = p[cur];
            if (len == 0) { if (!jumped) pos = cur + 1; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 8) break;                      // loop guard
                int target = ((len & 0x3F) << 8) | p[cur + 1];
                if (!jumped) pos = cur + 2;
                cur = target;
                jumped = true;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(p, cur + 1, len));
            cur += len + 1;
        }
        return sb.ToString();
    }
}
