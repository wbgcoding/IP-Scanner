using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

/// <summary>Shared page of the scan tools. The view model decides which options show and runs the scan.</summary>
public partial class ScanToolPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly ScanToolViewModel _vm;

    public ScanToolPage(MainWindow owner, ScanToolViewModel vm)
    {
        _owner = owner;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Initialize(owner.Config);
        PresetBox.Visibility = Show(vm.HasPreset);
        TimingBox.Visibility = Show(vm.HasTiming);
        ScopeBox.Visibility = Show(vm.HasPortScope);
        SkipBox.Visibility = Show(vm.HasSkipDiscovery);
        ChecksBox.Visibility = Show(vm.HasChecks);
        SaveRow.Visibility = Show(vm.HasSavePath);
        var hasNote = false;
        if (vm is ScanExportToolViewModel)
        {
            PresetCombo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, new Binding(nameof(ScanExportToolViewModel.PresetIndex)));
            FooterFormat.Visibility = Visibility.Collapsed;
        }
        if (vm is OsEstimateToolViewModel { Note: { } note })
        {
            NoteText.Text = note;
            NoteText.Visibility = Visibility.Visible;
            hasNote = true;
        }
        // Without any option the panel would only add empty space above the table.
        OptionsPanel.Visibility = Show(vm.HasPreset || vm.HasTiming || vm.HasPortScope || vm.HasSkipDiscovery
                                       || vm.HasChecks || vm.HasSavePath || hasNote);
        UpdateTexts();
        BuildColumns();
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateTexts()
        => StartButton.Content = _vm.Kind == ScanToolKind.Export ? Loc.ScanToolExportRun : Loc.ScanToolStartButton;

    /// <summary>Rebuilds the translated lists and column headers after a live language switch.</summary>
    public void RefreshLanguage()
    {
        _vm.ReloadLabels();
        UpdateTexts();
        BuildColumns();
    }

    private void BuildColumns()
    {
        ResultGrid.Columns.Clear();
        ResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.IpAddress,
            Binding = new Binding(nameof(ScanToolRow.Ip)),
            Width = new DataGridLength(130),
        });
        var headers = _vm.ColumnHeaders;
        string[] paths = [nameof(ScanToolRow.C1), nameof(ScanToolRow.C2), nameof(ScanToolRow.C3), nameof(ScanToolRow.C4)];
        for (int i = 0; i < headers.Count && i < paths.Length; i++)
        {
            ResultGrid.Columns.Add(new DataGridTextColumn
            {
                Header = headers[i],
                Binding = new Binding(paths[i]),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 90,
            });
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
        => await UiGuard.Run(() => _vm.StartAsync(Confirm), Loc.Tools);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Stop();

    private void OnIntrusiveClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CheckIntrusive && !ConfirmIntrusive(outside: false)) _vm.CheckIntrusive = false;
    }

    private bool ConfirmIntrusive(bool outside) => FullPortScanSafetyGate.SharedChecks.Confirm(outside, () =>
    {
        var dialog = new IntrusiveCheckWarningDialog(outside) { Owner = _owner };
        return dialog.ShowDialog() == true;
    });

    /// <summary>Asks once for each reason the run needs a confirmation; any no stops the run.</summary>
    private bool Confirm(ScanToolGate gate)
    {
        if (gate.Large)
        {
            var dialog = new ArchiveConfirmDialog(string.Format(Loc.ScanToolLargeFmt, gate.Count), null, false, Loc.LargeScanTitle(gate.Count)) { Owner = _owner };
            if (dialog.ShowDialog() != true) return false;
        }
        if (gate.FullRange)
        {
            bool ok = FullPortScanSafetyGate.Shared.Confirm(gate.Outside, () =>
            {
                var dialog = new FullPortScanWarningDialog(gate.Outside, gate.PortCount) { Owner = _owner };
                return dialog.ShowDialog() == true;
            });
            if (!ok) return false;
        }
        else if (gate.Outside)
        {
            var dialog = new ArchiveConfirmDialog(Loc.FullPortScanOutsideNote, null, false, Loc.PortCheckOutsideSubnet) { Owner = _owner };
            if (dialog.ShowDialog() != true) return false;
        }
        return !gate.Intrusive || ConfirmIntrusive(gate.Outside);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CopyText() is { Length: > 0 } text) ClipboardHelper.TrySetText(text);
    }

    private string? PickFile()
    {
        var format = _vm.Format;
        string ext = ReportFormats.Extension(format);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.ScanToolSaveTitle,
            Filter = $"{ReportFormats.Name(format)} (*.{ext})|*.{ext}",
            FileName = "scan." + ext,
            DefaultExt = ext,
            OverwritePrompt = true,
        };
        ToolSaveDialog.ApplyInitialDirectory(dlg, _owner.Config);
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (PickFile() is not { } path) return;
        await UiGuard.Run(() => _vm.SaveAsync(path, _vm.Format), Loc.Tools);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (PickFile() is { } path) SavePathBox.Text = path;
    }

    private void OnSavePathChanged(object sender, TextChangedEventArgs e)
        => _vm.SavePath = string.IsNullOrWhiteSpace(SavePathBox.Text) ? null : SavePathBox.Text.Trim();
}
