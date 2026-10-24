using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class CalculatorTool : UserControl
{
    private readonly CalculatorToolViewModel _vm;

    public CalculatorTool(CalculatorToolViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private async void OnArpLookupClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.LookupArpAsync(), Loc.Tools);
    }
}
