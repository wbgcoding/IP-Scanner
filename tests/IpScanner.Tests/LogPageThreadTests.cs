using System;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

public sealed class LogPageThreadTests
{
    [Fact]
    public async Task OnLogAdded_FromAWorkerThread_DoesNotTouchThePageOffItsThread()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var page = new LogPage();
            var entry = new LogEntry(DateTime.Now, LogLevel.Warn, LogArea.Scan, "from a worker", null);

            // IsVisible and the timer are bound to the page's thread: reading them here would throw.
            await Task.Run(() => page.OnLogAdded(entry));

            // let the queued dispatcher callbacks run
            await page.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        });
    }
}
