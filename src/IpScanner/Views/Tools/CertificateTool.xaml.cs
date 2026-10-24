using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class CertificateTool : UserControl
{
    private readonly CertificateToolViewModel _vm;

    public CertificateTool(CertificateToolViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.StartAsync(), Loc.Tools);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.Stop();
}
