using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class PortCheckTool : UserControl
{
    private readonly MainWindow _owner;
    private readonly PortCheckToolViewModel _vm;

    public PortCheckTool(MainWindow owner, PortCheckToolViewModel vm)
    {
        _owner = owner;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Initialize(owner.Config);
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.StartAsync((outside, ports) => FullPortScanSafetyGate.Shared.Confirm(outside, () =>
        {
            var dialog = new FullPortScanWarningDialog(outside, ports) { Owner = _owner };
            return dialog.ShowDialog() == true;
        })), Loc.Tools);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Stop();

    /// <summary>Intrusive checks reach beyond the target, so ticking them asks first. Declining unticks the box.</summary>
    private void OnIntrusiveClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CheckIntrusive && !ConfirmIntrusive()) _vm.CheckIntrusive = false;
    }

    private bool ConfirmIntrusive() => FullPortScanSafetyGate.SharedChecks.Confirm(_vm.OutsideSubnet, () =>
    {
        var dialog = new IntrusiveCheckWarningDialog(_vm.OutsideSubnet) { Owner = _owner };
        return dialog.ShowDialog() == true;
    });

    private async void OnRunChecksClick(object sender, RoutedEventArgs e)
    {
        // An outside target never gets the broadcast check, so the warning only matters inside the own networks.
        if (_vm.CheckIntrusive && !_vm.OutsideSubnet && !ConfirmIntrusive()) _vm.CheckIntrusive = false;
        await UiGuard.Run(() => _vm.RunChecksAsync(), Loc.Tools);
    }

    private void OnStopChecksClick(object sender, RoutedEventArgs e) => _vm.StopChecks();

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string scope }) return;
        _vm.Scope = scope;
        RangeRow.Visibility = scope == PortCheckToolViewModel.ScopeRange ? Visibility.Visible : Visibility.Collapsed;
        PortListBox.Visibility = scope == PortCheckToolViewModel.ScopeList ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ResolvedIp is not { } ip) return;
        if ((sender as DataGrid)?.SelectedItem is not PortResult { IsOpen: true, Service: { } service }) return;
        PortScanner.Open(ip, service);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CopyText() is { Length: > 0 } text) ClipboardHelper.TrySetText(text);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var format = _vm.Format;
        string ext = ReportFormats.Extension(format);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.ScanToolSaveTitle,
            Filter = $"{ReportFormats.Name(format)} (*.{ext})|*.{ext}",
            FileName = "ports." + ext,
            DefaultExt = ext,
            OverwritePrompt = true,
        };
        ToolSaveDialog.ApplyInitialDirectory(dlg, _owner.Config);
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await UiGuard.Run(() => _vm.SaveAsync(dlg.FileName, format), Loc.Tools);
    }
}
