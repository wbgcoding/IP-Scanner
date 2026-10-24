namespace IpScanner.Core.Scanner;

public enum CapabilityGroup { PortScan, Discovery, OsDetection }

/// <summary>Every scan type the app offers that depends on the machine: the packet driver,
/// administrator rights or the address family of the target.</summary>
public enum Capability
{
    TcpConnectScan, SynScan, FinScan, NullScan, XmasScan, AckScan, WindowScan, MaimonScan, UdpScan,
    ArpSweep, IcmpEcho, IcmpTimestamp, IcmpAddressMask, TcpConnectPing, TcpSynPing, TcpAckPing, UdpPing, ListScan, SkipDiscovery,
    OsEstimate, OsFingerprint,
}

/// <param name="Reason">Machine-readable reason code (see <see cref="PacketDriver"/> constants), null when available.</param>
public sealed record CapabilityState(Capability Id, bool Available, string? Reason);

public static class Capabilities
{
    private sealed record Rule(CapabilityGroup Group, bool NeedsDriver = false, bool NeedsAdmin = false, bool Ipv4Only = false, ScanType? Scan = null);

    private static readonly (Capability Id, Rule Rule)[] Table =
    {
        (Capability.TcpConnectScan, new(CapabilityGroup.PortScan, Scan: ScanType.Connect)),
        (Capability.SynScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Syn)),
        (Capability.FinScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Fin)),
        (Capability.NullScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Null)),
        (Capability.XmasScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Xmas)),
        (Capability.AckScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Ack)),
        (Capability.WindowScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Window)),
        (Capability.MaimonScan, new(CapabilityGroup.PortScan, NeedsDriver: true, Ipv4Only: true, Scan: ScanType.Maimon)),
        (Capability.UdpScan, new(CapabilityGroup.PortScan)),
        (Capability.ArpSweep, new(CapabilityGroup.Discovery, Ipv4Only: true)),
        (Capability.IcmpEcho, new(CapabilityGroup.Discovery)),
        (Capability.IcmpTimestamp, new(CapabilityGroup.Discovery, NeedsAdmin: true, Ipv4Only: true)),
        (Capability.IcmpAddressMask, new(CapabilityGroup.Discovery, NeedsAdmin: true, Ipv4Only: true)),
        (Capability.TcpConnectPing, new(CapabilityGroup.Discovery)),
        (Capability.TcpSynPing, new(CapabilityGroup.Discovery, NeedsDriver: true, Ipv4Only: true)),
        (Capability.TcpAckPing, new(CapabilityGroup.Discovery, NeedsDriver: true, Ipv4Only: true)),
        (Capability.UdpPing, new(CapabilityGroup.Discovery)),
        (Capability.ListScan, new(CapabilityGroup.Discovery)),
        (Capability.SkipDiscovery, new(CapabilityGroup.Discovery)),
        (Capability.OsEstimate, new(CapabilityGroup.OsDetection)),
        (Capability.OsFingerprint, new(CapabilityGroup.OsDetection, NeedsDriver: true, Ipv4Only: true)),
    };

    public static IReadOnlyList<Capability> All { get; } = Table.Select(t => t.Id).ToArray();

    public static CapabilityGroup GroupOf(Capability id) => RuleOf(id).Group;

    /// <summary>The port scan type a capability stands for, null for every other capability.</summary>
    internal static ScanType? RuleScan(Capability id) => RuleOf(id).Scan;

    public static bool NeedsDriver(Capability id) => RuleOf(id).NeedsDriver;

    /// <summary>Availability of one capability for the current machine. <paramref name="ipv6Target"/> asks about an
    /// IPv6 target, which the raw-packet and ICMPv4 based modes do not handle.</summary>
    public static CapabilityState Evaluate(Capability id, bool ipv6Target = false)
        => Evaluate(id, PacketDriver.Status, PacketDriver.Elevated, ipv6Target);

    /// <param name="transportRegistered">Whether a raw transport is registered; null reads the live state.
    /// A raw scan type is only usable with both the driver and a transport.</param>
    public static CapabilityState Evaluate(Capability id, PacketDriverStatus driver, bool elevated, bool ipv6Target = false,
        bool? transportRegistered = null)
    {
        var rule = RuleOf(id);
        if (ipv6Target && rule.Ipv4Only) return new(id, false, PacketDriver.ReasonIpv6);
        if (rule.Scan is { } scan)
        {
            var (available, reason) = ScanTypes.Availability(scan, driver, transportRegistered ?? RawTransportProvider.Factory is not null);
            return new(id, available, reason);
        }
        if (rule.NeedsDriver && !driver.Available) return new(id, false, driver.Reason ?? PacketDriver.ReasonMissing);
        if (rule.NeedsAdmin && !elevated) return new(id, false, PacketDriver.ReasonAdmin);
        return new(id, true, null);
    }

    /// <summary>True when the capability works against an IPv6 target.</summary>
    public static bool SupportsIpv6(Capability id) => !RuleOf(id).Ipv4Only;

    /// <summary>True when the scan type works against an IPv6 target (connect and UDP; the raw types are IPv4 only).</summary>
    public static bool SupportsIpv6(ScanType type)
    {
        foreach (var (_, rule) in Table) if (rule.Scan == type) return !rule.Ipv4Only;
        return true;
    }

    private static Rule RuleOf(Capability id)
    {
        foreach (var (cap, rule) in Table) if (cap == id) return rule;
        throw new ArgumentOutOfRangeException(nameof(id));
    }
}
