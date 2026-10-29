using System.Windows;

namespace IpScanner;

/// <summary>Remembers whether the main window was maximized before it was minimized, so a restore
/// from the tray brings it back the way it was instead of always as a normal window.</summary>
internal sealed class TrayWindowState
{
    private WindowState _lastShown = WindowState.Normal;

    /// <summary>Feed every window state change; minimized states are not remembered.</summary>
    public void Observe(WindowState state)
    {
        if (state != WindowState.Minimized) _lastShown = state;
    }

    /// <summary>The state to restore to: maximized when it was maximized, otherwise normal.</summary>
    public WindowState RestoreTarget => _lastShown == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
}
