using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IpScanner.Core.Scanner;

/// <summary>One open capture handle: send a prepared Ethernet frame, read the next captured one.</summary>
public interface IRawLink : IDisposable
{
    bool Send(byte[] frame);

    /// <summary>Next captured frame, or null when nothing arrived within the timeout.</summary>
    byte[]? Receive(int timeoutMs);
}

/// <summary>Where a raw probe to one target leaves the machine.</summary>
public sealed record RawRoute(string DeviceName, byte[] SrcMac, IPAddress SrcIp, byte[] NextHopMac);

/// <summary>Direct binding of the few wpcap entry points the app needs (open live, set filter, send, next packet).
/// The library is loaded by absolute path from the driver folder, never bundled; no wrapper package is used.</summary>
public static class Wpcap
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BpfProgram { public uint Len; public IntPtr Insns; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenLiveFn([MarshalAs(UnmanagedType.LPStr)] string device, int snapLen, int promisc, int toMs, IntPtr errBuf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompileFn(IntPtr p, ref BpfProgram prog, [MarshalAs(UnmanagedType.LPStr)] string filter, int optimize, uint netmask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetFilterFn(IntPtr p, ref BpfProgram prog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCodeFn(ref BpfProgram prog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SendPacketFn(IntPtr p, byte[] buf, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NextExFn(IntPtr p, out IntPtr header, out IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseFn(IntPtr p);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetMinToCopyFn(IntPtr p, int size);

    private sealed record Api(OpenLiveFn Open, CompileFn Compile, SetFilterFn SetFilter, FreeCodeFn FreeCode,
        SendPacketFn Send, NextExFn Next, CloseFn Close, SetMinToCopyFn? SetMinToCopy);

    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    // The load directory and System32 only: no cwd, no exe folder, no PATH, and no process-wide search state.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryExW")]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    private static readonly object Gate = new();
    private static Api? _api;
    private static string? _loadError;

    /// <summary>Forgets a failed library load so the next open tries again, used after the driver was installed.</summary>
    internal static void ResetLoadFailure()
    {
        lock (Gate) _loadError = null;
    }

    /// <summary>Test seam: replaces the real driver link, null restores it.</summary>
    internal static Func<string, string?, IRawLink?>? OpenOverride { get; set; }

    internal static string DriverDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");

    /// <summary>True for an existing file that sits directly in <paramref name="driverDirectory"/> once the path is
    /// fully resolved, so a relative or traversing name cannot point the loader elsewhere.</summary>
    internal static bool IsTrustedDriverFile(string path, string driverDirectory)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(driverDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static Api? Load()
    {
        lock (Gate)
        {
            if (_api != null || _loadError != null) return _api;
            try
            {
                var path = Path.Combine(DriverDirectory, "wpcap.dll");
                if (!IsTrustedDriverFile(path, DriverDirectory)) throw new FileNotFoundException("wpcap.dll is not in the driver folder.");
                var lib = LoadLibraryEx(path, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
                if (lib == IntPtr.Zero) throw new DllNotFoundException("wpcap.dll could not be loaded (" + Marshal.GetLastWin32Error() + ").");
                T Fn<T>(string name) where T : Delegate
                    => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
                _api = new Api(Fn<OpenLiveFn>("pcap_open_live"), Fn<CompileFn>("pcap_compile"), Fn<SetFilterFn>("pcap_setfilter"),
                    Fn<FreeCodeFn>("pcap_freecode"), Fn<SendPacketFn>("pcap_sendpacket"), Fn<NextExFn>("pcap_next_ex"),
                    Fn<CloseFn>("pcap_close"),
                    NativeLibrary.TryGetExport(lib, "pcap_setmintocopy", out var minToCopy)
                        ? Marshal.GetDelegateForFunctionPointer<SetMinToCopyFn>(minToCopy) : null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or ArgumentException or FileNotFoundException)
            {
                _loadError = ex.Message;
            }
            return _api;
        }
    }

    /// <summary>Opens a capture handle on <paramref name="deviceName"/> with an optional BPF filter. Null when the
    /// driver is missing or the open fails.</summary>
    public static IRawLink? Open(string deviceName, string? bpfFilter = null)
    {
        if (OpenOverride != null) return OpenOverride(deviceName, bpfFilter);
        if (!PacketDriver.Available) return null;
        var api = Load();
        if (api == null) return null;

        var err = Marshal.AllocHGlobal(256);
        try
        {
            var handle = api.Open(deviceName, 2048, 0, 50, err);
            if (handle == IntPtr.Zero) return null;
            // Without this the driver holds a reply back until the 50 ms read timeout ends, which flattens every
            // measured round trip to about 50 ms.
            api.SetMinToCopy?.Invoke(handle, 0);
            if (bpfFilter != null)
            {
                var prog = new BpfProgram();
                if (api.Compile(handle, ref prog, bpfFilter, 1, 0xFFFFFF00) != 0)
                {
                    api.Close(handle);
                    return null;
                }
                bool set;
                try { set = api.SetFilter(handle, ref prog) == 0; }
                finally { api.FreeCode(ref prog); }
                if (!set)
                {
                    api.Close(handle);
                    return null;
                }
            }
            return new Link(api, handle);
        }
        finally { Marshal.FreeHGlobal(err); }
    }

    private sealed class Link : IRawLink
    {
        private readonly Api _api;
        private IntPtr _handle;
        private readonly object _sendLock = new();
        private readonly object _recvLock = new();

        public Link(Api api, IntPtr handle) { _api = api; _handle = handle; }

        public bool Send(byte[] frame)
        {
            lock (_sendLock) return _handle != IntPtr.Zero && _api.Send(_handle, frame, frame.Length) == 0;
        }

        public byte[]? Receive(int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            do
            {
                lock (_recvLock)
                {
                    if (_handle == IntPtr.Zero) return null;
                    int rc = _api.Next(_handle, out var hdr, out var data);
                    if (rc < 0) return null;
                    if (rc == 1)
                    {
                        int capLen = Marshal.ReadInt32(hdr, 8);
                        if (capLen <= 0 || capLen > 65535) return null;
                        var buf = new byte[capLen];
                        Marshal.Copy(data, buf, 0, capLen);
                        return buf;
                    }
                }
            } while (Environment.TickCount64 < deadline);
            return null;
        }

        public void Dispose()
        {
            lock (_recvLock) lock (_sendLock)
            {
                if (_handle == IntPtr.Zero) return;
                _api.Close(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>Picks the interface, source address and next-hop MAC for a probe to <paramref name="target"/>.</summary>
    public static RawRoute? RouteTo(IPAddress target)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                bool onLink = SameSubnet(ua.Address, target, ua.IPv4Mask);
                IPAddress? hop = onLink ? target
                    : props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (!onLink && hop == null) continue;
                if (!onLink && !IsDefaultRouteNic(nic)) continue;
                var mac = ArpHelper.Resolve(hop!.ToString());
                var hopMac = ParseMac(mac);
                var srcMac = nic.GetPhysicalAddress().GetAddressBytes();
                if (hopMac == null || srcMac.Length != 6) continue;
                return new RawRoute(@"\Device\NPF_" + nic.Id, srcMac, ua.Address, hopMac);
            }
        }
        return null;
    }

    private static bool IsDefaultRouteNic(NetworkInterface nic)
        => nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

    internal static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    internal static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var parts = mac.Split('-', ':');
        if (parts.Length != 6) return null;
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i])) return null;
        return bytes;
    }
}
