using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;
using IpScanner.Views.Tools;

namespace IpScanner.Views;

/// <summary>Non-modal toolbox window: a left-hand tool list plus a lazily
/// built page per tool on the right, kept open next to the main window. Each page is
/// built once on first selection and stays alive afterwards, so a running tool keeps
/// its state when you switch away and back.</summary>
public partial class ToolsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly ToolsViewModel _vm = new();
    private readonly Dictionary<string, FrameworkElement> _pages = new();

    public ToolsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        WindowTheme.Apply(this, dark: !ThemeManager.IsLight);
        ApplyScaleAndFlow(_owner.Config.UiScalePercent / 100.0, Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);

        string last = _owner.Config.ToolsLastPage;
        foreach (ListBoxItem item in ToolsNavList.Items)
        {
            if (Equals(item.Tag, last)) { item.IsSelected = true; break; }
        }
        if (ToolsNavList.SelectedItem is null) ToolsNavList.SelectedIndex = 0;
    }

    /// <summary>Re-applies the DWM dark/light title-bar frame after a live theme
    /// switch from the main window's Settings overlay (the page's
    /// own colors already follow via DynamicResource, only this native call does not).</summary>
    public void RefreshTheme() => WindowTheme.Apply(this, dark: !ThemeManager.IsLight);

    /// <summary>Layout transform and flow direction for a text-size factor and reading direction.</summary>
    internal static (Transform? Transform, FlowDirection Flow) ScaleAndFlow(double factor, bool rtl)
    {
        double f = Math.Clamp(factor, 0.5, 2.0);
        return (f == 1.0 ? null : new ScaleTransform(f, f),
            rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
    }

    internal const double BaseMinWidth = 820;
    internal const double BaseMinHeight = 560;

    /// <summary>Minimum window size for a text-size factor: the base minimum times the
    /// factor, never larger than the work area.</summary>
    internal static (double Width, double Height) ScaledMinSize(double factor, double workWidth, double workHeight)
    {
        double f = Math.Clamp(factor, 0.5, 2.0);
        return (Math.Min(BaseMinWidth * f, workWidth), Math.Min(BaseMinHeight * f, workHeight));
    }

    /// <summary>Follows the main window's text size and reading direction.</summary>
    public void ApplyScaleAndFlow(double factor, FlowDirection flow)
    {
        var (transform, _) = ScaleAndFlow(factor, flow == FlowDirection.RightToLeft);
        RootLayout.LayoutTransform = transform;
        FlowDirection = flow;
        var work = SystemParameters.WorkArea;
        (MinWidth, MinHeight) = ScaledMinSize(factor, work.Width, work.Height);
    }

    /// <summary>Unlocks the scan types that need the packet driver once it was installed while the window is open.</summary>
    public void RefreshDriver() => _vm.PortCheck.RefreshScanTypes();

    /// <summary>Re-raises any cached, language-dependent tool text after a live
    /// language switch (the vendor-lookup footnote) - see docs/DESIGN.md's
    /// "Loc property read once into a field" rule.</summary>
    public void RefreshLanguage()
    {
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _vm.Calculator.RefreshLanguage();
        _vm.Mac.RefreshLanguage();
        _vm.DnsLookup.RefreshLanguage();
        foreach (var page in _pages.Values)
            if (page is ScanToolPage scan) scan.RefreshLanguage();
        if (ToolsNavList.SelectedItem is ListBoxItem { Tag: string tag }) ToolDescription.Text = DescFor(tag);
    }

#if DEBUG
    internal ToolsViewModel ReviewVm => _vm;
#endif

    private static string DescFor(string tag) => tag switch
    {
        "WakeOnLan" => Loc.ToolWolDesc,
        "PortCheck" => Loc.ToolPortCheckDesc,
        "DnsLookup" => Loc.ToolDnsLookupDesc,
        "PingRoute" => Loc.ToolPingRouteDesc,
        "Calculator" => Loc.ToolCalculatorDesc,
        "MacTool" => Loc.ToolMacToolDesc,
        "Certificate" => Loc.ToolCertificateDesc,
        "HostDiscovery" => Loc.ToolHostDiscoveryDesc,
        "ServiceDetection" => Loc.ToolServiceDetectionDesc,
        "OsEstimate" => Loc.ToolOsEstimateDesc,
        "Checks" => Loc.ToolChecksDesc,
        "ScanExport" => Loc.ToolScanExportDesc,
        _ => "",
    };

    private FrameworkElement BuildPage(string tag) => tag switch
    {
        "WakeOnLan" => new WakeOnLanTool(_owner, _vm.WakeOnLan),
        "PortCheck" => new PortCheckTool(_owner, _vm.PortCheck),
        "DnsLookup" => new DnsLookupTool(_vm.DnsLookup),
        "PingRoute" => new PingRouteTool(_vm.PingRoute),
        "Calculator" => new CalculatorTool(_vm.Calculator),
        "MacTool" => new MacTool(_vm.Mac),
        "Certificate" => new CertificateTool(_vm.Certificate),
        "HostDiscovery" => new ScanToolPage(_owner, _vm.HostDiscovery),
        "ServiceDetection" => new ScanToolPage(_owner, _vm.ServiceDetection),
        "OsEstimate" => new ScanToolPage(_owner, _vm.OsEstimate),
        "Checks" => new ScanToolPage(_owner, _vm.Checks),
        "ScanExport" => new ScanToolPage(_owner, _vm.ScanExport),
        _ => new WakeOnLanTool(_owner, _vm.WakeOnLan),
    };

    private void OnToolsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolsNavList.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = BuildPage(tag);
            _pages[tag] = page;
        }
        ToolContent.Content = page;
        ToolDescription.Text = DescFor(tag);
        _owner.PersistToolsLastPage(tag);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Stops every tool's in-flight work the moment the window closes - a running
    /// scan/ping/lookup must never keep going once its own page is gone.
    /// Fire-and-forget: nothing here needs to be awaited.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.StopAll();
        base.OnClosed(e);
        RepairOwner();
    }

    private WindowState _ownerStateAtClose;
    private Rect _ownerBoundsAtClose;
    private bool _ownerSnapshotTaken;

    /// <summary>The main window must come out of this window's closing exactly as it went in.
    /// Destroying the foreground window lets Windows pick the next window in z-order, which can
    /// be another app's (the main window drops behind it or, for a minimized tools window,
    /// follows into the taskbar). So the owner is made the active window while this one still
    /// exists, and its state and size are remembered for <see cref="RepairOwner"/>.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || Owner is not { } owner) return;
        _ownerStateAtClose = owner.WindowState;
        _ownerBoundsAtClose = new Rect(owner.Left, owner.Top, owner.Width, owner.Height);
        _ownerSnapshotTaken = true;
        if (owner.IsVisible && !_owner.IsClosingDown && owner.WindowState != WindowState.Minimized) owner.Activate();
    }

    /// <summary>What the main window needs after this window closed: a state to restore (it was
    /// minimized or hidden by the close) and/or its old size (it shrank). Null parts need nothing.</summary>
    internal static (WindowState? State, Rect? Bounds) OwnerRepair(
        WindowState before, Rect boundsBefore, WindowState after, Rect boundsAfter)
    {
        if (before != WindowState.Minimized && after == WindowState.Minimized)
            return (before, boundsBefore);
        bool sized = before == WindowState.Normal && after == WindowState.Normal
            && (Math.Abs(boundsBefore.Width - boundsAfter.Width) > 1 || Math.Abs(boundsBefore.Height - boundsAfter.Height) > 1);
        return (null, sized ? boundsBefore : null);
    }

    /// <summary>Safety net after the close has fully run: undoes any state or size change the
    /// close caused on the main window.</summary>
    private void RepairOwner()
    {
        if (!_ownerSnapshotTaken) return;
        var owner = _owner;
        var (state, bounds) = (_ownerStateAtClose, _ownerBoundsAtClose);
        owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!owner.IsLoaded || owner.IsClosingDown) return;
            var now = new Rect(owner.Left, owner.Top, owner.Width, owner.Height);
            var (restoreState, restoreBounds) = OwnerRepair(state, bounds, owner.WindowState, now);
            if (restoreState is { } s)
            {
                owner.Show();
                owner.WindowState = s;
            }
            if (restoreBounds is { } b)
            {
                owner.Left = b.Left; owner.Top = b.Top; owner.Width = b.Width; owner.Height = b.Height;
            }
            if (restoreState is not null || restoreBounds is not null) owner.Activate();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

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

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
    }
}
