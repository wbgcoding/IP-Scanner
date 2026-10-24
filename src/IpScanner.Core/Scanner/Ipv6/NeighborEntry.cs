using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>State of a neighbour cache entry, numbered like the Windows NL_NEIGHBOR_STATE.</summary>
public enum NeighborState
{
    Unreachable = 0,
    Incomplete = 1,
    Probe = 2,
    Delay = 3,
    Stale = 4,
    Reachable = 5,
    Permanent = 6,
}

/// <summary>One IPv6 neighbour cache row: the address (zone = interface index), its MAC when resolved,
/// the interface and whether the neighbour announced itself as a router.</summary>
public sealed record NeighborEntry(
    Ipv6Address Address, string? Mac, int InterfaceIndex, NeighborState State, bool IsRouter);

/// <summary>The system IPv6 neighbour cache; a seam so discovery can be tested without a network.</summary>
public interface INeighborTable
{
    IReadOnlyList<NeighborEntry> Read();
}
