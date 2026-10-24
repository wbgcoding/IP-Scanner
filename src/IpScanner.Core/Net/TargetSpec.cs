using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using IpScanner.Core.Localization;

namespace IpScanner.Core.Net;

/// <summary>One token of a target specification that could not be parsed, or a file that could not be read.</summary>
public readonly record struct TargetSpecError(string Token, string Reason);

/// <summary>Thrown when a spec larger than <see cref="TargetSpec.HardCap"/> addresses is expanded without confirmation.</summary>
public sealed class TargetCapExceededException : InvalidOperationException
{
    public long Estimated { get; }

    public TargetCapExceededException(long estimated)
        : base($"The target specification covers about {estimated} addresses, more than the cap of {TargetSpec.HardCap}.")
        => Estimated = estimated;
}

public sealed class TargetSpecOptions
{
    /// <summary>Exclusions in the same syntax as the targets.</summary>
    public string? Exclude { get; init; }
    /// <summary>Path of a target file (one entry per line, '#' starts a comment) appended to the targets.</summary>
    public string? IncludeFile { get; init; }
    /// <summary>Path of an exclusion file in the same format.</summary>
    public string? ExcludeFile { get; init; }
    /// <summary>Maps a hostname to its addresses (dotted or IPv6 text); defaults to DNS. Called at expansion time.
    /// Only the addresses of <see cref="HostFamily"/> are used.</summary>
    public Func<string, IReadOnlyList<string>>? Resolver { get; init; }
    /// <summary>The address family hostnames resolve to (IPv4 unless a scan asks for IPv6).</summary>
    public IpFamily HostFamily { get; init; } = IpFamily.V4;
    /// <summary>Reads the lines of a file; defaults to <see cref="File.ReadLines(string)"/>.</summary>
    public Func<string, IEnumerable<string>>? ReadLines { get; init; }
}

/// <summary>
/// A scan target specification: CIDR (<c>10.0.0.0/24</c>), address ranges (<c>10.0.0.5-10.0.1.20</c>),
/// per-octet ranges (<c>192.168.1-3.1-254</c>, <c>10.0.0.*</c>), single addresses, hostnames (resolved when
/// the spec is expanded), comma/whitespace separated lists, target files and exclusions. Exclusions always win.
/// IPv6 works the same way for literals (<c>2001:db8::1</c>, <c>fe80::1%12</c> with a zone), prefixes
/// (<c>2001:db8::/112</c>) and ranges (<c>2001:db8::1-2001:db8::ff</c>); an IPv6 entry that holds more than
/// <see cref="HardCap"/> addresses is refused, an IPv6 exclusion may be any size. Both families can share one list.
/// Expansion is lazy: a /8 yields its first addresses without allocating anything proportional to its size.
/// CIDR blocks cover the scannable hosts (no network/broadcast address for /30 and wider, like the sidebar subnets);
/// an excluded CIDR block drops every address of the block.
/// </summary>
public sealed class TargetSpec
{
    /// <summary>Addresses above this need an explicit confirmation before they are expanded.</summary>
    public const int HardCap = 65536;

    private readonly List<Item> _includes = new();
    private readonly List<Item> _excludes = new();
    private readonly List<TargetSpecError> _errors = new();
    private readonly List<string> _unresolved = new();
    private readonly Func<string, IReadOnlyList<string>> _resolver;
    private readonly IpFamily _hostFamily;

    private TargetSpec(Func<string, IReadOnlyList<string>>? resolver, IpFamily hostFamily = IpFamily.V4)
    {
        _resolver = resolver ?? DefaultResolver;
        _hostFamily = hostFamily;
    }

    /// <summary>Tokens that were malformed (skipped) or files that could not be read. Check before expanding.</summary>
    public IReadOnlyList<TargetSpecError> Errors => _errors;
    public bool IsValid => _errors.Count == 0;
    public bool HasTargets => _includes.Count > 0;

    /// <summary>True when an include entry is a literal IPv4 address, range or block (hostnames do not count).</summary>
    public bool HasIpv4Targets => _includes.Any(i => i is not HostItem && !i.V6);

    /// <summary>True when an include entry is a literal IPv6 address, range or prefix.</summary>
    public bool HasIpv6Targets => _includes.Any(i => i is not HostItem && i.V6);

    /// <summary>Hostnames of the last expansion that resolved to no address of the requested family.</summary>
    public IReadOnlyList<string> UnresolvedHosts => _unresolved;

    /// <summary>Upper bound of the address count: the sum of every include entry, ignoring overlap and
    /// exclusions; a hostname counts as one address.</summary>
    public long EstimatedCount => _includes.Aggregate(0L, (sum, i) => sum > long.MaxValue - i.Count ? long.MaxValue : sum + i.Count);

    public bool RequiresConfirmation => EstimatedCount > HardCap;

    public static TargetSpec Parse(string? targets, TargetSpecOptions? options = null)
    {
        var spec = new TargetSpec(options?.Resolver, options?.HostFamily ?? IpFamily.V4);
        Func<string, IEnumerable<string>> readLines = options?.ReadLines ?? (path => File.ReadLines(path));
        spec.AddLines(SplitLines(targets), include: true);
        if (!string.IsNullOrWhiteSpace(options?.IncludeFile)) spec.AddFile(options!.IncludeFile!, true, readLines);
        spec.AddLines(SplitLines(options?.Exclude), include: false);
        if (!string.IsNullOrWhiteSpace(options?.ExcludeFile)) spec.AddFile(options!.ExcludeFile!, false, readLines);
        return spec;
    }

    /// <summary>The prefix form the sidebar writes: every <c>.1-.254</c> host of each /24 prefix (e.g.
    /// "192.168.1"), or only the addresses inside <paramref name="ranges"/> when given. Prefix order is kept.</summary>
    public static TargetSpec FromPrefixes(IEnumerable<string> prefixes,
                                          IReadOnlyList<(uint First, uint Last)>? ranges = null)
    {
        var spec = new TargetSpec(null);
        var intervals = new List<(uint First, uint Last)>();
        var seen = new HashSet<uint>();
        foreach (var prefix in prefixes)
        {
            if (!Ipv4.TryParseUInt(prefix + ".0", out var b) || !seen.Add(b)) continue;
            if (ranges is null) { intervals.Add((b + Ipv4.FirstHost, b + Ipv4.LastHost)); continue; }
            var clipped = new List<(uint First, uint Last)>();
            foreach (var (first, last) in ranges)
            {
                uint lo = Math.Max(first, b), hi = Math.Min(last, b + 255);
                if (lo <= hi) clipped.Add((lo, hi));
            }
            clipped.Sort();
            var merged = new List<(uint First, uint Last)>();
            foreach (var iv in clipped)
            {
                if (merged.Count > 0 && iv.First <= (long)merged[^1].Last + 1)
                    merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, iv.Last));
                else merged.Add(iv);
            }
            intervals.AddRange(merged);
        }
        if (intervals.Count > 0) spec._includes.Add(new IntervalsItem(intervals.Select(i => ((UInt128)i.First, (UInt128)i.Last)).ToList(), false, null));
        return spec;
    }

    /// <summary>Lazily yields the unique, non-excluded addresses in the order they were specified.
    /// Throws <see cref="TargetCapExceededException"/> above <see cref="HardCap"/> unless confirmed.</summary>
    public IEnumerable<string> Expand(bool confirmedOverCap = false)
    {
        long estimate = EstimatedCount;
        if (estimate > HardCap && !confirmedOverCap) throw new TargetCapExceededException(estimate);
        return Enumerate(confirmedOverCap);
    }

    /// <summary>Like <see cref="Expand(bool)"/>; above the cap, <paramref name="confirmOverCap"/> receives the
    /// estimate and decides.</summary>
    public IEnumerable<string> Expand(Func<long, bool> confirmOverCap)
    {
        long estimate = EstimatedCount;
        bool confirmed = estimate > HardCap && confirmOverCap(estimate);
        return Expand(confirmed);
    }

    private readonly record struct Addr(bool V6, UInt128 Value);

    private IEnumerable<string> Enumerate(bool confirmedOverCap)
    {
        _unresolved.Clear();
        var excludedPoints = new HashSet<Addr>();
        var excludedBlocks = new List<Item>();
        foreach (var ex in _excludes)
        {
            if (ex is HostItem h) foreach (var (a, _) in Resolve(h.Name, null)) excludedPoints.Add(a);
            else if (ex.Count == 1) excludedPoints.Add(new Addr(ex.V6, ex.Enumerate().First()));
            else excludedBlocks.Add(ex);
        }
        bool Excluded(Addr a)
        {
            if (excludedPoints.Contains(a)) return true;
            foreach (var b in excludedBlocks) if (b.V6 == a.V6 && b.Contains(a.Value)) return true;
            return false;
        }

        // Duplicates are found by asking the earlier entries, never by remembering every yielded range address.
        var points = new HashSet<Addr>();
        var earlierBlocks = new List<Item>();
        long yielded = 0;
        foreach (var item in _includes)
        {
            IEnumerable<(Addr Address, string? Zone)> addresses = item is HostItem host
                ? Resolve(host.Name, _unresolved)
                : item.Enumerate().Select(v => (new Addr(item.V6, v), item.Zone));
            bool isPoint = item is HostItem || item.Count == 1;
            foreach (var (a, zone) in addresses)
            {
                if (Excluded(a) || points.Contains(a)) continue;
                bool dup = false;
                foreach (var b in earlierBlocks) if (b.V6 == a.V6 && b.Contains(a.Value)) { dup = true; break; }
                if (dup) continue;
                if (isPoint) points.Add(a);
                if (++yielded > HardCap && !confirmedOverCap) throw new TargetCapExceededException(yielded);
                yield return a.V6 ? new Ipv6Address(a.Value, zone).ToScopedString() : Ipv4.FromUInt((uint)a.Value);
            }
            if (!isPoint) earlierBlocks.Add(item);
        }
    }

    private IEnumerable<(Addr Address, string? Zone)> Resolve(string host, List<string>? unresolved)
    {
        var found = new List<(Addr, string?)>();
        var known = new HashSet<Addr>();
        IReadOnlyList<string> texts;
        try { texts = _resolver(host); }
        catch { texts = Array.Empty<string>(); }
        foreach (var t in texts)
        {
            if (!IpAddressKey.TryParse(t, out var key) || key.Family != _hostFamily) continue;
            var addr = new Addr(key.IsV6, key.Value);
            if (known.Add(addr)) found.Add((addr, key.Zone));
        }
        if (found.Count == 0) unresolved?.Add(host);
        return found;
    }

    private static IReadOnlyList<string> DefaultResolver(string host)
        => Dns.GetHostAddresses(host)
              .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
              .Select(a => a.ToString()).ToList();

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split('\n');

    private void AddFile(string path, bool include, Func<string, IEnumerable<string>> readLines)
    {
        List<string> lines;
        try { lines = readLines(path).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _errors.Add(new TargetSpecError(path, "The file could not be read."));
            return;
        }
        AddLines(lines, include);
    }

    private void AddLines(IEnumerable<string> lines, bool include)
    {
        foreach (var raw in lines)
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            foreach (var token in line.Split(new[] { ',', ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = ParseToken(token, include, out var reason);
                if (item is null) { _errors.Add(new TargetSpecError(token, reason!)); continue; }
                (include ? _includes : _excludes).Add(item);
            }
        }
    }

    // ---- token parsing -------------------------------------------------------------------------

    private static readonly Regex HostName = new(
        @"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Item? ParseToken(string token, bool include, out string? reason)
    {
        reason = null;
        if (token.Contains(':')) return ParseV6(token, include, out reason);
        if (token.Contains('/')) return ParseCidr(token, include, out reason);

        bool numericSyntax = token.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '*');
        if (numericSyntax) return ParseNumeric(token, out reason);

        if (token.Length <= 253 && HostName.IsMatch(token) && token.Any(char.IsAsciiLetter))
            return new HostItem(token);

        reason = "Not an address, range or hostname.";
        return null;
    }

    /// <summary>An IPv6 literal (with an optional zone), prefix or range; the zone of a range follows its end.</summary>
    private static Item? ParseV6(string token, bool include, out string? reason)
    {
        reason = null;
        if (token.Contains('/'))
        {
            if (token.Contains('%') || !Ipv6Prefix.TryParse(token, out var prefix))
            {
                reason = "Expected an IPv6 prefix such as 2001:db8::/112 (no zone, length 0 to 128).";
                return null;
            }
            if (include && prefix.ExceedsAddresses(HardCap)) { reason = Loc.Ipv6PrefixTooLarge; return null; }
            return new IntervalsItem(new List<(UInt128, UInt128)> { (prefix.Network, prefix.Last.Value) }, true, null);
        }

        string? zone = null;
        var body = token;
        int pct = token.IndexOf('%');
        if (pct >= 0) { zone = token[(pct + 1)..]; body = token[..pct]; }

        var halves = body.Split('-');
        if (halves.Length > 2
            || !Ipv6Address.TryParse(halves[0], out var first)
            || (halves.Length == 2 && !Ipv6Address.TryParse(halves[1], out _)))
        {
            reason = "Not a valid IPv6 address or range.";
            return null;
        }
        if (zone is not null && !Ipv6Address.TryParse(halves[0] + "%" + zone, out _))
        {
            reason = "The zone after % is not valid.";
            return null;
        }
        var last = halves.Length == 2 ? Ipv6Address.Parse(halves[1]) : first;
        if (first.Value > last.Value) { reason = "The range start is above its end."; return null; }
        if (include && last.Value - first.Value >= HardCap) { reason = Loc.Ipv6PrefixTooLarge; return null; }
        return new IntervalsItem(new List<(UInt128, UInt128)> { (first.Value, last.Value) }, true, zone);
    }

    private static Item? ParseCidr(string token, bool include, out string? reason)
    {
        reason = null;
        var parts = token.Split('/');
        if (parts.Length != 2 || !TryParseAddress(parts[0], out var addr))
        {
            reason = "Expected an IPv4 address and a prefix length such as 10.0.0.0/24.";
            return null;
        }
        if (parts[1].Length is 0 or > 2 || !parts[1].All(char.IsAsciiDigit) || int.Parse(parts[1]) > 32)
        {
            reason = "The prefix length must be 0 to 32.";
            return null;
        }
        int cidr = int.Parse(parts[1]);
        int hostBits = 32 - cidr;
        uint mask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint network = addr & mask, broadcast = network | ~mask;
        // An exclusion drops the whole block, network and broadcast address included.
        var iv = cidr >= 31 || !include ? (network, broadcast) : (network + 1, broadcast - 1);
        return new IntervalsItem(new List<(UInt128, UInt128)> { (iv.Item1, iv.Item2) }, false, null);
    }

    private static Item? ParseNumeric(string token, out string? reason)
    {
        reason = null;
        var halves = token.Split('-');
        if (halves.Length == 2 && TryParseAddress(halves[0], out var first) && TryParseAddress(halves[1], out var last))
        {
            if (first > last) { reason = "The range start is above its end."; return null; }
            return new IntervalsItem(new List<(UInt128, UInt128)> { (first, last) }, false, null);
        }

        var octets = token.Split('.');
        if (octets.Length != 4) { reason = "An address needs four parts, e.g. 192.168.1.10."; return null; }
        var lo = new int[4];
        var hi = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!TryParseOctetRange(octets[i], out lo[i], out hi[i]))
            {
                reason = $"Invalid part \"{octets[i]}\": use 0-255, a range like 1-254, or *.";
                return null;
            }
        }
        return new BoxItem(lo, hi);
    }

    private static bool TryParseOctetRange(string text, out int lo, out int hi)
    {
        lo = hi = 0;
        if (text == "*") { hi = 255; return true; }
        var p = text.Split('-');
        if (p.Length > 2 || !TryParseOctet(p[0], out lo)) return false;
        hi = lo;
        if (p.Length == 2 && !TryParseOctet(p[1], out hi)) return false;
        return lo <= hi;
    }

    private static bool TryParseOctet(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 3 || !text.All(char.IsAsciiDigit)) return false;
        value = int.Parse(text);
        return value <= 255;
    }

    private static bool TryParseAddress(string text, out uint value)
    {
        value = 0;
        var p = text.Split('.');
        if (p.Length != 4) return false;
        uint v = 0;
        foreach (var part in p)
        {
            if (!TryParseOctet(part, out var o)) return false;
            v = (v << 8) | (uint)o;
        }
        value = v;
        return true;
    }

    // ---- entries -------------------------------------------------------------------------------

    private abstract class Item
    {
        /// <summary>The address family of the entry; meaningless for a hostname.</summary>
        public virtual bool V6 => false;
        public virtual string? Zone => null;
        /// <summary>Number of addresses, saturating at <see cref="long.MaxValue"/>.</summary>
        public abstract long Count { get; }
        public abstract bool Contains(UInt128 address);
        public abstract IEnumerable<UInt128> Enumerate();
    }

    private sealed class HostItem : Item
    {
        public string Name { get; }
        public HostItem(string name) => Name = name;
        public override long Count => 1;
        public override bool Contains(UInt128 address) => false;
        public override IEnumerable<UInt128> Enumerate() => Array.Empty<UInt128>();
    }

    /// <summary>Disjoint address intervals of one family, enumerated in the stored order.</summary>
    private sealed class IntervalsItem : Item
    {
        private readonly List<(UInt128 First, UInt128 Last)> _intervals;
        private readonly bool _v6;
        private readonly string? _zone;

        public IntervalsItem(List<(UInt128 First, UInt128 Last)> intervals, bool v6, string? zone)
        {
            _intervals = intervals;
            _v6 = v6;
            _zone = zone;
        }

        public override bool V6 => _v6;
        public override string? Zone => _zone;

        public override long Count
        {
            get
            {
                UInt128 n = 0;
                foreach (var (first, last) in _intervals)
                {
                    var size = last - first + 1;          // wraps to 0 for the whole IPv6 space
                    if (size == 0 || size > long.MaxValue) return long.MaxValue;
                    n += size;
                    if (n > long.MaxValue) return long.MaxValue;
                }
                return (long)n;
            }
        }

        public override bool Contains(UInt128 address)
        {
            foreach (var (first, last) in _intervals)
                if (address >= first && address <= last) return true;
            return false;
        }

        public override IEnumerable<UInt128> Enumerate()
        {
            foreach (var (first, last) in _intervals)
                for (var a = first; ; a++)
                {
                    yield return a;
                    if (a == last) break;
                }
        }
    }

    /// <summary>One inclusive range per octet (IPv4).</summary>
    private sealed class BoxItem : Item
    {
        private readonly int[] _lo, _hi;
        public BoxItem(int[] lo, int[] hi) { _lo = lo; _hi = hi; }

        public override long Count
        {
            get
            {
                long n = 1;
                for (int i = 0; i < 4; i++) n *= _hi[i] - _lo[i] + 1;
                return n;
            }
        }

        public override bool Contains(UInt128 address)
        {
            if (address > uint.MaxValue) return false;
            for (int i = 0; i < 4; i++)
            {
                int o = (int)((uint)address >> (24 - 8 * i) & 0xFF);
                if (o < _lo[i] || o > _hi[i]) return false;
            }
            return true;
        }

        public override IEnumerable<UInt128> Enumerate()
        {
            for (int a = _lo[0]; a <= _hi[0]; a++)
            for (int b = _lo[1]; b <= _hi[1]; b++)
            for (int c = _lo[2]; c <= _hi[2]; c++)
            for (int d = _lo[3]; d <= _hi[3]; d++)
                yield return (uint)a << 24 | (uint)b << 16 | (uint)c << 8 | (uint)d;
        }
    }
}
