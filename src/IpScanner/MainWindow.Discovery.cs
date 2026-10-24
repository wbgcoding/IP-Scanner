using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Discovery;

namespace IpScanner;

public partial class MainWindow
{
    /// <summary>True when a configured subnet or pinned entry is an IPv6 one; the IPv4 only modes grey out with a reason.</summary>
    private bool HasIpv6Scope => _config.Subnets.Concat(_config.PinnedIps).Any(e => e.Contains(':'));

    private void RefreshIpv6Capabilities()
    {
        RefreshDiscoveryRows(_config.DiscoveryModes);
        RefreshOsFingerprintRow();
    }

    /// <summary>Rebuilds the discovery mode checkboxes; modes the machine cannot run are greyed with the reason.</summary>
    private void RefreshDiscoveryRows(string configured)
    {
        var selected = new HashSet<DiscoveryMode>(DiscoveryModes.Parse(configured));
        bool wasLoading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            DiscoveryModesPanel.Children.Clear();
            foreach (var mode in DiscoveryModes.All)
            {
                var state = Capabilities.Evaluate(DiscoveryModes.CapabilityOf(mode), HasIpv6Scope);
                var box = new CheckBox
                {
                    Tag = mode,
                    Content = Loc.CapName(DiscoveryModes.CapabilityOf(mode)),
                    FontSize = 13,
                    Margin = new Thickness(0, 4, 0, 0),
                    IsChecked = selected.Contains(mode),
                    IsEnabled = state.Available,
                    Opacity = state.Available ? 1 : 0.5,
                    ToolTip = state.Available ? Loc.TipDiscovery(mode) : Loc.CapUnavailableTip(Loc.CapReasonText(state.Reason)),
                };
                box.SetResourceReference(Control.ForegroundProperty, "Text");
                ToolTipService.SetShowOnDisabled(box, true);
                System.Windows.Automation.AutomationProperties.SetName(box, (string)box.Content);
                box.Checked += OnDiscoveryModeChanged;
                box.Unchecked += OnDiscoveryModeChanged;
                DiscoveryModesPanel.Children.Add(box);
            }
        }
        finally { _loadingSettings = wasLoading; }
    }

    /// <summary>The fingerprint option needs the packet reader; without it the box is greyed and a note says what is used instead.</summary>
    private void RefreshOsFingerprintRow()
    {
        var state = Capabilities.Evaluate(Capability.OsFingerprint, HasIpv6Scope);
        OsFingerprintBox.IsEnabled = state.Available;
        OsFingerprintBox.Opacity = state.Available ? 1 : 0.5;
        OsFingerprintBox.ToolTip = state.Available ? Loc.TipOsFingerprint : Loc.CapUnavailableTip(Loc.CapReasonText(state.Reason));
        ToolTipService.SetShowOnDisabled(OsFingerprintBox, true);
        OsFingerprintNote.Visibility = state.Available ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDiscoveryModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not CheckBox changed || changed.Tag is not DiscoveryMode mode) return;
        // A list scan sends nothing, so it excludes every other mode.
        bool exclusive = changed.IsChecked == true && mode == DiscoveryMode.ListScan;
        bool others = changed.IsChecked == true && mode != DiscoveryMode.ListScan;
        _loadingSettings = true;
        try
        {
            foreach (var other in DiscoveryModesPanel.Children.OfType<CheckBox>())
            {
                if (ReferenceEquals(other, changed)) continue;
                var m = (DiscoveryMode)other.Tag;
                if (exclusive) other.IsChecked = false;
                else if (others && m == DiscoveryMode.ListScan) other.IsChecked = false;
            }
        }
        finally { _loadingSettings = false; }
        if (!DiscoveryModesPanel.Children.OfType<CheckBox>().Any(b => b.IsChecked == true)
            && DiscoveryModesPanel.Children.OfType<CheckBox>().FirstOrDefault(b => (DiscoveryMode)b.Tag == DiscoveryMode.IcmpEcho) is { } icmp)
        {
            _loadingSettings = true;
            icmp.IsChecked = true;
            _loadingSettings = false;
        }
        ApplyInstant();
    }

    /// <summary>Selected modes as config text. Disabled boxes keep their stored choice, so a mode picked while the
    /// packet reader was present survives a run without it.</summary>
    private string ReadDiscoveryModes(string current)
    {
        var boxes = DiscoveryModesPanel.Children.OfType<CheckBox>().ToList();
        if (boxes.Count == 0) return current;
        var picked = boxes.Where(b => b.IsChecked == true).Select(b => (DiscoveryMode)b.Tag).ToList();
        if (picked.Count == 0) picked.Add(DiscoveryMode.IcmpEcho);
        return DiscoveryModes.Format(picked);
    }
}
