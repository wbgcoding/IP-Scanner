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
        if (_toolsWindow is not null) { _toolsWindow.Activate(); return; }
        _toolsWindow = new ToolsWindow(this) { Owner = this };
        _toolsWindow.Closed += (_, _) => _toolsWindow = null;
        _toolsWindow.Show();
    }
}
