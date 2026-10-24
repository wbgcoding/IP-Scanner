using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;

namespace IpScanner;

// Packet reader notice bar and the capability matrix on the Help page.
public partial class MainWindow
{
    private bool _driverBarDismissed;
    private CancellationTokenSource? _driverWatch;

    /// <summary>Shows the notice while the packet reader is missing or unusable, hidden after a manual close until restart.</summary>
    private void RefreshDriverBar()
    {
        var status = PacketDriver.Status;
        DriverBarMessage.Text = Loc.DriverBarFor(status.Reason);
        DriverBar.Visibility = status.Available || _driverBarDismissed ? Visibility.Collapsed : Visibility.Visible;
        BuildDriverHelp();
        RefreshDiscoveryRows(_config.DiscoveryModes);
        RefreshOsFingerprintRow();
    }

    private async void OnDriverGet(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PacketDriver.DownloadUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Opening the browser failed", ex); }
        try { if (!PacketDriver.Status.Available) await WatchDriverInstallAsync(); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Watching for the packet reader failed", ex); }
    }

    /// <summary>Waits (2 s steps, up to 10 minutes, until the window closes) for the packet reader to
    /// show up, then unlocks everything that needs it without a restart.</summary>
    private async Task WatchDriverInstallAsync()
    {
        // A superseded watch is only cancelled here; it disposes its own source when its wait ends.
        _driverWatch?.Cancel();
        var cts = _driverWatch = new CancellationTokenSource();
        Closed -= StopDriverWatch;
        Closed += StopDriverWatch;
        bool found;
        try { found = await PacketDriver.WaitForInstallAsync(cts.Token); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Watching for the packet reader failed", ex); return; }
        finally
        {
            if (ReferenceEquals(_driverWatch, cts)) _driverWatch = null;
            cts.Dispose();
        }
        if (!found) return;
        RawTransportProvider.RegisterDriverTransport();
        RefreshDriverBar();
        _toolsWindow?.RefreshDriver();
    }

    private void StopDriverWatch(object? sender, EventArgs e) => _driverWatch?.Cancel();

    private void OnDriverDetails(object sender, RoutedEventArgs e)
    {
        OnSettingsClick(this, e);
        SettingsNavList.SelectedIndex = Array.IndexOf(SettingsPages, "Help");
    }

    private void OnDriverClose(object sender, RoutedEventArgs e)
    {
        _driverBarDismissed = true;
        DriverBar.Visibility = Visibility.Collapsed;
    }

    private static TextBlock HelpText(string text, string brushKey, double size, FontWeight? weight = null, Thickness? margin = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        if (weight != null) tb.FontWeight = weight.Value;
        return tb;
    }

    /// <summary>(Re)builds the packet reader status and the scan type matrix; called again on a language change.</summary>
    private void BuildDriverHelp()
    {
        var panel = DriverHelpPanel;
        panel.Children.Clear();
        var status = PacketDriver.Status;

        panel.Children.Add(HelpText(Loc.DriverHelpTitle, "Mauve", 15, FontWeights.Bold));
        string state = status.Available ? Loc.DriverInstalledVersion(status.Version)
            : status.Reason == PacketDriver.ReasonAdmin ? Loc.DriverNeedsAdmin
            : status.Reason == PacketDriver.ReasonStopped ? Loc.DriverStopped : Loc.DriverNotInstalled;
        if (status.Available && status.Version.Length == 0) state = Loc.DriverInstalled;
        panel.Children.Add(HelpText(Loc.DriverLabel + ": " + state, "Text", 13, FontWeights.SemiBold, new Thickness(0, 10, 0, 0)));
        panel.Children.Add(HelpText(Loc.DriverBarFor(status.Reason), "Subtext", 12, null, new Thickness(0, 6, 0, 0)));

        var get = new Button
        {
            Content = Loc.DriverGet, ToolTip = Loc.TipDriverGet, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0),
        };
        get.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        get.Click += OnDriverGet;
        panel.Children.Add(get);

        panel.Children.Add(HelpText(Loc.CapMatrixTitle, "Mauve", 15, FontWeights.Bold, new Thickness(0, 22, 0, 0)));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 170 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 110 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        void Add(UIElement el, int col)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); grid.Children.Add(el);
        }
        void NewRow() => grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        NewRow();
        Add(HelpText(Loc.CapColType, "Subtext", 12, FontWeights.Bold, new Thickness(0, 0, 12, 7)), 0);
        Add(HelpText(Loc.CapColStatus, "Subtext", 12, FontWeights.Bold, new Thickness(0, 0, 12, 7)), 1);
        Add(HelpText(Loc.CapColReason, "Subtext", 12, FontWeights.Bold, new Thickness(0, 0, 0, 7)), 2);
        row++;

        foreach (var group in Enum.GetValues<CapabilityGroup>())
        {
            NewRow();
            var header = HelpText(Loc.CapGroupName(group), "Mauve", 12, FontWeights.SemiBold, new Thickness(0, 10, 0, 4));
            Grid.SetColumnSpan(header, 3);
            Add(header, 0);
            row++;
            foreach (var id in Capabilities.All.Where(c => Capabilities.GroupOf(c) == group))
            {
                var st = Capabilities.Evaluate(id);
                NewRow();
                Add(HelpText(Loc.CapName(id), "Text", 13, null, new Thickness(0, 3, 12, 3)), 0);
                Add(HelpText(st.Available ? Loc.CapAvailable : Loc.CapUnavailable, st.Available ? "Green" : "Subtext", 13, null, new Thickness(0, 3, 12, 3)), 1);
                Add(HelpText(st.Available ? "" : Loc.CapReasonText(st.Reason), "Subtext", 13, null, new Thickness(0, 3, 0, 3)), 2);
                row++;
            }
        }
        panel.Children.Add(grid);
        BuildReferenceHelp();
    }
}
