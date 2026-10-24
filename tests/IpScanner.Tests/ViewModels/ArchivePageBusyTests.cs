using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests.ViewModels;

[Collection("Localization")]
public class ArchivePageBusyTests
{
    [Fact]
    public async Task Mutation_DisablesTheManagementPanelWhileItRuns_AndRestoresItAfterwards()
    {
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();

        var snapshot = await WpfTestHost.RunAsync(async () =>
        {
            var page = new ArchivePage();
            await page.OpenAsync(new ScanConfig { DatabasePath = dbPath });
            var enabledBefore = page.ManagementPanel.IsEnabled;

            var mutation = page.MutateAsync(_ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new object[] { 1 };
            }, "{0}");
            var enabledDuring = page.ManagementPanel.IsEnabled;
            var statusDuring = page.StatusText.Text;
            await Task.Run(() => started.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            await mutation;
            return (enabledBefore, enabledDuring, statusDuring, enabledAfter: page.ManagementPanel.IsEnabled);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(snapshot.enabledBefore);
        Assert.False(snapshot.enabledDuring);
        Assert.Equal(Loc.ArchiveWorking, snapshot.statusDuring);
        Assert.True(snapshot.enabledAfter);
    }
}
