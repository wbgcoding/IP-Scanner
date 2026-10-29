using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class CoalescingProgressTests
{
    [Fact]
    public async Task ManyReports_ReachTheHandlerAsFewDeliveries_EndingOnTheNewestValue()
    {
        var seen = new List<int>();
        var progress = new CoalescingProgress<int>(v => { lock (seen) seen.Add(v); }, TimeSpan.FromMilliseconds(40));

        for (int i = 1; i <= 50_000; i++) progress.Report(i);
        await Task.Delay(200);
        progress.Complete();

        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.True(seen.Count < 20, $"{seen.Count} deliveries for 50000 reports");
            Assert.Equal(50_000, seen[^1]);
            Assert.Equal(seen, seen.OrderBy(x => x).ToList());
        }
    }

    [Fact]
    public void Complete_DeliversTheUnseenValueOnce_AndSilencesLaterReports()
    {
        var seen = new List<int>();
        var progress = new CoalescingProgress<int>(seen.Add, TimeSpan.FromMinutes(1));

        progress.Report(7);
        progress.Report(9);
        progress.Complete();
        progress.Report(11);
        progress.Complete();

        Assert.Equal(new[] { 9 }, seen);
    }

    [Fact]
    public async Task ReportsAfterADelivery_ScheduleANewOne()
    {
        var seen = new List<int>();
        var progress = new CoalescingProgress<int>(v => { lock (seen) seen.Add(v); }, TimeSpan.FromMilliseconds(20));

        progress.Report(1);
        await WaitForCount(seen, 1);
        progress.Report(2);
        await WaitForCount(seen, 2);

        lock (seen) Assert.Equal(new[] { 1, 2 }, seen);
    }

    // Waits for the delivery itself, not a fixed time: a loaded machine can run the timer late.
    private static async Task WaitForCount(List<int> seen, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (seen) if (seen.Count >= count) return;
            await Task.Delay(10);
        }
    }
}

public class BatchObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesOneReset_AndSwapsTheContent()
    {
        var list = new BatchObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        list.ReplaceAll(new[] { 10, 20, 30, 40 });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Equal(new[] { 10, 20, 30, 40 }, list);
    }

    [Fact]
    public void ReplaceAll_WithItsOwnSnapshot_KeepsTheItems()
    {
        var list = new BatchObservableCollection<int> { 1, 2 };
        list.ReplaceAll(new List<int>(list));
        Assert.Equal(new[] { 1, 2 }, list);
    }
}
