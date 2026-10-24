using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using Microsoft.Win32;

namespace IpScanner.Views;

public partial class ArchivePage
{
    private ScanCompareResult? _compareResult;
    private int _compareVersion;
    private bool _comparing;

    private sealed record RunOption(ArchivedScan Scan)
    {
        public string Label => string.Join(" \u00B7 ", Scan.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
            NetworkSummary(Scan), $"{Scan.DeviceCount} {Loc.Devices}");
        public override string ToString() => Label;
    }

    private sealed record CompareRow(ScanChange Change)
    {
        /// <summary>The change type by name; the Change column colors itself from it.</summary>
        public string Type => Change.Type.ToString();
        public string TypeLabel => ScanCompareExport.TypeLabel(Change.Type);
        public string Ip => Change.Ip;
        public string? Hostname => Change.Hostname;
        public string? Before => Change.Before;
        public string? After => Change.After;
    }

    private async void OnToggleCompare(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_comparing) { SetCompareMode(false); return; }
            SetCompareMode(true);
            FillRunPickers();
            await RecomputeCompareAsync();
        }, Loc.ArchiveTitle);
    }

    private void SetCompareMode(bool on)
    {
        _comparing = on;
        MainArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CompareArea.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ManagementRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CompareButton.Content = on ? Loc.CompareBack : Loc.CompareButton;
        if (!on) ++_compareVersion;
    }

    private void FillRunPickers()
    {
        var options = _scans.Select(x => new RunOption(x)).ToArray();
        var enough = options.Length >= 2;
        CompareEmptyText.Visibility = enough ? Visibility.Collapsed : Visibility.Visible;
        CompareEmptyText.Text = string.Format(CultureInfo.CurrentCulture, Loc.CompareNeedTwoRunsFmt, options.Length);
        foreach (var element in new UIElement[] { ComparePickers, CompareActions, CompareFrame, CompareGrid })
            element.Visibility = enough ? Visibility.Visible : Visibility.Collapsed;
        _compareResult = null;
        SetCompareHint("");
        CompareRunABox.ItemsSource = options;
        CompareRunBBox.ItemsSource = options;
        if (!enough) return;
        // Runs are listed newest first: B is the newest run, A the one before it.
        CompareRunBBox.SelectedIndex = 0;
        CompareRunABox.SelectedIndex = 1;
    }

    private async void OnCompareInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_comparing) return;
        await UiGuard.Run(RecomputeCompareAsync, Loc.ArchiveTitle);
    }

    private ScanCompareOptions? ReadOptions()
    {
        var culture = CultureInfo.CurrentCulture;
        if (!double.TryParse(ComparePercentBox.Text, NumberStyles.Float, culture, out var percent) || percent < 0) return null;
        if (!double.TryParse(CompareMinMsBox.Text, NumberStyles.Float, culture, out var minMs) || minMs < 0) return null;
        return new ScanCompareOptions(percent, minMs);
    }

    private async Task RecomputeCompareAsync()
    {
        if (_config is null || !_comparing) return;
        if (CompareRunABox.SelectedItem is not RunOption a || CompareRunBBox.SelectedItem is not RunOption b) return;
        var options = ReadOptions();
        if (options is null) { ShowInvalidThresholds(); return; }
        int version = ++_compareVersion;
        try
        {
            var path = _config.DatabasePath;
            var result = await Task.Run(() =>
            {
                var db = new KnownDevicesDb(path);
                var da = db.LoadScanArchive(a.Scan.Id);
                var dbb = db.LoadScanArchive(b.Scan.Id);
                return da is null || dbb is null ? null : ScanCompare.Compute(da, dbb, options);
            });
            if (version != _compareVersion || result is null) return;
            ShowCompare(result);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>A threshold box holds no usable number: drop the stale result so nothing wrong can be exported.</summary>
    private void ShowInvalidThresholds()
    {
        _compareVersion++;
        _compareResult = null;
        CompareGrid.ItemsSource = null;
        CompareSummaryText.Text = "";
        SetCompareHint(Loc.CompareThresholdInvalid);
        CompareCopyButton.IsEnabled = CompareJsonButton.IsEnabled = CompareTextButton.IsEnabled = false;
    }

    private void SetCompareHint(string text)
    {
        CompareHintText.Text = text;
        CompareHintText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCompare(ScanCompareResult result)
    {
        _compareResult = result;
        var rows = result.Changes.Select(c => new CompareRow(c)).ToArray();
        CompareGrid.ItemsSource = rows;
        CompareGrid.SelectedIndex = -1;
        CompareSummaryText.Text = rows.Length == 0
            ? Loc.CompareNoChanges
            : string.Format(CultureInfo.CurrentCulture, Loc.CompareSummaryFmt, rows.Length);
        SetCompareHint(result.PortsCompared ? "" : Loc.ComparePortsHint);
        CompareCopyButton.IsEnabled = CompareJsonButton.IsEnabled = CompareTextButton.IsEnabled = true;
    }

    private void OnCompareCopy(object sender, RoutedEventArgs e)
    {
        if (_compareResult is null) return;
        try { Clipboard.SetText(ScanCompareExport.ToText(_compareResult)); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Core.Log.Warn(Core.LogArea.Ui, "Copying the comparison failed", ex);
            StatusText.Text = Loc.ArchiveUnexpectedError;
        }
    }

    private void OnCompareSaveJson(object sender, RoutedEventArgs e)
        => SaveCompare(ReportFormat.Json);

    private void OnCompareSaveText(object sender, RoutedEventArgs e)
        => SaveCompare(ReportFormat.Text);

    /// <summary>The file dialog lists every report format; the buttons only preselect one.</summary>
    private void SaveCompare(ReportFormat preselected)
    {
        if (_compareResult is null) return;
        try
        {
            var formats = ReportFormats.All;
            var dialog = new SaveFileDialog
            {
                Filter = string.Join("|", formats.Select(f => $"{ReportFormats.Name(f)} (*.{ReportFormats.Extension(f)})|*.{ReportFormats.Extension(f)}")),
                FilterIndex = formats.ToList().IndexOf(preselected) + 1,
                DefaultExt = ReportFormats.Extension(preselected),
                FileName = $"compare-{_compareResult.A.StartedAt.LocalDateTime:yyyyMMdd-HHmm}-{_compareResult.B.StartedAt.LocalDateTime:yyyyMMdd-HHmm}.{ReportFormats.Extension(preselected)}"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var format = formats[Math.Clamp(dialog.FilterIndex - 1, 0, formats.Count - 1)];
            File.WriteAllText(dialog.FileName, ScanCompareExport.Render(_compareResult, format), new UTF8Encoding(false));
            StatusText.Text = Loc.CompareSaved;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>Surface tool: selects a run and one of its devices, as a click would.</summary>
    internal async Task ReviewSelectAsync(int run, int device)
    {
        RunsGrid.SelectedIndex = run;
        await Task.Delay(400);
        if (DevicesGrid.Items.Count > device) DevicesGrid.SelectedIndex = device;
    }

    /// <summary>Surface tool: opens the comparison view.</summary>
    internal async Task ReviewShowCompareAsync()
    {
        SetCompareMode(true);
        FillRunPickers();
        await RecomputeCompareAsync();
    }
}
