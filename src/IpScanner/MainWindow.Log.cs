using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Views;

namespace IpScanner;

/// <summary>The log viewer overlay, opened from the header button next to Archive.</summary>
public partial class MainWindow
{
    private async void OnLogClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            ArchiveScroll.Visibility = Visibility.Collapsed;
            LogScroll.Visibility = Visibility.Visible;
            LogOverlay.CloseRequested -= OnLogCloseRequested;
            LogOverlay.CloseRequested += OnLogCloseRequested;
            await LogOverlay.OpenAsync();
        }, Loc.LogTitle);
    }

    private void OnLogCloseRequested(object? sender, EventArgs e)
    {
        LogScroll.Visibility = Visibility.Collapsed;
        LogButton.Focus();
    }

    // Settings and the archive page open over the same area; the log page steps aside for either.
    private void OnOtherPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && LogScroll.Visibility == Visibility.Visible)
            LogScroll.Visibility = Visibility.Collapsed;
    }
}
