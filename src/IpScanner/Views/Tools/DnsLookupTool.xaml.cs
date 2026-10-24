using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var lines = _vm.ShowReverseResults
            ? new[] { _vm.CanonicalName ?? "" }.Concat(_vm.Aliases)
            : _vm.Addresses.AsEnumerable();
        var text = string.Join(System.Environment.NewLine, lines);
        if (text.Length > 0) ClipboardHelper.TrySetText(text);
    }
}
