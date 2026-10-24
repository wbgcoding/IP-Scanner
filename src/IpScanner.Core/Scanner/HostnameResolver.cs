using System.Net;

namespace IpScanner.Core.Scanner;

public static class HostnameResolver
{
    private const int LookupTimeoutMs = 2000;

    public static string Normalize(string host)
        => host.EndsWith(".localdomain") ? host[..^".localdomain".Length] : host;

    public static Task<string?> ResolveAsync(string ip, CancellationToken ct)
        => ResolveAsync(ip, ct, (address, token) => Dns.GetHostEntryAsync(address, token));

    /// <summary>Reverse lookup with a hard two second limit; null when the name is unknown, the
    /// lookup fails, times out or is cancelled. The lookup itself is injectable for tests.</summary>
    internal static async Task<string?> ResolveAsync(string ip, CancellationToken ct,
        Func<string, CancellationToken, Task<IPHostEntry>> lookup)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LookupTimeoutMs);
        try
        {
            // The lookup may not honour its token (the resolver API can ignore it), so the wait
            // itself is also bounded by the same token.
            var pending = lookup(ip, limit.Token);
            // An abandoned lookup (timeout or cancel) can still fault later; observe it so the
            // fault never surfaces as an unobserved task exception.
            _ = pending.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var entry = await pending.WaitAsync(limit.Token).ConfigureAwait(false);
            var host = entry.HostName;
            return string.IsNullOrEmpty(host) ? null : Normalize(host);
        }
        catch (Exception ex) { Log.Debug(LogArea.Dns, "Reverse lookup failed: " + ex.GetType().Name); return null; }
    }
}
