using System.Threading.Tasks;
using System.Threading;
using System;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class HostnameResolverTests
{
    [Fact]
    public void StripLocaldomain_RemovesSuffix()
        => Assert.Equal("myhost", HostnameResolver.Normalize("myhost.localdomain"));

    [Fact]
    public void Normalize_KeepsPlainName()
        => Assert.Equal("router.home", HostnameResolver.Normalize("router.home"));

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ResolveAsync_HangingLookup_ReturnsNullWithinTwoAndAHalfSeconds()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var host = await HostnameResolver.ResolveAsync("192.0.2.1", CancellationToken.None,
            (ip, ct) => new TaskCompletionSource<System.Net.IPHostEntry>().Task);

        Assert.Null(host);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2.5), "took " + started.Elapsed);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTheNormalizedName()
    {
        var host = await HostnameResolver.ResolveAsync("192.0.2.1", CancellationToken.None,
            (ip, ct) => Task.FromResult(new System.Net.IPHostEntry { HostName = "printer.localdomain" }));

        Assert.Equal("printer", host);
    }

    [Fact]
    public async Task ResolveAsync_FailingOrCancelledLookup_ReturnsNull()
    {
        Assert.Null(await HostnameResolver.ResolveAsync("192.0.2.1", CancellationToken.None,
            (ip, ct) => throw new System.Net.Sockets.SocketException()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(await HostnameResolver.ResolveAsync("192.0.2.1", cancelled.Token,
            (ip, ct) => new TaskCompletionSource<System.Net.IPHostEntry>().Task));
    }

    [Fact]
    public async Task ResolveAsync_AbandonedLookupThatFaultsLater_LeavesNoUnobservedFault()
    {
        // The lookup ignores its token, so the resolver gives up while it is still running; the
        // later fault must not surface as an unobserved task exception.
        var marker = "resolver-late-fault-" + Guid.NewGuid();
        var seen = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.ToString().Contains(marker, StringComparison.Ordinal)) seen = true;
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await AbandonThenFaultAsync(marker);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally { TaskScheduler.UnobservedTaskException -= handler; }

        Assert.False(seen, "An abandoned lookup left an unobserved task fault.");
    }

    // Kept out of the test body so no local of it keeps the abandoned task reachable at collection time.
    private static async Task AbandonThenFaultAsync(string marker)
    {
        var late = new TaskCompletionSource<System.Net.IPHostEntry>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await HostnameResolver.ResolveAsync("192.0.2.1", cts.Token, (ip, ct) => late.Task));
        late.SetException(new InvalidOperationException(marker));
    }
}
