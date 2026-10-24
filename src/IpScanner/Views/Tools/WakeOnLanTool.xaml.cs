using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class WakeOnLanTool : UserControl
{
    private readonly MainWindow _owner;
    private readonly WakeOnLanToolViewModel _vm;

    public WakeOnLanTool(MainWindow owner, WakeOnLanToolViewModel vm)
    {
        _owner = owner;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.InitializeAsync(_owner.Config, _owner.PersistRecentWakeTargets), Loc.WakeDevice);
    }

    private void OnKnownDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (KnownDevicesBox.SelectedItem is KnownDeviceOption device) _vm.PickKnownDevice(device);
    }

    private void OnRecentTargetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecentWakeTarget target) _vm.PickRecentTarget(target);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => _vm.SendAsync(), Loc.WakeDevice);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.Stop();
}
