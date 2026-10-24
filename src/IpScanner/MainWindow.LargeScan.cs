using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow
{
    /// <summary>Asks before a scan of more addresses than the hard cap; the scan may start on a worker thread.</summary>
    private bool ConfirmLargeScan(long count)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => ConfirmLargeScan(count));
        var dialog = new ArchiveConfirmDialog(Loc.LargeScanMessage(count), title: Loc.LargeScanTitle(count)) { Owner = this };
        return dialog.ShowDialog() == true;
    }
}
