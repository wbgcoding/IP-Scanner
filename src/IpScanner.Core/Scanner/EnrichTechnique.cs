namespace IpScanner.Core.Scanner;

/// <summary>Adapters for MAC/hostname techniques handed to <see cref="ScanEngine"/>.</summary>
public static class EnrichTechnique
{
    /// <summary>Wraps a blocking technique so it runs on the pool and is skipped once its token is
    /// already cancelled.</summary>
    public static Func<string, CancellationToken, Task<(string? mac, string? host)>> FromSync(
        Func<string, (string? mac, string? host)> technique)
        => (ip, ct) => Task.Run(() => technique(ip), ct);
}
