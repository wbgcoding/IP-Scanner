using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IpScanner.Core.Scanner;

/// <summary>A set of TCP and UDP ports to probe, parsed from the usual port syntax: single ports,
/// ranges (<c>20-25</c>, <c>-100</c>, <c>8000-</c>), all ports (<c>-</c>), service names
/// (<c>ssh</c>, <c>http*</c>) and <c>T:</c> / <c>U:</c> group prefixes (<c>T:80,443,U:53</c>).
/// A token without a prefix goes to both protocols; the caller keeps the protocols it scans.</summary>
public sealed class PortSpec
{
    public const int MaxPort = 65535;

    public IReadOnlyList<int> Tcp { get; }
    public IReadOnlyList<int> Udp { get; }

    public PortSpec(IEnumerable<int> tcp, IEnumerable<int> udp)
    {
        Tcp = tcp.Distinct().ToList();
        Udp = udp.Distinct().ToList();
    }

    public bool IsEmpty => Tcp.Count == 0 && Udp.Count == 0;

    /// <summary>The same ports with a protocol switched off.</summary>
    public PortSpec Only(bool tcp, bool udp)
        => new(tcp ? Tcp : System.Array.Empty<int>(), udp ? Udp : System.Array.Empty<int>());

    /// <summary>The curated common-services preset: the built-in service table plus the
    /// configured extra TCP ports, and the UDP protocols that have a payload probe.</summary>
    public static PortSpec Common()
    {
        var known = new HashSet<int>(PortScanner.TcpPorts.Select(p => p.Port));
        var tcp = PortScanner.TcpPorts.Select(p => p.Port)
            .Concat(PortScanner.CustomTcpPorts.Select(p => p.Port).Where(p => !known.Contains(p)));
        return new PortSpec(tcp, PortScanner.UdpPorts.Select(p => p.Port));
    }

    /// <summary>The <paramref name="n"/> most common ports of each protocol.</summary>
    public static PortSpec Top(int n)
        => new(PortFrequency.Top(n, NetProtocol.Tcp), PortFrequency.Top(n, NetProtocol.Udp));

    public static PortSpec Range(int first, int last)
    {
        var ports = Enumerable.Range(first, last - first + 1).ToList();
        return new PortSpec(ports, ports);
    }

    public static PortSpec All() => Range(1, MaxPort);

    /// <summary>Parses <paramref name="text"/>; false when any token is invalid or nothing is selected.</summary>
    public static bool TryParse(string? text, out PortSpec spec)
    {
        spec = new PortSpec(System.Array.Empty<int>(), System.Array.Empty<int>());
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tcp = new List<int>();
        var udp = new List<int>();
        bool toTcp = true, toUdp = true;
        foreach (var raw in text.Split(',', System.StringSplitOptions.TrimEntries))
        {
            var token = raw;
            if (token.Length == 0) return false;
            if (token.Length >= 2 && token[1] == ':' && char.ToUpperInvariant(token[0]) is 'T' or 'U')
            {
                bool t = char.ToUpperInvariant(token[0]) == 'T';
                toTcp = t; toUdp = !t;
                token = token[2..].Trim();
                if (token.Length == 0) continue;
            }
            if (!TryParseToken(token, out var ports)) return false;
            if (toTcp) tcp.AddRange(ports.Where(p => p.Tcp).Select(p => p.Port));
            if (toUdp) udp.AddRange(ports.Where(p => p.Udp).Select(p => p.Port));
        }
        spec = new PortSpec(tcp.OrderBy(p => p), udp.OrderBy(p => p));
        return !spec.IsEmpty;
    }

    private static bool TryParseToken(string token, out List<(int Port, bool Tcp, bool Udp)> ports)
    {
        var result = new List<(int Port, bool Tcp, bool Udp)>();
        ports = result;
        if (token == "-") { AddRange(1, MaxPort); return true; }
        int dash = token.IndexOf('-');
        if (dash >= 0 && token.All(c => char.IsDigit(c) || c == '-'))
        {
            var lo = token[..dash];
            var hi = token[(dash + 1)..];
            int first = lo.Length == 0 ? 1 : int.TryParse(lo, out var a) ? a : -1;
            int last = hi.Length == 0 ? MaxPort : int.TryParse(hi, out var b) ? b : -1;
            if (first < 1 || last > MaxPort || first > last) return false;
            AddRange(first, last);
            return true;
        }
        if (token.All(char.IsDigit))
        {
            if (!int.TryParse(token, out var port) || port is < 1 or > MaxPort) return false;
            result.Add((port, true, true));
            return true;
        }
        return TryParseServiceName(token, result);

        void AddRange(int first, int last)
        {
            for (int p = first; p <= last; p++) result.Add((p, true, true));
        }
    }

    private static bool TryParseServiceName(string name, List<(int Port, bool Tcp, bool Udp)> ports)
    {
        bool prefix = name.EndsWith('*');
        var key = prefix ? name[..^1] : name;
        if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
        bool Matches(string n) => prefix ? n.StartsWith(key, System.StringComparison.OrdinalIgnoreCase)
                                         : n.Equals(key, System.StringComparison.OrdinalIgnoreCase);
        foreach (var (port, n) in PortScanner.TcpPorts) if (Matches(n)) ports.Add((port, true, false));
        foreach (var (port, n) in PortScanner.UdpPorts) if (Matches(n)) ports.Add((port, false, true));
        return ports.Count > 0;
    }

    /// <summary>The display name the built-in table knows for a port, or an empty string.</summary>
    public static string ServiceNameFor(int port, NetProtocol protocol)
    {
        var table = protocol == NetProtocol.Tcp ? PortScanner.TcpPorts : PortScanner.UdpPorts;
        foreach (var (p, name) in table) if (p == port) return name;
        if (protocol == NetProtocol.Tcp)
            foreach (var (p, name) in PortScanner.CustomTcpPorts) if (p == port) return name;
        return "";
    }
}

/// <summary>The embedded commonness order behind "top N ports".</summary>
public static class PortFrequency
{
    private static readonly System.Lazy<(int[] Tcp, int[] Udp)> Table = new(Load);

    /// <summary>The <paramref name="n"/> most common ports. Past the end of the curated list the
    /// remaining ports follow in ascending order, so any N up to 65535 is satisfiable.</summary>
    public static IReadOnlyList<int> Top(int n, NetProtocol protocol)
    {
        n = System.Math.Clamp(n, 0, PortSpec.MaxPort);
        var listed = protocol == NetProtocol.Tcp ? Table.Value.Tcp : Table.Value.Udp;
        if (n <= listed.Length) return listed.Take(n).ToArray();
        var seen = new HashSet<int>(listed);
        var result = new List<int>(listed);
        for (int p = 1; p <= PortSpec.MaxPort && result.Count < n; p++)
            if (seen.Add(p)) result.Add(p);
        return result;
    }

    internal static (int[] Tcp, int[] Udp) Parse(TextReader reader)
    {
        var tcp = new List<int>();
        var udp = new List<int>();
        var current = tcp;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Equals("[tcp]", System.StringComparison.OrdinalIgnoreCase)) { current = tcp; continue; }
            if (line.Equals("[udp]", System.StringComparison.OrdinalIgnoreCase)) { current = udp; continue; }
            if (int.TryParse(line, out var port) && port is >= 1 and <= PortSpec.MaxPort && !current.Contains(port)) current.Add(port);
        }
        return (tcp.ToArray(), udp.ToArray());
    }

    private static (int[] Tcp, int[] Udp) Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("port-frequency.txt"));
            if (name is null) return (System.Array.Empty<int>(), System.Array.Empty<int>());
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            return Parse(reader);
        }
        catch (System.Exception ex)
        {
            Log.Warn(LogArea.Ports, "Port frequency table unreadable", ex);
            return (System.Array.Empty<int>(), System.Array.Empty<int>());
        }
    }
}
