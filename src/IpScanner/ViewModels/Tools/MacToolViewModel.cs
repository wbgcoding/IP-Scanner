using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels.Tools;

/// <summary>MAC address tool page state: an offline vendor lookup and a lookup
/// in the address (ARP) table of this computer, the only part that touches the system.</summary>
public sealed class MacToolViewModel : ObservableObject
{
    private readonly Func<string, string?> _arpResolver;

    public MacToolViewModel(Func<string, string?>? arpResolver = null)
        => _arpResolver = arpResolver ?? (ip => Core.Scanner.Ipv6.Ipv6Pinger.IsIpv6(ip) ? Core.Scanner.Ipv6.Ipv6Pinger.MacOf(ip) : ArpHelper.Resolve(ip));

    // ── Vendor lookup ──
    private string _vendorMac = "";
    public string VendorMac { get => _vendorMac; set { if (_vendorMac != value) { _vendorMac = value; Raise(nameof(VendorMac)); ComputeVendor(); } } }

    public string VendorResult { get; private set; } = "";
    public bool HasVendorResult { get; private set; }
    // Computed live rather than cached, but the binding still needs telling to
    // re-read it after a language switch - see RefreshLanguage() below.
    public string VendorFooter => MacVendorLookup.Instance.SourceDate is { } d
        ? Loc.CalcVendorFooterMsg(MacVendorLookup.Instance.EntryCount, d)
        : Loc.CalcVendorFooterNoDateMsg(MacVendorLookup.Instance.EntryCount);

    public void RefreshLanguage()
    {
        Raise(nameof(VendorFooter));
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
