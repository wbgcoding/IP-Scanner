using System.Threading;

namespace IpScanner.Core.Scanner;

public sealed class ScanProgress
{
    private int _success, _failed, _skipped, _processed;
    public int SuccessPings => Volatile.Read(ref _success);
    public int FailedPings => Volatile.Read(ref _failed);
    public int SkippedPings => Volatile.Read(ref _skipped);
    public int ProcessedHosts => Volatile.Read(ref _processed);

    public void AddSuccess() => Interlocked.Increment(ref _success);
    public void AddFailed() => Interlocked.Increment(ref _failed);
    public void AddSkipped(int n) => Interlocked.Add(ref _skipped, n);
    public void AddProcessed() => Interlocked.Increment(ref _processed);

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();
}
