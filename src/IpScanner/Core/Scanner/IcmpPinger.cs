using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Sub-millisecond ICMP via iphlpapi!IcmpSendEcho (no admin needed),
/// timed with a high-resolution clock. Mirrors the Python windows_icmp_ping.
/// </summary>
public static class IcmpPinger
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho(
        IntPtr handle, uint destIp, byte[] requestData, short requestSize,
        IntPtr requestOptions, byte[] replyBuffer, uint replySize, uint timeoutMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct IcmpEchoReply
    {
        public uint Address;
        public uint Status;
        public uint RoundTripTime;
        public ushort DataSize;
        public ushort Reserved;
        public IntPtr Data;
        public byte OptionsTtl;
        public byte OptionsTos;
        public byte OptionsFlags;
        public byte OptionsOptionsSize;
        public IntPtr OptionsOptionsData;
    }

    private static readonly byte[] Payload =
        System.Text.Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwabcdefghi");

    public static PingResult Ping(string ip, int timeoutMs)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return new PingResult(false, null, null);
        uint dest = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);

        IntPtr handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return new PingResult(false, null, null);
        try
        {
            int replySize = Marshal.SizeOf<IcmpEchoReply>() + Payload.Length + 8;
            var reply = new byte[replySize];
            var sw = Stopwatch.StartNew();
            uint n = IcmpSendEcho(handle, dest, Payload, (short)Payload.Length,
                                  IntPtr.Zero, reply, (uint)replySize, (uint)timeoutMs);
            double elapsed = sw.Elapsed.TotalMilliseconds;
            if (n == 0) return new PingResult(false, null, null);

            var handleGc = GCHandle.Alloc(reply, GCHandleType.Pinned);
            try
            {
                var r = Marshal.PtrToStructure<IcmpEchoReply>(handleGc.AddrOfPinnedObject());
                return r.Status != 0
                    ? new PingResult(false, null, null)
                    : new PingResult(true, elapsed, r.OptionsTtl);
            }
            finally { handleGc.Free(); }
        }
        finally { IcmpCloseHandle(handle); }
    }
}
