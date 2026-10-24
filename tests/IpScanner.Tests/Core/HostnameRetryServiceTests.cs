using System;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class HostnameRetryServiceTests
{
    [Fact]
    public async Task ResolveOneAsync_NeverRunsTwoLookupsConcurrently()
    {
        var service = new HostnameRetryService();
        int active = 0;
        int maxSeen = 0;
        var gate = new object();

        Func<Task> FakeLookup() => async () =>
        {
            lock (gate)
            {
                active++;
                if (active > maxSeen) maxSeen = active;
            }
            await Task.Delay(20);
            lock (gate) { active--; }
        };

        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = service.ResolveOneAsync(FakeLookup());
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxSeen);
        Assert.Equal(1, service.MaxObservedConcurrency);
    }

    [Fact]
    public async Task ResolveOneAsync_RunsAllScheduledWork()
    {
        var service = new HostnameRetryService();
        int completed = 0;
        var tasks = new Task[5];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = service.ResolveOneAsync(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
        await Task.WhenAll(tasks);

        Assert.Equal(5, completed);
    }
}
