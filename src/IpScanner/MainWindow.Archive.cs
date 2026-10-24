using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow
{
    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            ArchiveScroll.Visibility = Visibility.Visible;
            ArchiveOverlay.CloseRequested -= OnArchiveCloseRequested;
            ArchiveOverlay.CloseRequested += OnArchiveCloseRequested;
            ArchiveOverlay.StartScanRequested -= OnArchiveStartScanRequested;
            ArchiveOverlay.StartScanRequested += OnArchiveStartScanRequested;
            ArchiveOverlay.BackupNoticeRequested -= OnArchiveBackupNotice;
            ArchiveOverlay.BackupNoticeRequested += OnArchiveBackupNotice;
            await ArchiveOverlay.OpenAsync(_config);
            // Filling the run list brings it into view, which scrolls the title and filters away on a short window.
            await Dispatcher.InvokeAsync(ArchiveScroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
        }, Loc.ArchiveTitle);
    }

    private void OnArchiveBackupNotice(object? sender, ArchiveBackupNotice notice)
    {
        var folder = notice.Folder;
        ShowNotice(notice.Text, folder is null ? null : Loc.OpenFolder,
            folder is null ? null : () => OpenFolderInExplorer(folder));
    }

    private void OnArchiveCloseRequested(object? sender, EventArgs e)
    {
        ArchiveScroll.Visibility = Visibility.Collapsed;
        ArchiveButton.Focus();
    }

    private async void OnArchiveStartScanRequested(object? sender, EventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            OnArchiveCloseRequested(sender, e);
            if (!_vm.IsScanning) await StartScan();
        }, Loc.ArchiveTitle);
    }
}
