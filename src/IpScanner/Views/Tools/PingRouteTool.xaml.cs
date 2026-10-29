using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class PingRouteTool : UserControl
{
    private readonly ScanConfig _config;
    private readonly PingRouteToolViewModel _vm;

    public PingRouteTool(ScanConfig config, PingRouteToolViewModel vm)
    {
        _config = config;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private async void OnPingStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.Ping.StartAsync(), Loc.Tools);
    }

    private void OnPingStopClick(object sender, RoutedEventArgs e) => _vm.Ping.Stop();

    private async void OnPingSaveClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.ScanToolSaveButton,
            Filter = Loc.DbCsvFileFilter,
            FileName = "ping_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
            DefaultExt = "csv",
            OverwritePrompt = true,
        };
        ToolSaveDialog.ApplyInitialDirectory(dlg, _config);
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await UiGuard.Run(() => _vm.Ping.SaveAsync(dlg.FileName), Loc.Tools);
    }

    private void OnPingGraphSizeChanged(object sender, SizeChangedEventArgs e) => _vm.Ping.SetGraphWidth(e.NewSize.Width);

    private async void OnRouteStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.Route.StartAsync(), Loc.Tools);
    }

    private void OnRouteStopClick(object sender, RoutedEventArgs e) => _vm.Route.Stop();

    private void OnRouteCopyClick(object sender, RoutedEventArgs e)
    {
        var text = string.Join(System.Environment.NewLine, _vm.Route.Hops.Select(h => h.DisplayLine));
        if (text.Length > 0) ClipboardHelper.TrySetText(text);
    }
}
