namespace IpScanner.Core.Models;

/// <param name="Method">Which discovery probe answered (null for the plain ICMP echo).</param>
public readonly record struct PingResult(bool Success, double? LatencyMs, int? Ttl, string? Method = null);
