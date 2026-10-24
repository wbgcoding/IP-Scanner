using System.Windows;
using System.Windows.Controls;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Sidebar CRUD: Networks, Pinned IPs, Internet hosts ──
    // Each list persists straight into ScanConfig and re-applies live (no restart
    // needed): Networks via RefreshSubnetMeta, Pinned IPs via RefreshPinnedNames,
    // Internet hosts by restarting their own ping loop.
    private EntryKind _editKind;
    private string? _editSourceKey;      // existing entry's config key, or null = adding new
    private bool _editTargetReadOnly;    // primary network: target fixed to its own CIDR
    private string _editColor = "";
    private ConfirmButtonGate? _entryDeleteGate;

    private ConfirmButtonGate EntryDeleteGate => _entryDeleteGate ??= new ConfirmButtonGate(ShowEntryDeleteArmed);

    private void ShowEntryDeleteArmed(bool armed)
    {
        EntryEditDeleteBtn.Content = armed ? Loc.TipConfirmRemove : Loc.TipRemoveChip;
        EntryEditDeleteBtn.Style = (Style)FindResource(armed ? "DangerButton" : "GhostButton");
    }

    private void OpenEntryEdit(UIElement anchor, EntryKind kind, string title, string targetLabel,
        string target, string name, string color, string? sourceKey, bool targetReadOnly, bool canDelete)
    {
        _editKind = kind;
        _editSourceKey = sourceKey;
        _editTargetReadOnly = targetReadOnly;
        _editColor = color;
        EntryEditTitle.Text = title;
        EntryEditTargetLabel.Text = targetLabel;
        EntryEditTargetBox.Text = target;
        EntryEditTargetBox.IsEnabled = !targetReadOnly;
        EntryEditNameBox.Text = name;
        EntryEditError.Visibility = Visibility.Collapsed;
        EntryEditNameHint.Visibility = Visibility.Collapsed;
        EntryEditColorBtn.Visibility = kind == EntryKind.Host ? Visibility.Collapsed : Visibility.Visible;
        EntryEditColorBtn.Background = color.Length > 0 ? BrushFor(color) : (System.Windows.Media.Brush)FindResource("Surface");
        EntryDeleteGate.Reset();
        ShowEntryDeleteArmed(false);
        EntryEditDeleteBtn.Visibility = sourceKey is not null && canDelete ? Visibility.Visible : Visibility.Collapsed;
        CloseOtherEditPopups(EntryEditPopup);
        EntryEditPopup.PlacementTarget = anchor;
        PlaceAvoidingBottomEdge(EntryEditPopup, anchor);
        EntryEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            EntryEditNameBox.Focus();
            EntryEditNameBox.SelectAll();
        });
    }

    private void OnEntryEditColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenColorPicker(EntryEditColorBtn, _editColor,
            hex => { _editColor = hex; EntryEditColorBtn.Background = BrushFor(hex); },
            () => { _editColor = ""; EntryEditColorBtn.Background = (System.Windows.Media.Brush)FindResource("Surface"); });

    private void OnEntryEditKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnEntryEditSave(sender, e);
        else if (e.Key == System.Windows.Input.Key.Escape) { OnEntryEditCancel(sender, e); e.Handled = true; }
    }

    /// <summary>Esc closes the popup without saving from any focused control inside it.</summary>
    private void OnEntryEditPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        EntryEditPopup.IsOpen = false;
        e.Handled = true;
    }

    private void OnEntryEditCancel(object sender, RoutedEventArgs e) => EntryEditPopup.IsOpen = false;

    /// <summary>The duplicate-name hint is only ever raised by a save attempt -
    /// the next edit clears it instead of leaving a stale warning up.</summary>
    private void OnEntryEditNameChanged(object sender, TextChangedEventArgs e)
        => EntryEditNameHint.Visibility = Visibility.Collapsed;

    private void OnEntryEditSave(object sender, RoutedEventArgs e)
    {
        var r = Sidebar.Save(_editKind, _editSourceKey, _editTargetReadOnly, EntryEditTargetBox.Text, EntryEditNameBox.Text, _editColor);
        EntryEditNameHint.Visibility = r.DuplicateName ? Visibility.Visible : Visibility.Collapsed;
        if (!r.Saved)
        {
            EntryEditError.Text = r.Error switch
            {
                EntryError.Invalid => Loc.InvalidEntry(r.Target),
                EntryError.PrefixTooLarge => Loc.Ipv6PrefixTooLarge,
                _ => Loc.DuplicateEntry(r.Target),
            };
            EntryEditError.Visibility = Visibility.Visible;
            return;
        }
        EntryEditPopup.IsOpen = false;
        RefreshIpv6Capabilities();
    }

    private void OnEntryEditDelete(object sender, RoutedEventArgs e)
    {
        if (!EntryDeleteGate.Press()) return;
        if (_editSourceKey is { } key) Sidebar.Delete(_editKind, key);
        EntryEditPopup.IsOpen = false;
        RefreshIpv6Capabilities();
    }

    // The list logic (validate, store, delete, arm, undo) lives in SidebarController; this class only
    // wires it to the window and feeds it the live config.
    private SidebarController? _sidebar;
    private SidebarController Sidebar => _sidebar ??= new SidebarController(() => _config, new SidebarHooks
    {
        ConfigChanged = () => { _vm.Config = _config; ApplySnmpTrust(); },
        RefreshNetworks = () => _vm.RefreshSubnetMeta(),
        RefreshPinned = () => _vm.RefreshPinnedNames(),
        RestartInternetHosts = () => _vm.RestartInternetPinging(_config.PingCount == 0 ? 1 : _config.PingCount),
        Persist = PersistConfigDebounced,
        EntryRemoved = ShowUndoBanner,
    });

    private List<string> EntryListFor(EntryKind kind) => Sidebar.ListFor(kind);

    // ── Arm-then-confirm delete on the sidebar's per-row ✕ (2nd click within a few seconds removes
    //    it): Sidebar.DeleteRow. A list rebuild must drop any armed state that could keep pointing
    //    at a replaced view model; wired to MainViewModel.ListsRebuilt. ──
    private void DisarmSidebarDeletes()
    {
        Sidebar.Disarm();
        EntryDeleteGate.Reset();
    }

    // ── Notice banner: undo hint after removing a sidebar entry, and startup data notices ──
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;
    private Action? _noticeAction;
    private bool _noticeSticky;
    // A sticky notice that a short-lived one covered; it comes back when that one goes away.
    private (string Text, string? ActionLabel, Action? Action)? _heldStickyNotice;

    /// <summary>Shows the bottom banner with an optional action button. By default it hides itself
    /// after 6 seconds; a sticky notice stays until its button is pressed.</summary>
    private void ShowNotice(string text, string? actionLabel, Action? action, bool sticky = false)
    {
        if (_noticeSticky && !sticky && _heldStickyNotice is null)
            _heldStickyNotice = (NoticeText.Text, NoticeActionBtn.Content as string, _noticeAction);
        _noticeAction = action;
        _noticeSticky = sticky;
        NoticeText.Text = text;
        NoticeActionBtn.Content = actionLabel;
        NoticeActionBtn.Visibility = string.IsNullOrEmpty(actionLabel) ? Visibility.Collapsed : Visibility.Visible;
        NoticeBanner.Visibility = Visibility.Visible;
        _noticeTimer ??= new System.Windows.Threading.DispatcherTimer();
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimeout;   // re-subscribing without this would stack handlers
        _noticeTimer.Tick += OnNoticeTimeout;
        _noticeTimer.Interval = TimeSpan.FromSeconds(6);
        if (!sticky) _noticeTimer.Start();
    }

    private void OnNoticeTimeout(object? sender, EventArgs e) => HideNotice();

    /// <summary>Hides the banner. A sticky notice survives list rebuilds and the timer; only its
    /// own button (force) closes it.</summary>
    private void HideNotice(bool force = false)
    {
        if (_noticeSticky && !force) return;
        _noticeTimer?.Stop();
        _noticeAction = null;
        _noticeSticky = false;
        NoticeBanner.Visibility = Visibility.Collapsed;
        if (_heldStickyNotice is { } held)
        {
            _heldStickyNotice = null;
            ShowNotice(held.Text, held.ActionLabel, held.Action, sticky: true);
        }
    }

    private void ShowUndoBanner(EntryKind kind, NetEntry entry)
        => ShowNotice(Loc.EntryRemoved(entry.Name.Length > 0 ? entry.Name : entry.Target), Loc.UndoLabel,
            () => Sidebar.Restore(kind, entry));

    /// <summary>Shows the one-time startup notice for a data file that could not be loaded as is.</summary>
    internal void ShowLoadProblem(LoadProblem problem)
    {
        if (problem == LoadProblem.None) return;
        ShowNotice(problem switch
        {
            LoadProblem.KeyUnavailable => Loc.NoticeKeyUnavailable,
            LoadProblem.Unreadable => Loc.NoticeDataUnreadable,
            LoadProblem.NewerFormat => Loc.NoticeDataNewer,
            _ => Loc.NoticeDataRestored,
        }, Loc.Ok, null, sticky: true);
    }

    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        var action = _noticeAction;
        HideNotice(force: true);   // first: the action may rebuild lists and re-enter the banner
        action?.Invoke();
    }

    // ── Networks sidebar card ──
    private void OpenEdit(UIElement anchor, EntryEditSpec s)
        => OpenEntryEdit(anchor, s.Kind, s.Title, s.TargetLabel, s.Target, s.Name, s.Color, s.SourceKey, s.TargetReadOnly, s.CanDelete);

    private void OnRowEditClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Sidebar.EditSpecFor((sender as FrameworkElement)?.DataContext) is not { } spec) return;
        OpenEdit((UIElement)sender, spec);
        e.Handled = true;
    }

    private void OnRowDeleteClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Sidebar.DeleteRow((sender as FrameworkElement)?.DataContext)) e.Handled = true;
    }

    private void OnAddEntryClick(EntryKind kind, object sender) => OpenEdit((UIElement)sender, Sidebar.AddSpecFor(kind));

    private void OnNetworkAddClick(object sender, RoutedEventArgs e) => OnAddEntryClick(EntryKind.Network, sender);

    private void OnNetworkZoomClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkInfoViewModel net) return;
        net.IsZoomed = string.Equals((sender as FrameworkElement)?.Tag as string, "True", StringComparison.Ordinal);
    }

    /// <summary>"Scan now" on a pending subnet card - scans just that
    /// subnet instead of a full rescan of everything already configured.</summary>
    private async void OnScanSubnetNowClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_restoreGate.Active) return;
            if ((sender as FrameworkElement)?.DataContext is not NetworkInfoViewModel { Cidr: { } cidr }) return;
            var parts = cidr.Split('/');
            int len = parts.Length > 1 && int.TryParse(parts[1], out var l) ? l : 24;
            var prefixes = Core.Net.Ipv4.PrefixesForCidr(parts[0], len);
            if (prefixes is null)
            {
                await _vm.RunScanAsync(new[] { Core.Net.Ipv4.SubnetPrefix(parts[0]) });
                return;
            }
            var range = Core.Net.Ipv4.HostRange(parts[0], len);
            await _vm.RunScanAsync(prefixes, range is { } r ? new[] { r } : null);
        }, Loc.ScanError);
    }

    private void OnPinnedAddClick(object sender, RoutedEventArgs e) => OnAddEntryClick(EntryKind.Pinned, sender);

    private void OnHostAddClick(object sender, RoutedEventArgs e) => OnAddEntryClick(EntryKind.Host, sender);

    /// <summary>Keyboard access for the three sidebar row Grids (Tab already reaches them via
    /// KeyboardNavigation, once Focusable is set in XAML) - Enter opens the same edit popup as a click,
    /// Delete starts the same arm-then-confirm gesture as the row's own chip.</summary>
    private void OnSidebarRowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (e.Key == System.Windows.Input.Key.Enter && Sidebar.EditSpecFor(fe.DataContext) is { } spec)
        {
            OpenEdit(fe, spec);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Delete && Sidebar.DeleteRow(fe.DataContext))
            e.Handled = true;
    }

    // Internet mini-graph 1m/30s zoom. The label colors come from _vm.IsZoomed and the shared
    // ZoomLabelActiveWhenZoomed/NotZoomed styles, so they follow a live theme switch.
    private void OnInetZoomClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.IsZoomed = string.Equals((sender as FrameworkElement)?.Tag as string, "True", StringComparison.Ordinal);
        RenderInternetGraph();
    }

    // ── Cell editing: hostname popup, group-square color picker (double-click) ──
    private DeviceViewModel? _cellEditVm;

    private static DeviceViewModel? RowVm(object sender) =>
        (sender as FrameworkElement)?.DataContext as DeviceViewModel;

    // Latency columns (Avg, Min, Max, Last) - clicking one opens the graph.
    private static readonly int[] PingValueColumns = { 4, 5, 6, 7 };

    /// <summary>Open the row's latency graph only from a ping-value column; close
    /// it again on a click anywhere in the row.</summary>
    private void OnDeviceRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || sender is not DataGridRow { Item: DeviceViewModel vm }) return;
        // Ctrl/Shift+click extends the selection without also toggling the graph.
        const System.Windows.Input.ModifierKeys selectExtend =
            System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift;
        if ((System.Windows.Input.Keyboard.Modifiers & selectExtend) != 0) return;
        var origin = e.OriginalSource as DependencyObject;
        // A click inside the expanded row-detail panel (scan/cancel buttons, service chips)
        // must reach its own target - not close the panel or fall through to the graph
        // toggle below, which would otherwise swallow all three.
        if (FindParent<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(origin) is not null) return;
        // While open, a click anywhere else in the row just closes it (consume so it
        // doesn't also pin/edit the clicked cell).
        if (vm.ShowGraph) { vm.ShowGraph = false; e.Handled = true; return; }
        var cell = FindParent<DataGridCell>(origin);
        if (cell?.Column is { } col && System.Array.IndexOf(PingValueColumns, col.DisplayIndex) >= 0)
            vm.ShowGraph = true;
    }

    private static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o is not null and not T)
        {
            if (o is not System.Windows.Media.Visual) return null;   // GetParent only walks visuals
            o = System.Windows.Media.VisualTreeHelper.GetParent(o);
        }
        return o as T;
    }

    private void OnHostnameCellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { } vm) return;
        var h = vm.Model.Hostname;
        _cellEditVm = vm;
        CellEditBox.Text = h is null or Device.Unknown ? "" : h;
        CellEditBox.MinWidth = Math.Max(170, (sender as FrameworkElement)?.ActualWidth ?? 0);
        CloseOtherEditPopups(CellEditPopup);
        CellEditPopup.PlacementTarget = (UIElement)sender;
        PlaceAvoidingBottomEdge(CellEditPopup, (UIElement)sender);
        CellEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            CellEditBox.Focus();
            System.Windows.Input.Keyboard.Focus(CellEditBox);
            CellEditBox.SelectAll();
        });
        e.Handled = true;
    }

    /// <summary>Group square: pick a fixed color for this device (✕ = automatic).</summary>
    private void OnGroupCellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { } vm) return;
        OpenColorPicker((UIElement)sender, vm.GroupColor,
            hex =>
            {
                _vm.SetColorOverride(vm, hex);
                if (vm.IsPinned) UpdatePinnedField(vm.Ip, null, hex);
            },
            () =>
            {
                _vm.SetColorOverride(vm, null);
                if (vm.IsPinned) UpdatePinnedField(vm.Ip, null, "");
            });
        e.Handled = true;
    }

    /// <summary>Update one field of an existing pinned entry, keeping the other as-is
    /// (null = keep, "" = clear, else = set). No-op if the IP isn't pinned.</summary>
    private void UpdatePinnedField(string ip, string? name, string? color)
    {
        var pins = _config.PinnedIps.Select(NetEntry.Parse).ToList();
        int idx = pins.FindIndex(en => en.Target == ip);
        if (idx < 0) return;
        var old = pins[idx];
        pins[idx] = new NetEntry(ip, name ?? old.Name, color ?? old.Color);
        _config.PinnedIps = pins.Select(en => en.ToString()).ToList();
        _vm.Config = _config;
        ApplySnmpTrust();
        _vm.RefreshPinnedNames();
        PersistConfigDebounced();
    }

    // ── Pin / unpin via the IP column ──
    private void OnPinIconClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowVm(sender) is not { IsPinned: true } vm) return;
        Sidebar.Delete(EntryKind.Pinned, vm.Ip);
        e.Handled = true;
    }

    private void OnIpTextClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { IsPinned: false } vm) return;
        Sidebar.Pin(vm.Ip);
        e.Handled = true;
    }

    private void OnCellEditKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) CommitCellEdit();
        else if (e.Key == System.Windows.Input.Key.Escape) CellEditPopup.IsOpen = false;
    }

    private void OnCellEditOk(object sender, RoutedEventArgs e) => CommitCellEdit();

    private void CommitCellEdit()
    {
        if (_cellEditVm is { } vm)
        {
            _vm.SetHostnameOverride(vm, CellEditBox.Text);
            if (vm.IsPinned) UpdatePinnedField(vm.Ip, CellEditBox.Text.Trim(), null);
        }
        CellEditPopup.IsOpen = false;
    }

    /// <summary>✕ in the popup: drop the manual value, back to the automatic one.</summary>
    private void OnCellEditReset(object sender, RoutedEventArgs e)
    {
        if (_cellEditVm is { } vm)
        {
            _vm.SetHostnameOverride(vm, null);
            if (vm.IsPinned) UpdatePinnedField(vm.Ip, "", null);
        }
        CellEditPopup.IsOpen = false;
    }

    // ── Scans folder shortcut (sidebar, left of the export toggles) ──
    private void OnOpenScansFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ScanConfig.ResolvePath(string.IsNullOrWhiteSpace(_config.OutputDirectory)
                ? ScanConfig.DefaultOutputDirectory : _config.OutputDirectory);
            Directory.CreateDirectory(dir);
            OpenFolderInExplorer(dir);
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Ui, "Opening the output folder failed", ex);
            ErrorDialog.Show(this, Loc.OpenFolderFailedTitle, ex, null);
        }
    }

    // Explorer is launched on the folder explicitly: handing the raw path to ShellExecute would
    // pick whatever verb Windows associates with it, and "run" if it ever pointed at an executable.
    private void OpenFolderInExplorer(string dir)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Ui, "Opening Explorer failed", ex);
            ErrorDialog.Show(this, Loc.OpenFolderFailedTitle, ex, dir);
        }
    }

    // ── Collapsed "recent files" section ──
    private void OnToggleExportsExpanded(object sender, RoutedEventArgs e)
    {
        _config.SidebarExportsExpanded = !_config.SidebarExportsExpanded;
        ApplyExportsExpanded();
        PersistConfigDebounced();
    }

    /// <summary>Applies the persisted expanded/collapsed state at startup and
    /// after a config reset/import.</summary>
    private void ApplyExportsExpanded()
    {
        ExportsContent.Visibility = _config.SidebarExportsExpanded
            ? Visibility.Visible : Visibility.Collapsed;
        ExportsChevron.Text = _config.SidebarExportsExpanded ? "▾" : "▸";
        ExportsHeaderToggle.IsChecked = _config.SidebarExportsExpanded;
    }

    // Expand state for the "Funde" (conflict findings) section - kept
    // as a plain field, not a ScanConfig setting, since the section only ever
    // shows in-memory results from the last scan and is empty again at
    // startup, so nothing would be left to restore from a persisted value.
    private bool _findingsExpanded = true;

    private void OnToggleFindingsExpanded(object sender, RoutedEventArgs e)
    {
        _findingsExpanded = !_findingsExpanded;
        FindingsContent.Visibility = _findingsExpanded ? Visibility.Visible : Visibility.Collapsed;
        FindingsChevron.Text = _findingsExpanded ? "▾" : "▸";
        FindingsHeaderToggle.IsChecked = _findingsExpanded;
    }

    // The three main sidebar sections collapse like the exports section above, but default to
    // expanded because they carry the app's primary content. The scroll position survives a
    // rebuild because the lists reconcile in place instead of clearing.
    private void OnToggleNetworksExpanded(object sender, RoutedEventArgs e)
    {
        _config.SidebarNetworksExpanded = !_config.SidebarNetworksExpanded;
        ApplySidebarSectionsExpanded();
        PersistConfigDebounced();
    }

    private void OnTogglePinnedExpanded(object sender, RoutedEventArgs e)
    {
        _config.SidebarPinnedExpanded = !_config.SidebarPinnedExpanded;
        ApplySidebarSectionsExpanded();
        PersistConfigDebounced();
    }

    private void OnToggleInternetExpanded(object sender, RoutedEventArgs e)
    {
        _config.SidebarInternetExpanded = !_config.SidebarInternetExpanded;
        ApplySidebarSectionsExpanded();
        PersistConfigDebounced();
    }

    /// <summary>Applies the persisted expanded/collapsed state of all three
    /// sidebar sections at startup and after a config reset/import.</summary>
    private void ApplySidebarSectionsExpanded()
    {
        NetworksContent.Visibility = _config.SidebarNetworksExpanded ? Visibility.Visible : Visibility.Collapsed;
        NetworksChevron.Text = _config.SidebarNetworksExpanded ? "▾" : "▸";
        NetworksHeaderToggle.IsChecked = _config.SidebarNetworksExpanded;
        PinnedContent.Visibility = _config.SidebarPinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
        PinnedChevron.Text = _config.SidebarPinnedExpanded ? "▾" : "▸";
        PinnedHeaderToggle.IsChecked = _config.SidebarPinnedExpanded;
        InternetContent.Visibility = _config.SidebarInternetExpanded ? Visibility.Visible : Visibility.Collapsed;
        InternetChevron.Text = _config.SidebarInternetExpanded ? "▾" : "▸";
        InternetHeaderToggle.IsChecked = _config.SidebarInternetExpanded;
    }

    // Advanced network settings expander on the Scan page. Session-only
    // (not persisted) - the section is meant to stay out of the way by default.
    private bool _advancedNetworkExpanded;
    private void OnToggleAdvancedNetwork(object sender, RoutedEventArgs e)
    {
        _advancedNetworkExpanded = !_advancedNetworkExpanded;
        AdvancedNetworkContent.Visibility = _advancedNetworkExpanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedNetworkChevron.Text = _advancedNetworkExpanded ? "▾" : "▸";
        AdvancedNetworkToggle.IsChecked = _advancedNetworkExpanded;
    }
}
