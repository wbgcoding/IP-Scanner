namespace IpScanner.Core.Scanner;

/// <summary>What a probe can say about one port. Only Open means a service answered; the other
/// states describe what the network did, which is often all a scan can know.</summary>
public enum PortState
{
    Open,
    Closed,
    /// <summary>Nothing came back or an intermediate device refused: a firewall is in the way.</summary>
    Filtered,
    /// <summary>The port did not answer in a way that tells open from filtered (UDP silence, FIN-type probes).</summary>
    OpenFiltered,
    /// <summary>Reachable, but the probe cannot tell whether a service listens (ACK scan).</summary>
    Unfiltered,
}

/// <summary>Why a port got its state, the short tokens shown by the optional reason column.</summary>
public static class PortReason
{
    public const string SynAck = "syn-ack";
    public const string Rst = "rst";
    public const string RstWindow = "rst-window";
    public const string NoResponse = "no-response";
    public const string IcmpUnreachable = "icmp-unreachable";
    public const string UdpResponse = "udp-response";
    public const string ConnectRefused = "conn-refused";
    public const string Connected = "connected";
    public const string Cancelled = "cancelled";
}

/// <summary>The outcome of probing one port.</summary>
public sealed record PortResult(int Port, NetProtocol Protocol, PortState State, string Reason, NetService? Service = null)
{
    public string Ip { get; init; } = "";
    public string ProtoLabel => Protocol == NetProtocol.Udp ? "UDP" : "TCP";
    public string PortLabel => $"{Port}/{ProtoLabel}";
    public string ServiceName => Service?.DisplayName is { Length: > 0 } n ? n : PortSpec.ServiceNameFor(Port, Protocol);
    public bool IsOpen => State == PortState.Open;

    /// <summary>The accessible name of a result row: port and service.</summary>
    public override string ToString() => (PortLabel + " " + ServiceName).Trim();
}
