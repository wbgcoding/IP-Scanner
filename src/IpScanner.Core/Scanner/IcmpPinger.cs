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

    // The "2Ex" variant adds a source-address parameter so an echo can be
    // sent from a specific local adapter - still the plain iphlpapi ICMP helper
    // (same driver as IcmpSendEcho above), so this needs no elevation either.
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        IntPtr handle, IntPtr ev, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destIp, byte[] requestData, short requestSize,
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

    private static readonly int ReplySize = Marshal.SizeOf<IcmpEchoReply>() + Payload.Length + 8;

    // One handle and reply buffer per pool thread instead of a create/close pair and a fresh
    // buffer for every ping. Tracking all values lets Shutdown reach handles of other threads.
    private sealed class PingState
    {
        public IntPtr Handle;
        public readonly byte[] Reply = new byte[ReplySize];
        public int Busy;
    }

    private static readonly ThreadLocal<PingState> _state = new(() => new PingState(), trackAllValues: true);
    private static volatile bool _shutdown;

    /// <summary>An optional sourceIp binds the echo to a specific local
    /// adapter via IcmpSendEcho2Ex instead of letting Windows pick the route -
    /// null/empty/unparseable falls back to the plain IcmpSendEcho behavior above.</summary>
    public static PingResult Ping(string ip, int timeoutMs, string? sourceIp = null)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return new PingResult(false, null, null);
        uint dest = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
        uint source = !string.IsNullOrEmpty(sourceIp) && IPAddress.TryParse(sourceIp, out var srcAddr)
            ? BitConverter.ToUInt32(srcAddr.GetAddressBytes(), 0) : 0;

        var state = _shutdown ? null : _state.Value;
        if (state is not null && Interlocked.CompareExchange(ref state.Busy, 1, 0) == 0)
        {
            try
            {
                if (state.Handle == IntPtr.Zero) state.Handle = IcmpCreateFile();
                if (IsInvalid(state.Handle)) { state.Handle = IntPtr.Zero; return new PingResult(false, null, null); }
                return Send(state.Handle, state.Reply, dest, source, timeoutMs);
            }
            finally
            {
                Volatile.Write(ref state.Busy, 0);
                if (_shutdown) CloseIfIdle(state);
            }
        }

        // Shutting down, or the pool thread re-entered: one-off handle and buffer.
        IntPtr handle = IcmpCreateFile();
        if (IsInvalid(handle)) return new PingResult(false, null, null);
        try { return Send(handle, new byte[ReplySize], dest, source, timeoutMs); }
        finally { IcmpCloseHandle(handle); }
    }

    private static bool IsInvalid(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1);

    private static PingResult Send(IntPtr handle, byte[] reply, uint dest, uint source, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        uint n = source == 0
            ? IcmpSendEcho(handle, dest, Payload, (short)Payload.Length,
                           IntPtr.Zero, reply, (uint)reply.Length, (uint)timeoutMs)
            : IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, source, dest,
                              Payload, (short)Payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeoutMs);
        double elapsed = sw.Elapsed.TotalMilliseconds;
        if (n == 0) return new PingResult(false, null, null);

        var r = MemoryMarshal.Read<IcmpEchoReply>(reply);
        return r.Status != 0
            ? new PingResult(false, null, null)
            : new PingResult(true, elapsed, r.OptionsTtl);
    }

    private static void CloseIfIdle(PingState state)
    {
        if (Interlocked.CompareExchange(ref state.Busy, 2, 0) != 0) return;
        var handle = state.Handle;
        state.Handle = IntPtr.Zero;
        if (!IsInvalid(handle)) IcmpCloseHandle(handle);
        Volatile.Write(ref state.Busy, 0);
    }

    /// <summary>Closes every pooled ICMP handle (app exit). A ping still running closes its own
    /// handle when it finishes; later pings fall back to one-off handles.</summary>
    public static void Shutdown()
    {
        _shutdown = true;
        foreach (var state in _state.Values) CloseIfIdle(state!);
    }
}
