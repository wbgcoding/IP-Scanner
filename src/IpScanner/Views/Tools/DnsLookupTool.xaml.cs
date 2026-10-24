using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class DnsLookupTool : UserControl
{
    private readonly DnsLookupToolViewModel _vm;

    public DnsLookupTool(DnsLookupToolViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.LoadDnsServers();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.StartAsync(), Loc.Tools);
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode }) _vm.Mode = mode;
    }

    private void OnAddServerClick(object sender, RoutedEventArgs e) => _vm.AddCompareServer();

    private void OnNewServerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.AddCompareServer();
        e.Handled = true;
    }

    private void OnRemoveServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string server }) _vm.RemoveCompareServer(server);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = _vm.CopyText();
        if (text.Length > 0) ClipboardHelper.TrySetText(text);
    }
}
