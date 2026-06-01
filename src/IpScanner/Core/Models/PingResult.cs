namespace IpScanner.Core.Models;

public readonly record struct PingResult(bool Success, double? LatencyMs, int? Ttl);
