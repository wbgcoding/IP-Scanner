using System.Globalization;
using System.Runtime.InteropServices;
using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>Reads the IPv6 neighbour cache through iphlpapi GetIpNetTable2. Needs no elevation. Entries
/// that never resolved (incomplete, unreachable) and multicast mappings are left out.</summary>
public sealed class WindowsNeighborTable : INeighborTable
{
    private const ushort AfInet6 = 23;
    private const int TableHeader = 8;          // ULONG NumEntries + padding to the 8-byte aligned rows

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    // Mirrors SOCKADDR_IN6 and MIB_IPNET_ROW2; a test checks size and offsets against the values Parse uses.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SockAddrIn6
    {
        public ushort Family;
        public ushort Port;
        public uint FlowInfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Addr;
        public uint ScopeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        public SockAddrIn6 Address;
        public uint InterfaceIndex;
        public ulong InterfaceLuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        public uint PhysicalAddressLength;
        public uint State;
        public uint Flags;
        public uint ReachabilityTime;
    }

    internal const int RowSize = 88;

    public IReadOnlyList<NeighborEntry> Read()
    {
        IntPtr table = IntPtr.Zero;
        try
        {
            if (GetIpNetTable2(AfInet6, out table) != 0 || table == IntPtr.Zero) return Array.Empty<NeighborEntry>();
            int count = Marshal.ReadInt32(table);
            if (count <= 0 || count > 100_000) return Array.Empty<NeighborEntry>();
            var raw = new byte[TableHeader + count * RowSize];
            Marshal.Copy(table, raw, 0, raw.Length);
            return Parse(raw);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(LogArea.Scan, "IPv6 neighbour table unavailable: " + ex.GetType().Name);
            return Array.Empty<NeighborEntry>();
        }
        finally
        {
            if (table != IntPtr.Zero) FreeMibTable(table);
        }
    }

    /// <summary>Decodes a MIB_IPNET_TABLE2 image (count, padding, rows).</summary>
    internal static IReadOnlyList<NeighborEntry> Parse(ReadOnlySpan<byte> table)
    {
        var list = new List<NeighborEntry>();
        if (table.Length < TableHeader) return list;
        int count = BitConverter.ToInt32(table[..4]);
        for (int i = 0; i < count; i++)
        {
            int at = TableHeader + i * RowSize;
            if (at + RowSize > table.Length) break;
            var row = table.Slice(at, RowSize);
            if (BitConverter.ToUInt16(row[..2]) != AfInet6) continue;

            var state = (NeighborState)BitConverter.ToUInt32(row[76..80]);
            if (state is NeighborState.Incomplete or NeighborState.Unreachable) continue;

            uint scope = BitConverter.ToUInt32(row[24..28]);
            var addr = Ipv6Address.FromBytes(row.Slice(8, 16), scope == 0 ? null : scope.ToString(CultureInfo.InvariantCulture));
            if (addr.IsMulticast) continue;

            int macLen = (int)BitConverter.ToUInt32(row[72..76]);
            string? mac = null;
            if (macLen == 6)
            {
                var m = row.Slice(40, 6);
                // 33:33:xx:xx:xx:xx is the Ethernet mapping of IPv6 multicast, never a host.
                if (m[0] == 0x33 && m[1] == 0x33) continue;
                mac = NetworkDetector.FormatMac(m.ToArray());
            }
            bool router = (row[80] & 1) != 0;
            list.Add(new NeighborEntry(addr, mac, (int)BitConverter.ToUInt32(row[28..32]), state, router));
        }
        return list;
    }
}
