using System.Collections.Concurrent;

namespace IpScanner.Core.Scanner;

/// <summary>What port scans of this app session found refused. A TCP port counts as closed only when
/// the host answered the probe with a reset or a refusal; silence (filtered, dropped) and any other
/// answer clear the fact again, so a later scan always wins. Nothing is persisted: a result stored by an
/// older session says nothing about the port today.</summary>
public sealed class SessionPortFacts
{
    /// <summary>The app-wide facts every scan writes to and every device row reads from.</summary>
    public static SessionPortFacts Shared { get; } = new();

    private readonly ConcurrentDictionary<(string Ip, int Port), byte> Closed = new();

    /// <summary>Notes the outcome of one probe; UDP results are ignored.</summary>
    public void Record(PortResult result)
    {
        if (result.Protocol != NetProtocol.Tcp || result.Ip.Length == 0) return;
        var key = (result.Ip, result.Port);
        if (IsRefusal(result)) Closed[key] = 0;
        else Closed.TryRemove(key, out _);
    }

    /// <summary>True when a scan in this session got a reset or refusal for the port.</summary>
    public bool IsClosed(string ip, int port) => Closed.ContainsKey((ip, port));

    private static bool IsRefusal(PortResult r) =>
        r.State == PortState.Closed && r.Reason is PortReason.Rst or PortReason.RstWindow or PortReason.ConnectRefused;
}
