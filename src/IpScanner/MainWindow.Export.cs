using System.IO;
using System.Threading;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow
{
    private IReadOnlyList<DeviceViewModel> ExportRows(ExportRequest request)
        => request.Rows(_vm.Devices, _devicesView.Cast<DeviceViewModel>(),
            DeviceGrid.SelectedItems.OfType<DeviceViewModel>());

    private async void OnManualExport(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var scope = Enum.TryParse<ExportScope>(_config.ManualExportScope, out var savedScope) &&
                Enum.IsDefined(savedScope) ? savedScope : ExportScope.Filtered;
            var format = Enum.TryParse<ExportFormat>(_config.ManualExportFormat, out var savedFormat) &&
                Enum.IsDefined(savedFormat) ? savedFormat : ExportFormat.Csv;
            var dialog = new ManualExportDialog(scope, format, _config.ExportPorts) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var request = new ExportRequest(dialog.Scope, dialog.Format, ExportTarget.File);
            var rows = ExportRows(request);
            if (rows.Count == 0)
            {
                ErrorDialog.Show(this, Loc.ManualExportTitle, Loc.NoExportRows);
                return;
            }

            _config.ManualExportScope = request.Scope.ToString();
            _config.ManualExportFormat = request.Format.ToString();
            PersistConfigDebounced();

            try
            {
                var devices = rows.Select(v => v.Model).ToList();
                var known = rows.GroupBy(v => v.Ip).ToDictionary(g => g.Key, g => (IReadOnlyList<NetService>)g.First().Services.ToList());
                // Probing can take a while, so it runs behind a cancellable busy notice; a stop
                // exports with the ports found so far.
                var probe = dialog.ProbeMissingPorts;
                var ports = _config.ExportPorts
                    ? probe
                        ? await BusyDialog.RunCancellableAsync(this, Loc.ArchiveWorking, token =>
                            _vm.CollectPortsForExport(devices, ip => known.GetValueOrDefault(ip), probe, token))
                        : await _vm.CollectPortsForExport(devices, ip => known.GetValueOrDefault(ip), false, CancellationToken.None)
                    : null;
                var info = NetworkDetector.DetectFast(NetworkDetector.SelectedSourceIp);
                var now = DateTime.Now;
                var timestamp = now.ToString("yyyyMMdd_HHmmss_fff");
                var displayTime = now.ToString("G", System.Globalization.CultureInfo.CurrentCulture);
                var directory = ScanConfig.ResolvePath(_config.OutputDirectory);
                var path = await Task.Run(() => request.Format switch
                {
                    ExportFormat.Txt => TxtExporter.Write(devices, info, directory, timestamp, "", displayTime, ports),
                    ExportFormat.Csv => CsvExporter.Write(devices, directory, timestamp, "", ports),
                    ExportFormat.Html => HtmlExporter.Write(devices, info, directory, timestamp, "", displayTime, ports),
                    ExportFormat.Json => JsonExporter.Write(devices, info, directory, timestamp, "", ports),
                    ExportFormat.Xml => XmlExporter.Write(devices, info, directory, timestamp, "", ports),
                    ExportFormat.Grepable => GrepableExporter.Write(devices, info, directory, timestamp, "", ports),
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Format))
                });
                _vm.RecordExport(path);
                var folder = Path.GetDirectoryName(path);
                ShowNotice(string.Format(Loc.SavedFmt, path), folder is null ? null : Loc.OpenFolder,
                    folder is null ? null : () => OpenFolderInExplorer(folder));
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Tools, "Manual export failed", ex);
                ErrorDialog.Show(this, Loc.ManualExportFailed, ex, null);
            }
        }, Loc.ManualExportTitle);
    }

    private void OnOpenCopyViewMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnCopyViewText(object sender, RoutedEventArgs e) => CopyView(ExportTarget.ClipboardText);
    private void OnCopyViewTable(object sender, RoutedEventArgs e) => CopyView(ExportTarget.ClipboardTable);

    private void CopyView(ExportTarget target)
    {
        var request = new ExportRequest(ExportScope.Filtered, ExportFormat.Txt, target);
        var rows = ExportRows(request);
        if (rows.Count == 0) { ShowNotice(Loc.NothingToCopy, null, null); return; }
        var fields = rows.Select(vm => new TxtExporter.RowFields(vm.Ip, vm.Status, vm.Hostname,
            vm.AvgDisplay, vm.MinDisplay, vm.MaxDisplay, vm.LastDisplay, vm.LossDisplay,
            vm.JitterDisplay, vm.ProgressDisplay, vm.Mac, vm.Vendor, vm.Note));
        Copy(target == ExportTarget.ClipboardTable
            ? TxtExporter.FormatRowsAsMarkdown(fields) : TxtExporter.FormatRowsAsText(fields),
            Loc.RowsCopied(rows.Count));
    }
}
