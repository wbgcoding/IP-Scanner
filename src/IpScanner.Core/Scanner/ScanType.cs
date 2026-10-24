namespace IpScanner.Core.Scanner;

/// <summary>The TCP probing technique of a port scan. UDP is probed by its own strategy
/// whenever UDP ports are selected.</summary>
public enum ScanType
{
    /// <summary>Full TCP handshake through the operating system; always available.</summary>
    Connect,
    /// <summary>Half-open SYN probe (driver only).</summary>
    Syn,
    /// <summary>ACK probe: maps firewall rules, cannot tell open from closed (driver only).</summary>
    Ack,
    /// <summary>ACK probe that reads the RST window field (driver only).</summary>
    Window,
    /// <summary>FIN probe (driver only, aggressive).</summary>
    Fin,
    /// <summary>No flags set (driver only, aggressive).</summary>
    Null,
    /// <summary>FIN, PSH and URG set (driver only, aggressive).</summary>
    Xmas,
    /// <summary>FIN and ACK set (driver only, aggressive).</summary>
    Maimon,
}

/// <summary>What a scan run should do: which TCP technique, and which protocols.</summary>
public sealed record ScanOptions(ScanType TcpType = ScanType.Connect, bool Tcp = true, bool Udp = true)
{
    public static ScanOptions Default { get; } = new();
}

/// <summary>Static facts about the scan types: what needs the driver, what reads as an attack.</summary>
public static class ScanTypes
{
    public static IReadOnlyList<ScanType> All { get; } = Enum.GetValues<ScanType>();

    public static bool RequiresDriver(ScanType type) => type != ScanType.Connect;

    /// <summary>The flag-abuse types that intrusion detection flags immediately: they sit behind the scan warning.</summary>
    public static bool IsAggressive(ScanType type) => type is ScanType.Fin or ScanType.Null or ScanType.Xmas or ScanType.Maimon;

    /// <summary>Short technical name shown in the picker; the same in every language.</summary>
    public static string Label(ScanType type) => type switch
    {
        ScanType.Connect => "Connect",
        ScanType.Syn => "SYN",
        ScanType.Ack => "ACK",
        ScanType.Window => "Window",
        ScanType.Fin => "FIN",
        ScanType.Null => "NULL",
        ScanType.Xmas => "Xmas",
        ScanType.Maimon => "Maimon",
        _ => type.ToString(),
    };

    /// <summary>Usable now, or the reason it is not (a status token, localized by the UI).</summary>
    public static (bool Available, string? Reason) Availability(ScanType type)
        => Availability(type, PacketDriver.Status, RawTransportProvider.Factory is not null);

    /// <summary>The same decision for an explicit driver status and transport state, so the capability
    /// view and the picker can never disagree.</summary>
    internal static (bool Available, string? Reason) Availability(ScanType type, PacketDriverStatus driver, bool transportRegistered)
    {
        if (!RequiresDriver(type)) return (true, null);
        if (!driver.Available) return (false, driver.Reason ?? PacketDriver.ReasonMissing);
        if (!transportRegistered) return (false, "transport-missing");
        return (true, null);
    }
}
