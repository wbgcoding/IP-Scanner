using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels.Tools;

/// <summary>Localized texts for port states, reasons and scan types.</summary>
public static class PortDisplay
{
    public static string StateText(PortState state) => state switch
    {
        PortState.Open => Loc.PortStateOpen,
        PortState.Closed => Loc.PortStateClosed,
        PortState.Filtered => Loc.PortStateFiltered,
        PortState.OpenFiltered => Loc.PortStateOpenFiltered,
        PortState.Unfiltered => Loc.PortStateUnfiltered,
        _ => state.ToString(),
    };

    /// <summary>The reason token of a result as text; an unknown token is shown as it is.</summary>
    public static string ReasonText(string reason) => reason switch
    {
        PortReason.SynAck => Loc.PortReasonSynAck,
        PortReason.Rst => Loc.PortReasonRst,
        PortReason.RstWindow => Loc.PortReasonRstWindow,
        PortReason.NoResponse => Loc.PortReasonNoResponse,
        PortReason.IcmpUnreachable => Loc.PortReasonIcmp,
        PortReason.UdpResponse => Loc.PortReasonUdpReply,
        PortReason.ConnectRefused => Loc.PortReasonRefused,
        PortReason.Connected => Loc.PortReasonConnected,
        _ => reason,
    };

    /// <summary>Why a driver-only scan type cannot run, as text.</summary>
    public static string UnavailableReasonText(string? reason) => reason switch
    {
        null or "" or "driver-missing" => Loc.PortDriverMissing,
        "transport-missing" => Loc.PortTransportMissing,
        _ => reason,
    };

    public static string ScanTypeTip(ScanType type) => type switch
    {
        ScanType.Connect => Loc.TipScanConnect,
        ScanType.Syn => Loc.TipScanSyn,
        ScanType.Ack => Loc.TipScanAck,
        ScanType.Window => Loc.TipScanWindow,
        ScanType.Fin => Loc.TipScanFin,
        ScanType.Null => Loc.TipScanNull,
        ScanType.Xmas => Loc.TipScanXmas,
        ScanType.Maimon => Loc.TipScanMaimon,
        _ => string.Empty,
    };
}

/// <summary>One entry of the scan-type picker. An unavailable entry is greyed out and its tooltip
/// carries the reason.</summary>
public sealed class ScanTypeOption : ObservableObject
{
    private bool _isAvailable;
    private string _tooltip = "";

    public ScanTypeOption(ScanType type)
    {
        Type = type;
        Label = ScanTypes.Label(type);
        Refresh();
    }

    public ScanType Type { get; }
    public string Label { get; }
    public bool IsAvailable { get => _isAvailable; private set => SetProperty(ref _isAvailable, value); }
    public bool IsAggressive => ScanTypes.IsAggressive(Type);
    public string Tooltip { get => _tooltip; private set => SetProperty(ref _tooltip, value); }

    /// <summary>Reads the availability again, after the packet driver was installed while the page was open.</summary>
    public void Refresh()
    {
        var (available, reason) = ScanTypes.Availability(Type);
        var tip = PortDisplay.ScanTypeTip(Type);
        Tooltip = available ? tip : string.Format(Loc.PortScanTypeUnavailableFmt, PortDisplay.UnavailableReasonText(reason)) + "\n" + tip;
        IsAvailable = available;
    }

    public override string ToString() => Label;
}
