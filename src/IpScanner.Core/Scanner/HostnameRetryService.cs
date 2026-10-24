namespace IpScanner.Core.Scanner;

/// <summary>Bounds the background hostname-retry pass to one lookup in
/// flight at a time so a long backlog of nameless devices cannot flood
/// DNS/mDNS/NetBIOS all at once. A plain gate (not per-device) is enough since
/// the caller already iterates its candidate devices one at a time.</summary>
public sealed class HostnameRetryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _active;

    /// <summary>Highest number of concurrent <see cref="ResolveOneAsync"/> bodies
    /// observed since construction - always 0 or 1 by design; exposed for tests.</summary>
    public int MaxObservedConcurrency { get; private set; }

    /// <summary>Runs <paramref name="work"/> for one device, waiting for any
    /// lookup already in flight to finish first.</summary>
    public async Task ResolveOneAsync(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int n = Interlocked.Increment(ref _active);
            if (n > MaxObservedConcurrency) MaxObservedConcurrency = n;
            await work().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _gate.Release();
        }
    }
}
