using System.Windows;
using IpScanner.Core.Models;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Tools window ──
    private ToolsWindow? _toolsWindow;

    internal ScanConfig Config => _config;

    /// <summary>Written directly and persisted immediately, same as OnSettingsNavChanged
    /// does for SettingsPage - ReadSettings() only carries the value over.</summary>
    internal void PersistToolsLastPage(string tag)
    {
        _config.ToolsLastPage = tag;
        PersistConfigDebounced();
    }

    /// <summary>The Wake-on-LAN tool's new recent-target list: stored on the live config (the tool
    /// may hold an older config object after a settings edit) and written to the sealed settings.</summary>
    internal void PersistRecentWakeTargets(List<string> recent)
    {
        _config.RecentWakeTargets = recent;
        PersistConfigDebounced();
    }

    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        var window = OpenToolsWindow();
        var selected = DeviceGrid.SelectedItem as ViewModels.DeviceViewModel;
        if (selected is not null) window.PrefillDevice(selected.Ip, selected.Model.Mac);
    }

    /// <summary>The one tools window: created on first use, brought to the front afterwards.</summary>
    private ToolsWindow OpenToolsWindow()
    {
        if (_toolsWindow is not null) { _toolsWindow.Activate(); return _toolsWindow; }
        var window = new ToolsWindow(this) { Owner = this };
        _toolsWindow = window;
        window.Closed += (_, _) => _toolsWindow = null;
        window.Show();
        return window;
    }

    /// <summary>Context menu: opens the ping page on this device and starts the continuous ping.</summary>
    private async void OnDeviceContinuousPing(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (MenuVm(sender) is not { } vm) return;
            var window = OpenToolsWindow();
            window.PrefillDevice(vm.Ip, vm.Model.Mac);
            await window.StartPingForAsync(vm.Ip);
        }, Core.Localization.Loc.Tools);
    }

    /// <summary>Context menu: opens the ping page on this device and starts the route trace.</summary>
    private async void OnDeviceTraceRoute(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (MenuVm(sender) is not { } vm) return;
            var window = OpenToolsWindow();
            window.PrefillDevice(vm.Ip, vm.Model.Mac);
            await window.StartRouteForAsync(vm.Ip);
        }, Core.Localization.Loc.Tools);
    }
}
