using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner.Checks;

/// <summary>SNMP sysDescr (OID 1.3.6.1.2.1.1.1.0) read with the community configured for this
/// network, or "public" outside it (see <see cref="PortScanner.SnmpCommunityFor"/>).</summary>
internal sealed class SnmpSysDescrCheck : CheckBase
{
    private readonly ICheckIo _io;
    public SnmpSysDescrCheck(ICheckIo io) : base("snmp-sysdescr", CheckCategory.Discovery) => _io = io;

    public override bool Matches(NetService service) => IsUdp(service, 161);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var request = PortScanner.BuildSnmpGetSysDescr(PortScanner.SnmpCommunityFor(target));
        var reply = await _io.UdpRequestAsync(target, 161, request, PortScanner.EffectiveConnectTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var text = ParseSysDescr(reply);
        return text is null
            ? Done(service, new List<CheckFinding>())
            : Done(service, new List<CheckFinding> { F("sysDescr", text, 200) });
    }

    /// <summary>Pulls the OCTET STRING value out of the GetResponse PDU. Public read access returns the
    /// sysDescr value as the last OCTET STRING in the varbind; anything else in the shape means the
    /// community was wrong and the agent is only echoing back an empty or error response.</summary>
    internal static string? ParseSysDescr(byte[] p)
    {
        try
        {
            int pos = 0;
            if (!ReadTag(p, ref pos, 0x30, out _)) return null;        // outer SEQUENCE
            if (!SkipTlv(p, ref pos)) return null;                     // version
            if (!SkipTlv(p, ref pos)) return null;                     // community
            if (!ReadTag(p, ref pos, 0xA2, out _)) return null;        // GetResponse-PDU (0xA2); anything else is not a reply
            if (!SkipTlv(p, ref pos)) return null;                     // request id
            if (!SkipTlv(p, ref pos)) return null;                     // error status
            if (!SkipTlv(p, ref pos)) return null;                     // error index
            if (!ReadTag(p, ref pos, 0x30, out _)) return null;        // varbind list
            if (!ReadTag(p, ref pos, 0x30, out _)) return null;        // one varbind
            if (!SkipTlv(p, ref pos)) return null;                     // the OID
            if (!ReadTag(p, ref pos, 0x04, out int len)) return null;  // OCTET STRING value
            if (pos + len > p.Length) return null;
            var text = Encoding.ASCII.GetString(p, pos, len);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
    }

    private static bool ReadTag(byte[] p, ref int pos, byte expectedTag, out int length)
    {
        length = 0;
        if (pos >= p.Length || p[pos] != expectedTag) return false;
        pos++;
        return ReadLength(p, ref pos, out length);
    }

    private static bool ReadLength(byte[] p, ref int pos, out int length)
    {
        length = 0;
        if (pos >= p.Length) return false;
        int first = p[pos++];
        if (first < 0x80) { length = first; return true; }
        int count = first & 0x7F;
        if (count == 0 || count > 2 || pos + count > p.Length) return false;
        for (int i = 0; i < count; i++) length = (length << 8) | p[pos++];
        return true;
    }

    private static bool SkipTlv(byte[] p, ref int pos)
    {
        if (pos >= p.Length) return false;
        pos++; // tag
        if (!ReadLength(p, ref pos, out int len)) return false;
        if (pos + len > p.Length) return false;
        pos += len;
        return true;
    }
}

/// <summary>UPnP device description: a unicast M-SEARCH, the LOCATION header, then that one URL's XML -
/// fetched only when its host is the target itself, so a device cannot redirect the scanner elsewhere.</summary>
internal sealed class UpnpDeviceCheck : CheckBase
{
    private static readonly Regex LocationRx = new(@"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(500));
    private static readonly (string Key, string Tag)[] Fields =
    {
        ("friendlyName", "friendlyName"), ("manufacturer", "manufacturer"),
        ("modelName", "modelName"), ("modelNumber", "modelNumber"),
    };

    private readonly ICheckIo _io;
    public UpnpDeviceCheck(ICheckIo io) : base("upnp-device", CheckCategory.Discovery) => _io = io;

    public override bool Matches(NetService service) => IsUdp(service, 1900) || (service.Protocol == NetProtocol.Udp && service.Name == "SSDP");

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var reply = await _io.UdpRequestAsync(target, 1900, PortScanner.SsdpProbe, PortScanner.EffectiveConnectTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var text = Encoding.ASCII.GetString(reply);
        var location = SafeMatch(LocationRx, text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || !HostMatches(uri, target))
            return Done(service, new List<CheckFinding> { F("upnpLocation", location ?? "") });

        var xmlReply = await _io.HttpGetAsync(target, uri.Port, false, uri.PathAndQuery, ct).ConfigureAwait(false);
        var findings = new List<CheckFinding>();
        if (xmlReply is not null)
            foreach (var (key, tag) in Fields)
                if (ExtractXmlField(xmlReply.Body, tag) is { Length: > 0 } value) findings.Add(F(key, value));
        return Done(service, findings);
    }

    private static bool HostMatches(Uri uri, string target) => string.Equals(uri.Host, target, StringComparison.OrdinalIgnoreCase);

    private static Match? SafeMatch(Regex rx, string text) { try { return rx.Match(text); } catch (RegexMatchTimeoutException) { return null; } }

    internal static string? ExtractXmlField(string xml, string tag)
    {
        try
        {
            var rx = new Regex($@"<{tag}[^>]*>(.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(500));
            var m = rx.Match(xml);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}

/// <summary>Instance names mDNS services advertise, read with a unicast query sent to the target only
/// (never to the multicast group), so the answer is tied to this one device.</summary>
internal sealed class MdnsServicesCheck : CheckBase
{
    private const string ServiceListName = "_services._dns-sd._udp.local";

    private readonly ICheckIo _io;
    public MdnsServicesCheck(ICheckIo io) : base("mdns-services", CheckCategory.Discovery) => _io = io;

    public override bool Matches(NetService service) => IsUdp(service, 5353);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var reply = await _io.UdpRequestAsync(target, 5353, BuildPtrQuery(ServiceListName), PortScanner.EffectiveConnectTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var names = ParsePtrNames(reply);
        var findings = names.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxListed).Select(n => F("mdnsService", n)).ToList();
        return Done(service, findings);
    }

    /// <summary>A DNS query for one name, QU bit set (unicast reply) - the same shape as
    /// <see cref="MdnsHelper.BuildPtrQuery"/> but for an arbitrary already-dotted name.</summary>
    internal static byte[] BuildPtrQuery(string name)
    {
        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0); W16(0); W16(1); W16(0); W16(0); W16(0);
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }
        ms.WriteByte(0);
        W16(12);     // QTYPE = PTR
        W16(0x8001); // QCLASS = IN, QU bit
        return ms.ToArray();
    }

    /// <summary>Every PTR answer's target name, decompressed.</summary>
    internal static List<string> ParsePtrNames(byte[] p)
    {
        var names = new List<string>();
        try
        {
            if (p.Length < 12) return names;
            int qd = (p[4] << 8) | p[5];
            int an = (p[6] << 8) | p[7];
            int pos = 12;
            for (int i = 0; i < qd; i++)
            {
                SkipName(p, ref pos);
                if (pos + 4 > p.Length) return names;
                pos += 4;
            }
            for (int i = 0; i < an; i++)
            {
                if (pos >= p.Length) break;
                SkipName(p, ref pos);
                if (pos + 10 > p.Length) break;
                int type = (p[pos] << 8) | p[pos + 1];
                pos += 8;
                int rdlen = (p[pos] << 8) | p[pos + 1];
                pos += 2;
                if (type == 12)
                {
                    int rp = pos;
                    var name = ReadName(p, ref rp);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
                pos += rdlen;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { /* malformed packet, keep what parsed */ }
        return names;
    }

    private static void SkipName(byte[] p, ref int pos)
    {
        while (pos < p.Length)
        {
            int len = p[pos];
            if (len == 0) { pos++; return; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return; }
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
                if (++jumps > 8 || cur + 1 >= p.Length) break;
                int target = ((len & 0x3F) << 8) | p[cur + 1];
                if (!jumped) pos = cur + 2;
                cur = target;
                jumped = true;
                continue;
            }
            if (cur + 1 + len > p.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(p, cur + 1, len));
            cur += len + 1;
        }
        return sb.ToString();
    }
}

/// <summary>Names a NetBIOS node answers to on an NBSTAT query - the same probe the port scanner
/// already sends to label open port 137, read here as a per-host finding.</summary>
internal sealed class NetBiosNamesCheck : CheckBase
{
    private readonly ICheckIo _io;
    public NetBiosNamesCheck(ICheckIo io) : base("netbios-names", CheckCategory.Discovery, perHost: true) => _io = io;

    public override bool Matches(NetService service)
        => IsUdp(service, 137) || (IsTcp(service) && service.Port == 139);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var reply = await _io.UdpRequestAsync(target, 137, PortScanner.NetBiosNbstatProbe, PortScanner.EffectiveConnectTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return Silent(service);
        var names = ParseNames(reply);
        return Done(service, names.Take(MaxListed).Select(n => F("netbiosName", n)).ToList());
    }

    /// <summary>The unique (non-group) names from an NBSTAT response, de-duplicated and trimmed of
    /// their padding and the trailing "type" byte groups never carry readable text.</summary>
    internal static List<string> ParseNames(byte[] p)
    {
        var names = new List<string>();
        try
        {
            if (p.Length < 57) return names;
            int count = p[56];
            int pos = 57;
            for (int i = 0; i < count && pos + 18 <= p.Length; i++, pos += 18)
            {
                var raw = Encoding.ASCII.GetString(p, pos, 15).TrimEnd(' ', '\0');
                byte flags = p[pos + 16];
                bool group = (flags & 0x80) != 0;
                if (!group && raw.Length > 0) names.Add(raw);
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { /* malformed packet */ }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Who answers for DHCP on this segment: a broadcast discover, read for the offers that
/// come back. Intrusive because the discover reaches every device on the segment, not just the
/// target, and the reply addresses are visible to everyone listening.</summary>
internal sealed class DhcpDiscoveryCheck : CheckBase
{
    private const int ListenMs = 3000;

    private readonly ICheckIo _io;
    public DhcpDiscoveryCheck(ICheckIo io) : base("dhcp-discover", CheckCategory.Intrusive, perHost: true) => _io = io;

    public override bool Matches(NetService service) => IsUdp(service, 67);

    public override async Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct)
    {
        var discover = BuildDiscover();
        var replies = await _io.DhcpBroadcastAsync(discover, ListenMs, ct).ConfigureAwait(false);
        if (replies is null) return Silent(service);
        var xid = discover[4..8];
        var findings = new List<CheckFinding>();
        foreach (var offer in replies)
        {
            if (ParseOffer(offer, xid) is not { } o) continue;
            findings.Add(new CheckFinding("dhcpServer", o.ServerId ?? o.YourAddress ?? ""));
            if (o.Router is { Length: > 0 } r) findings.Add(F("dhcpRouter", r));
            if (o.Dns is { Length: > 0 } d) findings.Add(F("dhcpDns", d));
            if (o.LeaseSeconds is { } secs) findings.Add(new CheckFinding("dhcpLease", secs.ToString()));
        }
        return Done(service, findings);
    }

    internal sealed record Offer(string? YourAddress, string? ServerId, string? Router, string? Dns, uint? LeaseSeconds);

    /// <summary>A random locally administered MAC (the "private use" bit set, so it can never collide
    /// with a real vendor address) - this probe never claims to be an existing device on the network.</summary>
    private static readonly byte[] ClientMac = BuildClientMac();

    private static byte[] BuildClientMac()
    {
        var mac = new byte[6];
        Random.Shared.NextBytes(mac);
        mac[0] = (byte)((mac[0] | 0x02) & 0xFE); // locally administered, unicast
        return mac;
    }

    internal static byte[] BuildDiscover()
    {
        var p = new byte[240];
        p[0] = 1;                 // BOOTREQUEST
        p[1] = 1;                 // htype ethernet
        p[2] = 6;                 // hlen
        uint xid = (uint)Random.Shared.Next();
        p[4] = (byte)(xid >> 24); p[5] = (byte)(xid >> 16); p[6] = (byte)(xid >> 8); p[7] = (byte)xid;
        p[10] = 0x80;              // broadcast flag: the offer comes back to 255.255.255.255, not a unicast IP we never got
        Array.Copy(ClientMac, 0, p, 28, 6);
        p[236] = 0x63; p[237] = 0x82; p[238] = 0x53; p[239] = 0x63; // magic cookie
        var options = new List<byte> { 53, 1, 1 };                 // option 53 = message type, DHCPDISCOVER
        options.AddRange(new byte[] { 55, 4, 1, 3, 6, 15 });        // parameter request list: mask, router, dns, domain
        options.Add(255);                                          // end
        return p.Concat(options).ToArray();
    }

    /// <summary>The offer in <paramref name="p"/>, or null when it is not a DHCPOFFER answering our request
    /// (same transaction id, our client hardware address, message type 2).</summary>
    internal static Offer? ParseOffer(byte[] p, byte[] xid)
    {
        try
        {
            if (p.Length < 240 || p[0] != 2) return null; // not a BOOTREPLY
            if (!p.AsSpan(4, 4).SequenceEqual(xid) || !p.AsSpan(28, 6).SequenceEqual(ClientMac)) return null;
            string? yiaddr = p[16] == 0 && p[17] == 0 && p[18] == 0 && p[19] == 0 ? null : $"{p[16]}.{p[17]}.{p[18]}.{p[19]}";
            string? serverId = null, router = null, dns = null;
            uint? lease = null;
            int messageType = 0;
            int pos = 240;
            while (pos < p.Length)
            {
                int code = p[pos++];
                if (code == 255) break;
                if (code == 0) continue; // pad
                if (pos >= p.Length) break;
                int len = p[pos++];
                if (pos + len > p.Length) break;
                switch (code)
                {
                    case 53 when len == 1: messageType = p[pos]; break;
                    case 54 when len == 4: serverId = $"{p[pos]}.{p[pos + 1]}.{p[pos + 2]}.{p[pos + 3]}"; break;
                    case 3 when len >= 4: router = $"{p[pos]}.{p[pos + 1]}.{p[pos + 2]}.{p[pos + 3]}"; break;
                    case 6 when len >= 4: dns = $"{p[pos]}.{p[pos + 1]}.{p[pos + 2]}.{p[pos + 3]}"; break;
                    case 51 when len == 4: lease = (uint)((p[pos] << 24) | (p[pos + 1] << 16) | (p[pos + 2] << 8) | p[pos + 3]); break;
                }
                pos += len;
            }
            return messageType == 2 ? new Offer(yiaddr, serverId, router, dns, lease) : null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
    }
}
