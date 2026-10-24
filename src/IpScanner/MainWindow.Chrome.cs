using System.Windows;
using System.Windows.Media;
using IpScanner.Core.Localization;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Custom title bar ──
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Swaps the maximize/restore glyph and compensates the classic WPF
    /// WindowChrome maximized-overflow bug (content would otherwise clip past the
    /// screen edge and cover the taskbar).</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        var rb = SystemParameters.WindowResizeBorderThickness;
        RootLayout.Margin = maximized ? rb : new Thickness(0);

        MaximizeRestoreGlyph.Data = Geometry.Parse(maximized
            ? "M4,8H8V4H20V16H16V20H4V8M16,8V14H18V6H10V8H16M6,10V18H14V10H6Z"
            : "M4,4H20V20H4V4M6,6V18H18V6H6Z");
        System.Windows.Automation.AutomationProperties.SetName(MaximizeRestoreButton, maximized ? Loc.Restore : Loc.Maximize);
    }
}
