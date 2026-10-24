using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class PingRouteTool : UserControl
{
    private readonly PingRouteToolViewModel _vm;

    public PingRouteTool(PingRouteToolViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private async void OnPingStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.Ping.StartAsync(), Loc.Tools);
    }

    private void OnPingStopClick(object sender, RoutedEventArgs e) => _vm.Ping.Stop();

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
