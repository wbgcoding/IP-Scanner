namespace IpScanner.Core.Scanner.Discovery;

public enum DiscoveryMode { Arp, IcmpEcho, IcmpTimestamp, IcmpMask, TcpConnect, TcpSyn, TcpAck, Udp, ListScan, SkipDiscovery }

/// <summary>Config string helpers for the selected discovery modes and probe ports.</summary>
public static class DiscoveryModes
{
    public const string DefaultModes = "icmp";
    public const string DefaultTcpPorts = "80,443,22,445";
    public const string DefaultUdpPorts = "53,161,137";
    public const int MaxPorts = 16;

    private static readonly (DiscoveryMode Mode, string Key, Capability Cap)[] Table =
    {
        (DiscoveryMode.Arp, "arp", Capability.ArpSweep),
        (DiscoveryMode.IcmpEcho, "icmp", Capability.IcmpEcho),
        (DiscoveryMode.IcmpTimestamp, "icmp-ts", Capability.IcmpTimestamp),
        (DiscoveryMode.IcmpMask, "icmp-mask", Capability.IcmpAddressMask),
        (DiscoveryMode.TcpConnect, "tcp", Capability.TcpConnectPing),
        (DiscoveryMode.TcpSyn, "tcp-syn", Capability.TcpSynPing),
        (DiscoveryMode.TcpAck, "tcp-ack", Capability.TcpAckPing),
        (DiscoveryMode.Udp, "udp", Capability.UdpPing),
        (DiscoveryMode.ListScan, "list", Capability.ListScan),
        (DiscoveryMode.SkipDiscovery, "skip", Capability.SkipDiscovery),
    };

    public static IReadOnlyList<DiscoveryMode> All { get; } = Table.Select(t => t.Mode).ToArray();

    public static string KeyOf(DiscoveryMode mode) => Table.First(t => t.Mode == mode).Key;

    public static Capability CapabilityOf(DiscoveryMode mode) => Table.First(t => t.Mode == mode).Cap;

    /// <summary>Parses a comma separated mode list; unknown tokens are dropped, an empty result is the default.
    /// A list scan sends nothing, so it excludes every other mode.</summary>
    public static IReadOnlyList<DiscoveryMode> Parse(string? text)
    {
        var set = new HashSet<DiscoveryMode>();
        foreach (var tok in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var t in Table)
                if (string.Equals(t.Key, tok, StringComparison.OrdinalIgnoreCase)) set.Add(t.Mode);
        if (set.Count == 0) set.Add(DiscoveryMode.IcmpEcho);
        if (set.Contains(DiscoveryMode.ListScan)) return new[] { DiscoveryMode.ListScan };
        return All.Where(set.Contains).ToArray();
    }

    public static string Format(IEnumerable<DiscoveryMode> modes)
        => string.Join(",", All.Where(new HashSet<DiscoveryMode>(modes).Contains).Select(KeyOf));

    /// <summary>The configured modes with unavailable ones (missing driver or rights) removed; never empty.</summary>
    public static IReadOnlyList<DiscoveryMode> Usable(IEnumerable<DiscoveryMode> modes)
    {
        var list = modes.Where(m => Capabilities.Evaluate(CapabilityOf(m)).Available).ToArray();
        return list.Length > 0 ? list : new[] { DiscoveryMode.IcmpEcho };
    }

    /// <summary>Parses a port list: values 1..65535, no duplicates, at most <see cref="MaxPorts"/>; falls back to
    /// <paramref name="fallback"/> when nothing valid remains.</summary>
    public static int[] ParsePorts(string? text, string fallback)
    {
        var ports = new List<int>();
        foreach (var tok in (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok, out int p) && p is >= 1 and <= 65535 && !ports.Contains(p) && ports.Count < MaxPorts) ports.Add(p);
        return ports.Count > 0 ? ports.ToArray() : fallback == "" ? Array.Empty<int>() : ParsePorts(fallback, "");
    }

    public static string NormalizePorts(string? text, string fallback) => string.Join(",", ParsePorts(text, fallback));

    /// <summary>Readable form of a method code ("tcp:445" becomes "TCP 445").</summary>
    public static string Display(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        int colon = code.IndexOf(':');
        string key = colon < 0 ? code : code[..colon];
        string port = colon < 0 ? "" : " " + code[(colon + 1)..];
        return key switch
        {
            "icmp" => "ICMP",
            "arp" => "ARP",
            "icmp-ts" => Core.Localization.Loc.CapNameIcmpTimestamp,
            "icmp-mask" => Core.Localization.Loc.CapNameIcmpAddressMask,
            "tcp" => "TCP" + port,
            "tcp-syn" => "SYN" + port,
            "tcp-ack" => "ACK" + port,
            "udp" => "UDP" + port,
            "list" => Core.Localization.Loc.CapNameListScan,
            "skip" => Core.Localization.Loc.CapNameSkipDiscovery,
            _ => code,
        };
    }
}
