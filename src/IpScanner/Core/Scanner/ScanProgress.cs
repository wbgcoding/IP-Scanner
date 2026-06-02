using System.Threading;

namespace IpScanner.Core.Scanner;

public sealed class ScanProgress
{
    // long counters: a /16 or /8 scan with a high ping count can exceed int range.
    private long _success, _failed, _skipped, _processed;
    public long SuccessPings => Interlocked.Read(ref _success);
    public long FailedPings => Interlocked.Read(ref _failed);
    public long SkippedPings => Interlocked.Read(ref _skipped);
    public long ProcessedHosts => Interlocked.Read(ref _processed);

    public void AddSuccess() => Interlocked.Increment(ref _success);
    public void AddFailed() => Interlocked.Increment(ref _failed);
    public void AddSkipped(long n) => Interlocked.Add(ref _skipped, n);
    public void AddProcessed() => Interlocked.Increment(ref _processed);

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();
}
