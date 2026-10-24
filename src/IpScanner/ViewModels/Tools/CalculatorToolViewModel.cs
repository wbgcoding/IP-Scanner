using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels.Tools;

/// <summary>Offline calculator and lookup tool page state: three
/// independent, instant blocks - a subnet calculator, an OUI vendor lookup, and
/// an ARP address-table lookup (the only block that touches the network).</summary>
public sealed class CalculatorToolViewModel : ObservableObject
{
    private readonly Func<string, string?> _arpResolver;

    public CalculatorToolViewModel(Func<string, string?>? arpResolver = null)
        => _arpResolver = arpResolver ?? (ip => Core.Scanner.Ipv6.Ipv6Pinger.IsIpv6(ip) ? Core.Scanner.Ipv6.Ipv6Pinger.MacOf(ip) : ArpHelper.Resolve(ip));

    // ── Subnet calculator ──
    private string _subnetAddress = "";
    public string SubnetAddress { get => _subnetAddress; set { if (_subnetAddress != value) { _subnetAddress = value; Raise(nameof(SubnetAddress)); Compute(); } } }

    private string _subnetMask = "";
    public string SubnetMask { get => _subnetMask; set { if (_subnetMask != value) { _subnetMask = value; Raise(nameof(SubnetMask)); Compute(); } } }

    private string _subnetError = "";
    public string SubnetError { get => _subnetError; private set { if (_subnetError != value) { _subnetError = value; Raise(nameof(SubnetError)); } } }

    public string NetworkResult { get; private set; } = "";
    public string BroadcastResult { get; private set; } = "";
    public string FirstHostResult { get; private set; } = "";
    public string LastHostResult { get; private set; } = "";
    public string UsableHostsResult { get; private set; } = "";
    public string WildcardResult { get; private set; } = "";
    public string CidrResult { get; private set; } = "";
    public bool HasSubnetResult { get; private set; }

    internal readonly record struct SubnetResult(
        string Network, string Broadcast, string FirstHost, string LastHost, long UsableHosts, string Wildcard, int Cidr);

    /// <summary>Pure calculation, exposed for tests - null when the address or mask
    /// doesn't parse. /31 and /32 are the RFC 3021 / single-host special cases: no
    /// separate first/last host, every address in the block is usable.</summary>
    internal static SubnetResult? Calculate(string address, string mask)
    {
        // "192.168.2.0/24" typed into the address box is the same as the prefix in the mask box.
        if (address.IndexOf('/') is var slash and >= 0 && mask.Trim().Length == 0)
        {
            mask = address[slash..];
            address = address[..slash].Trim();
        }
        if (!Ipv4.TryParseUInt(address, out var addr)) return null;
        var trimmed = mask.Trim();
        int? cidr = trimmed.StartsWith('/') || (trimmed.Length is > 0 and <= 2 && trimmed.All(char.IsAsciiDigit))
            ? (int.TryParse(trimmed.StartsWith('/') ? trimmed[1..].Trim() : trimmed, out var c) && c is >= 1 and <= 32 ? c : null)
            : Ipv4.MaskToCidr(mask);
        if (cidr is not { } n) return null;

        int hostBits = 32 - n;
        uint blockMask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint network = addr & blockMask;
        uint broadcast = hostBits >= 32 ? 0xFFFFFFFFu : network | ~blockMask;
        uint wildcard = ~blockMask;

        string first, last;
        long usable;
        if (n == 32) { first = last = Ipv4.FromUInt(network); usable = 1; }
        else if (n == 31) { first = Ipv4.FromUInt(network); last = Ipv4.FromUInt(broadcast); usable = 2; }
        else { first = Ipv4.FromUInt(network + 1); last = Ipv4.FromUInt(broadcast - 1); usable = (long)(broadcast - network - 1); }

        return new SubnetResult(Ipv4.FromUInt(network), Ipv4.FromUInt(broadcast), first, last, usable, Ipv4.FromUInt(wildcard), n);
    }

    internal readonly record struct SubnetResult6(string Network, string Last, string Count, int Prefix);

    /// <summary>Largest host-bit count shown as an exact number; wider prefixes show "2^n".</summary>
    private const int ExactCountMaxBits = 40;

    /// <summary>IPv6 counterpart of <see cref="Calculate"/>: the address may carry its own "/length", or the
    /// mask field supplies "/length" or a bare number. Null when either part does not parse.</summary>
    internal static SubnetResult6? CalculateIpv6(string address, string mask)
    {
        address = address.Trim(); mask = mask.Trim();
        var slash = address.IndexOf('/');
        if (slash >= 0)
        {
            if (mask.Length > 0) return null;
            mask = address[(slash + 1)..];
            address = address[..slash];
        }
        mask = mask.TrimStart('/');
        if (mask.Length is 0 or > 3 || !mask.All(char.IsAsciiDigit)) return null;
        if (!Ipv6Prefix.TryParse(address + "/" + mask, out var prefix)) return null;
        var count = prefix.HostBits <= ExactCountMaxBits
            ? (UInt128.One << prefix.HostBits).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "2^" + prefix.HostBits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new SubnetResult6(prefix.First.ToString(), prefix.Last.ToString(), count, prefix.Length);
    }

    /// <summary>True when <paramref name="candidate"/> lies inside the calculated subnet; null when either input is unusable.
    /// Works for both families (an address of the other family is simply outside).</summary>
    internal static bool? IsInside(string address, string mask, string candidate)
    {
        candidate = candidate.Trim();
        if (IsIpv6Input(address))
        {
            var a = address.Trim(); var m = mask.Trim();
            var slash = a.IndexOf('/');
            if (slash >= 0) { m = a[(slash + 1)..]; a = a[..slash]; }
            if (!Ipv6Prefix.TryParse(a + "/" + m.TrimStart('/'), out var p)) return null;
            if (Ipv4.TryParseUInt(candidate, out _)) return false;
            return Ipv6Address.TryParse(candidate, out var c) ? p.Contains(c) : null;
        }
        var r = Calculate(address.Trim(), mask.Trim());
        if (r is null || !Ipv4.TryParseUInt(candidate, out var cu)) return Ipv6Address.TryParse(candidate, out _) && r is not null ? false : null;
        Ipv4.TryParseUInt(r.Value.Network, out var net);
        uint blockMask = 32 - r.Value.Cidr >= 32 ? 0u : 0xFFFFFFFFu << (32 - r.Value.Cidr);
        return (cu & blockMask) == net;
    }

    private static bool IsIpv6Input(string address) => address.Contains(':');

    private void Compute()
    {
        if (SubnetAddress.Trim().Length == 0 && SubnetMask.Trim().Length == 0)
        {
            HasSubnetResult = false; SubnetError = "";
            Raise(nameof(HasSubnetResult)); Raise(nameof(SubnetError));
            return;
        }
        if (IsIpv6Input(SubnetAddress))
        {
            ComputeIpv6();
            return;
        }
        var r = Calculate(SubnetAddress.Trim(), SubnetMask.Trim());
        if (r is not { } result)
        {
            HasSubnetResult = false;
            SubnetError = Loc.CalcInvalidSubnet;
            Raise(nameof(HasSubnetResult)); Raise(nameof(SubnetError));
            return;
        }
        NetworkResult = result.Network;
        BroadcastResult = result.Broadcast;
        FirstHostResult = result.FirstHost;
        LastHostResult = result.LastHost;
        UsableHostsResult = result.UsableHosts.ToString();
        WildcardResult = result.Wildcard;
        CidrResult = $"/{result.Cidr}";
        HasSubnetResult = true;
        IsIpv6Result = false;
        SubnetError = "";
        Raise(nameof(IsIpv6Result)); Raise(nameof(BroadcastLabel)); Raise(nameof(UsableHostsLabel));
        Raise(nameof(NetworkResult)); Raise(nameof(BroadcastResult)); Raise(nameof(FirstHostResult));
        Raise(nameof(LastHostResult)); Raise(nameof(UsableHostsResult)); Raise(nameof(WildcardResult));
        Raise(nameof(CidrResult)); Raise(nameof(HasSubnetResult)); Raise(nameof(SubnetError));
    }

    private void ComputeIpv6()
    {
        var r = CalculateIpv6(SubnetAddress, SubnetMask);
        if (r is not { } result)
        {
            HasSubnetResult = false;
            SubnetError = Loc.CalcInvalidSubnet;
            Raise(nameof(HasSubnetResult)); Raise(nameof(SubnetError));
            return;
        }
        NetworkResult = result.Network;
        BroadcastResult = result.Last;
        FirstHostResult = LastHostResult = WildcardResult = "";
        UsableHostsResult = result.Count;
        CidrResult = $"/{result.Prefix}";
        HasSubnetResult = true;
        IsIpv6Result = true;
        SubnetError = "";
        Raise(nameof(IsIpv6Result)); Raise(nameof(BroadcastLabel)); Raise(nameof(UsableHostsLabel));
        Raise(nameof(NetworkResult)); Raise(nameof(BroadcastResult)); Raise(nameof(FirstHostResult));
        Raise(nameof(LastHostResult)); Raise(nameof(UsableHostsResult)); Raise(nameof(WildcardResult));
        Raise(nameof(CidrResult)); Raise(nameof(HasSubnetResult)); Raise(nameof(SubnetError));
        ComputeContains();
    }

    /// <summary>True while the shown result is an IPv6 prefix (no broadcast, host range or wildcard rows).</summary>
    public bool IsIpv6Result { get; private set; }
    public string BroadcastLabel => IsIpv6Result ? Loc.CalcLastAddressLabel : Loc.CalcBroadcastLabel;
    public string UsableHostsLabel => IsIpv6Result ? Loc.CalcAddressCountLabel : Loc.CalcUsableHostsLabel;

    // ── Contains test ──
    private string _containsAddress = "";
    public string ContainsAddress { get => _containsAddress; set { if (_containsAddress != value) { _containsAddress = value; Raise(nameof(ContainsAddress)); ComputeContains(); } } }
    public string ContainsResult { get; private set; } = "";
    public bool HasContainsResult { get; private set; }

    private void ComputeContains()
    {
        var inside = ContainsAddress.Trim().Length == 0 || !HasSubnetResult
            ? (bool?)null
            : IsInside(SubnetAddress, SubnetMask, ContainsAddress);
        HasContainsResult = inside is not null || (ContainsAddress.Trim().Length > 0 && HasSubnetResult);
        ContainsResult = inside switch { true => Loc.CalcContainsYes, false => Loc.CalcContainsNo, _ => ContainsAddress.Trim().Length > 0 && HasSubnetResult ? Loc.CalcContainsInvalid : "" };
        Raise(nameof(ContainsResult)); Raise(nameof(HasContainsResult));
    }


    // ── Vendor lookup ──
    private string _vendorMac = "";
    public string VendorMac { get => _vendorMac; set { if (_vendorMac != value) { _vendorMac = value; Raise(nameof(VendorMac)); ComputeVendor(); } } }

    public string VendorResult { get; private set; } = "";
    public bool HasVendorResult { get; private set; }
    // Computed live rather than cached, but the binding still needs telling to
    // re-read it after a language switch (docs/DESIGN.md's "Loc value read once"
    // rule) - see RefreshLanguage() below.
    public string VendorFooter => MacVendorLookup.Instance.SourceDate is { } d
        ? Loc.CalcVendorFooterMsg(MacVendorLookup.Instance.EntryCount, d)
        : Loc.CalcVendorFooterNoDateMsg(MacVendorLookup.Instance.EntryCount);

    public void RefreshLanguage()
    {
        Raise(nameof(VendorFooter)); Raise(nameof(BroadcastLabel)); Raise(nameof(UsableHostsLabel));
        if (SubnetError.Length > 0) SubnetError = Loc.CalcInvalidSubnet;
        ComputeContains();
        if (HasVendorResult) ComputeVendor();
        if (HasArpResult) ShowArpResult(_arpMac);
    }

    private void ComputeVendor()
    {
        if (VendorMac.Trim().Length == 0) { HasVendorResult = false; Raise(nameof(HasVendorResult)); return; }
        VendorResult = MacVendorLookup.Instance.Lookup(VendorMac) ?? Loc.CalcVendorNotFound;
        HasVendorResult = true;
        Raise(nameof(VendorResult)); Raise(nameof(HasVendorResult));
    }

    // ── Address table (ARP) ──
    private string _arpIp = "";
    public string ArpIp { get => _arpIp; set { if (_arpIp != value) { _arpIp = value; Raise(nameof(ArpIp)); Raise(nameof(CanLookupArp)); } } }
    public bool CanLookupArp => !_arpLookupRunning && AddressSyntax.IsValidAddress(ArpIp.Trim());

    private bool _arpLookupRunning;
    private string? _arpMac;

    public string ArpMac { get; private set; } = "";
    public string ArpVendor { get; private set; } = "";
    public bool HasArpResult { get; private set; }

    public async Task LookupArpAsync()
    {
        if (!CanLookupArp) return;
        var ip = ArpIp.Trim();
        _arpLookupRunning = true;
        Raise(nameof(CanLookupArp));
        try
        {
            var mac = await Task.Run(() => _arpResolver(ip));
            _arpMac = mac;
            ShowArpResult(mac);
        }
        finally
        {
            _arpLookupRunning = false;
            Raise(nameof(CanLookupArp));
        }
    }

    private void ShowArpResult(string? mac)
    {
        ArpMac = mac ?? Loc.CalcArpNotFound;
        ArpVendor = mac is { } m ? MacVendorLookup.Instance.Lookup(m) ?? Loc.CalcVendorNotFound : "";
        HasArpResult = true;
        Raise(nameof(ArpMac)); Raise(nameof(ArpVendor)); Raise(nameof(HasArpResult));
    }
}
