using System.IO;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ScanConfig _config;

    public MainWindow()
    {
        // Settings persist as ip_scanner.conf next to the database and are
        // loaded again on every start. Language must be set before any XAML
        // loads because x:Static bindings cache their values.
        _config = LoadPersistedConfig();
        Loc.SetLanguage(_config.Language);

        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _vm = new MainViewModel(
            pingFunc: IcmpPinger.Ping,
            detectNetwork: NetworkDetector.DetectFast,
            dispatch: a => Dispatcher.BeginInvoke(a),
            enrichers: Enrichers)
        { Config = _config };
        DataContext = _vm;

        // Prefill the subnet field with the currently detected network so the
        // value is visible immediately (user can edit it before scanning).
        try
        {
            var ni = NetworkDetector.DetectFast();
            if (ni.Cidr is not null) SubnetBox.Text = ni.Cidr.Split('/')[0];   // base IP, no /24
        }
        catch { /* detection best-effort */ }

        // Center horizontally on the primary monitor; fill its full work-area height.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = wa.Height;
        Top = wa.Top;
        Left = wa.Left + (wa.Width - Width) / 2;

        // One button toggles between Scan and Stop depending on scan state.
        _vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(MainViewModel.IsScanning))
                Dispatcher.BeginInvoke(UpdateScanButton);
        };
        LogoImage.RenderTransform = _logoSpin;
        System.Windows.Media.CompositionTarget.Rendering += OnSpinTick;
        ApplyUiScale();          // persisted text-scale takes effect at startup
        ApplyBarColors();        // persisted bar colors
        ApplyDefaultPingCount(); // persisted default ping count into the dropdown
        LanguageBox.ItemsSource = new[] { Loc.LangAuto, Loc.LangDe, Loc.LangEn };
        InitColorPicker();
        InitChipEditors();
        _vm.InitOverrides(OverridesPathFor(_config.DatabasePath));   // manual hostname/group edits
        InitGraph();
        ApplyGraphSettings();
        LoadSettings(_config);   // fills all fields incl. export toggles once

        // On startup, immediately run a discovery sweep of the local network so
        // the sidebar network info and online devices show up without a manual scan.
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunInitScanAsync();
                // Auto-detect: fill the subnet field from the discovered network
                // if the user hasn't typed anything yet.
                if (SubnetBox.Text.Trim().Length == 0 && _vm.Networks.Count > 0)
                    SubnetBox.Text = _vm.Networks[0].Cidr.Split('/')[0];
            }
            catch { /* best-effort */ }
        };
    }

    // Logo spin: speed eases toward a target so starting/stopping never jumps.
    private readonly System.Windows.Media.RotateTransform _logoSpin = new();
    private double _spinAngle, _spinSpeed, _spinTarget;   // deg, deg/s
    private double _spinRef = 1;                          // last nonzero target (glow scale)
    private long _spinLastTick = System.Diagnostics.Stopwatch.GetTimestamp();

    private void OnSpinTick(object? sender, EventArgs e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _spinLastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
        _spinLastTick = now;
        if (_spinSpeed == 0 && _spinTarget == 0) return;

        _spinSpeed += (_spinTarget - _spinSpeed) * Math.Min(1.0, dt * 2.5);   // ~1s ramp

        if (_spinTarget == 0 && Math.Abs(_spinSpeed) < 3)
        {
            _spinSpeed = 0;
            LogoGlow.Opacity = 0;
            return;
        }

        _spinAngle = (_spinAngle + _spinSpeed * dt) % 360;
        _logoSpin.Angle = _spinAngle;

        // Centre dot glow: brightness follows the eased spin speed, pulsing
        // twice per revolution so it breathes in sync with the rotation.
        double norm = Math.Clamp(_spinSpeed / _spinRef, 0, 1);
        double pulse = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(_spinAngle * Math.PI / 90));
        LogoGlow.Opacity = norm * pulse;
    }

    private void UpdateScanButton()
    {
        bool scanning = _vm.IsScanning;
        ScanStopButton.Content = scanning ? Loc.Stop : Loc.Scan;
        ScanStopButton.Style = (Style)FindResource(scanning ? "DangerButton" : "AccentButton");

        // Spin while scanning — faster with more threads, eased in/out.
        int threads = _config.ScanThreads <= 0 ? 254 : _config.ScanThreads;
        double seconds = Math.Clamp(120.0 / threads, 0.6, 6.0);
        _spinTarget = scanning ? 360.0 / seconds : 0.0;
        if (_spinTarget > 0) _spinRef = _spinTarget;   // glow scales against full speed
    }

    /// <summary>Scale the whole UI (text included) by the configured percent.</summary>
    // ── Progress-bar colors (legend squares act as color pickers) ──
    private void ApplyBarColors()
    {
        static System.Windows.Media.SolidColorBrush B(string hex) =>
            new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        DevBar.Color1 = B(_config.ColorOnline);  LegOnline.Background = B(_config.ColorOnline);
        DevBar.Color2 = B(_config.ColorOffline); LegOffline.Background = B(_config.ColorOffline);
        PingBar.Color1 = B(_config.ColorSuccess); LegSuccess.Background = B(_config.ColorSuccess);
        PingBar.Color2 = B(_config.ColorFailed);  LegFailed.Background = B(_config.ColorFailed);
        PingBar.Color3 = B(_config.ColorSkipped); LegSkipped.Background = B(_config.ColorSkipped);
    }

    // ── Themed color picker (popup with palette swatches + hex field) ──
    // The OK button routes the chosen color to whoever opened the popup.
    private Action<string>? _pickerApply;

    private static readonly string[] SwatchColors =
    {
        "#F5E0DC", "#F2CDCD", "#F5C2E7", "#CBA6F7", "#F38BA8",
        "#EBA0AC", "#FAB387", "#F9E2AF", "#A6E3A1", "#94E2D5",
        "#89DCEB", "#74C7EC", "#89B4FA", "#B4BEFE", "#CDD6F4",
        "#A6ADC8", "#6C7086", "#585B70", "#45475A", "#313244",
    };

    private void InitColorPicker()
    {
        foreach (var hex in SwatchColors)
        {
            var swatch = new System.Windows.Controls.Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                Margin = new Thickness(3), Cursor = System.Windows.Input.Cursors.Hand,
                Background = BrushFor(hex), Tag = hex, ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += OnSwatchPick;
            SwatchPanel.Children.Add(swatch);
        }
    }

    private static System.Windows.Media.SolidColorBrush BrushFor(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void OnLegendColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string key } square) return;
        OpenColorPicker(square, GetBarColor(key), hex =>
        {
            SetBarColor(key, hex);
            ApplyBarColors();
            PersistConfig();
        });
    }

    private Action? _pickerReset;

    private void OpenColorPicker(UIElement target, string currentHex, Action<string> apply,
                                 Action? reset = null)
    {
        _pickerApply = apply;
        _pickerReset = reset;
        PickerResetBtn.Visibility = reset is null ? Visibility.Collapsed : Visibility.Visible;
        HexBox.Text = currentHex;
        ColorPickerPopup.PlacementTarget = target;
        ColorPickerPopup.IsOpen = true;
    }

    private void OnPickerReset(object sender, RoutedEventArgs e)
    {
        _pickerReset?.Invoke();
        ColorPickerPopup.IsOpen = false;
    }

    private void OnSwatchPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Select only (hex field + preview update) — applied when OK is pressed.
        if (sender is System.Windows.Controls.Border { Tag: string hex })
            HexBox.Text = hex;
    }

    /// <summary>Popups stay open until OK or a click outside them.</summary>
    private void OnWindowPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ColorPickerPopup.IsOpen &&
            ColorPickerPopup.Child is FrameworkElement picker && !picker.IsMouseOver)
            ColorPickerPopup.IsOpen = false;
        if (CellEditPopup.IsOpen &&
            CellEditPopup.Child is FrameworkElement editor && !editor.IsMouseOver)
            CellEditPopup.IsOpen = false;
    }

    private static bool IsHexColor(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), "^#[0-9A-Fa-f]{6}$");

    private void OnHexColorChanged(object sender, TextChangedEventArgs e)
    {
        if (IsHexColor(HexBox.Text)) HexPreview.Background = BrushFor(HexBox.Text.Trim());
    }

    private void OnHexColorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnHexColorApply(sender, e);
    }

    private void OnHexColorApply(object sender, RoutedEventArgs e)
    {
        if (IsHexColor(HexBox.Text)) ApplyPickedColor(HexBox.Text.Trim().ToUpperInvariant());
    }

    private void ApplyPickedColor(string hex)
    {
        _pickerApply?.Invoke(hex);
        ColorPickerPopup.IsOpen = false;
    }

    // ── Chip editors for subnets and pinned IPs (settings) ──
    private ChipEditor _subnetChips = null!, _pinnedChips = null!, _hostChips = null!;

    private void InitChipEditors()
    {
        _subnetChips = new ChipEditor(this, SubnetNetBox, SubnetNameBox, SubnetColorBtn,
                                      SubnetDeleteBtn, SubnetsError, SubnetChips, IsValidSubnetEntry);
        _pinnedChips = new ChipEditor(this, PinnedIpBox, PinnedNameBox, PinnedColorBtn,
                                      PinnedDeleteBtn, PinnedError, PinnedChips, Core.Net.Ipv4.IsValid);
        _hostChips = new ChipEditor(this, HostIpBox, HostNameBox, null,
                                    HostDeleteBtn, InternetHostsError, HostChips, Core.Net.Ipv4.IsValid);
    }

    private void OnSubnetColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => _subnetChips.PickColor();
    private void OnPinnedColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => _pinnedChips.PickColor();
    private void OnSubnetAdd(object sender, RoutedEventArgs e) => _subnetChips.Add();
    private void OnPinnedAdd(object sender, RoutedEventArgs e) => _pinnedChips.Add();
    private void OnSubnetDelete(object sender, RoutedEventArgs e) => _subnetChips.DeleteEditing();
    private void OnPinnedDelete(object sender, RoutedEventArgs e) => _pinnedChips.DeleteEditing();

    private void OnSubnetInputKey(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) _subnetChips.Add(); }

    private void OnPinnedInputKey(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) _pinnedChips.Add(); }

    private void OnHostAdd(object sender, RoutedEventArgs e) => _hostChips.Add();
    private void OnHostDelete(object sender, RoutedEventArgs e) => _hostChips.DeleteEditing();

    private void OnHostInputKey(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) _hostChips.Add(); }

    /// <summary>Editable bubble list: target + optional name + color per entry.
    /// Double-click on a bubble loads it for editing; ✕ deletes the loaded one.
    /// A fresh random default color is chosen after every add/load.</summary>
    private sealed class ChipEditor
    {
        private readonly MainWindow _w;
        private readonly TextBox _target, _name;
        private readonly System.Windows.Controls.Border? _colorBtn;   // null = colorless list
        private readonly Button _deleteBtn;
        private readonly TextBlock _error;
        private readonly System.Windows.Controls.WrapPanel _panel;
        private readonly Func<string, bool> _isValid;
        private NetEntry? _editing;
        private string _color = "";

        public List<NetEntry> Entries { get; } = new();

        public ChipEditor(MainWindow w, TextBox target, TextBox name,
                          System.Windows.Controls.Border? colorBtn, Button deleteBtn,
                          TextBlock error, System.Windows.Controls.WrapPanel panel,
                          Func<string, bool> isValid)
        {
            _w = w; _target = target; _name = name; _colorBtn = colorBtn;
            _deleteBtn = deleteBtn; _error = error; _panel = panel; _isValid = isValid;
            _target.TextChanged += (_, _) => LiveValidate();
            RandomColor();
        }

        private void LiveValidate()
        {
            var t = _target.Text.Trim();
            if (t.Length == 0 || _isValid(t))
            {
                _error.Visibility = Visibility.Collapsed;
            }
            else
            {
                _error.Text = Loc.InvalidEntry(t);
                _error.Visibility = Visibility.Visible;
            }
        }

        public void Load(IEnumerable<string> raw)
        {
            Entries.Clear();
            Entries.AddRange(raw.Select(NetEntry.Parse).Where(en => en.Target.Length > 0));
            Reset();
            Rebuild();
        }

        public void AddEntry(NetEntry entry)
        {
            Entries.RemoveAll(e => e.Target == entry.Target);
            Entries.Add(entry);
            Rebuild();
        }

        public void RemoveEntry(string target)
        {
            Entries.RemoveAll(e => e.Target == target);
            Reset();
            Rebuild();
        }

        public void UpdateEntry(string target, string? name, string? color)
        {
            int idx = Entries.FindIndex(e => e.Target == target);
            if (idx < 0) return;
            var old = Entries[idx];
            Entries[idx] = new NetEntry(
                target,
                name is null ? old.Name : name,
                color is null ? old.Color : color == "" ? "" : color);
            Rebuild();
        }

        public void PickColor()
        {
            if (_colorBtn is null) return;
            _w.OpenColorPicker(_colorBtn, _color,
                hex => { _color = hex; _colorBtn.Background = BrushFor(hex); });
        }

        public void RandomColor()
        {
            if (_colorBtn is null) return;
            _color = SwatchColors[Random.Shared.Next(SwatchColors.Length)];
            _colorBtn.Background = BrushFor(_color);
        }

        public void Add()
        {
            var target = _target.Text.Trim();
            if (!_isValid(target))
            {
                _error.Text = Loc.InvalidEntry(target);
                _error.Visibility = Visibility.Visible;
                return;
            }
            if (_editing is { } old) Entries.Remove(old);
            Entries.RemoveAll(en => en.Target == target);   // re-adding replaces
            Entries.Add(new NetEntry(target, _name.Text.Trim(), _color));
            Reset();
            Rebuild();
            _w.ApplyInstant();
        }

        public void DeleteEditing()
        {
            if (_editing is { } old)
            {
                Entries.Remove(old);
                Rebuild();
                _w.ApplyInstant();
            }
            Reset();
        }

        private void Reset()
        {
            _editing = null;
            _target.Clear();
            _name.Clear();
            _deleteBtn.Visibility = Visibility.Collapsed;
            _error.Visibility = Visibility.Collapsed;
            RandomColor();
        }

        private void BeginEdit(NetEntry e)
        {
            _editing = e;
            _target.Text = e.Target;
            _name.Text = e.Name;
            _color = e.Color;
            if (_colorBtn is not null)
                _colorBtn.Background = e.Color.Length > 0
                    ? BrushFor(e.Color)
                    : (System.Windows.Media.Brush)_w.FindResource("Surface");
            _deleteBtn.Visibility = Visibility.Visible;
            _error.Visibility = Visibility.Collapsed;
        }

        private void Rebuild()
        {
            _panel.Children.Clear();
            foreach (var entry in Entries)
            {
                var e = entry;
                bool colored = e.Color.Length > 0;
                var chip = new System.Windows.Controls.Border
                {
                    Background = colored ? BrushFor(e.Color)
                                         : (System.Windows.Media.Brush)_w.FindResource("Surface"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(9, 4, 9, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = $"{e.Target} — {Loc.TipEditChip}",
                    Child = new TextBlock
                    {
                        Text = e.Name.Length > 0 ? e.Name : e.Target,
                        FontSize = 12,
                        Foreground = colored ? (System.Windows.Media.Brush)_w.FindResource("BgDark")
                                             : (System.Windows.Media.Brush)_w.FindResource("Text"),
                    },
                };
                chip.MouseLeftButtonDown += (_, args) => { if (args.ClickCount == 2) BeginEdit(e); };
                _panel.Children.Add(chip);
            }
        }
    }

    // ── Cell editing: hostname popup, group-square color picker (double-click) ──
    private DeviceViewModel? _cellEditVm;

    private static DeviceViewModel? RowVm(object sender) =>
        (sender as FrameworkElement)?.DataContext as DeviceViewModel;

    private void OnHostnameCellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { } vm) return;
        var h = vm.Model.Hostname;
        _cellEditVm = vm;
        CellEditBox.Text = h is null or Device.Unknown ? "" : h;
        CellEditBox.MinWidth = Math.Max(170, (sender as FrameworkElement)?.ActualWidth ?? 0);
        CellEditPopup.PlacementTarget = (UIElement)sender;
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
                if (vm.IsPinned) { _pinnedChips.UpdateEntry(vm.Ip, null, hex); ApplyInstant(); }
            },
            () =>
            {
                _vm.SetColorOverride(vm, null);
                if (vm.IsPinned) { _pinnedChips.UpdateEntry(vm.Ip, null, ""); ApplyInstant(); }
            });
        e.Handled = true;
    }

    // ── Pin / unpin via the IP column ──
    private void OnPinIconClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowVm(sender) is not { IsPinned: true } vm) return;
        _pinnedChips.RemoveEntry(vm.Ip);
        ApplyInstant();
        e.Handled = true;
    }

    private void OnIpTextClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { IsPinned: false } vm) return;
        _pinnedChips.AddEntry(new NetEntry(vm.Ip, "", ""));
        ApplyInstant();
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
            if (vm.IsPinned)
            {
                _pinnedChips.UpdateEntry(vm.Ip, CellEditBox.Text.Trim(), null);
                ApplyInstant();
            }
        }
        CellEditPopup.IsOpen = false;
    }

    /// <summary>✕ in the popup: drop the manual value, back to the automatic one.</summary>
    private void OnCellEditReset(object sender, RoutedEventArgs e)
    {
        if (_cellEditVm is { } vm)
        {
            _vm.SetHostnameOverride(vm, null);
            if (vm.IsPinned)
            {
                _pinnedChips.UpdateEntry(vm.Ip, "", null);
                ApplyInstant();
            }
        }
        CellEditPopup.IsOpen = false;
    }

    // ── Scans folder shortcut (sidebar, left of the export toggles) ──
    private void OnOpenScansFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(_config.OutputDirectory)
                ? ScanConfig.DefaultOutputDirectory : _config.OutputDirectory);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Latency history graphs (device graphs at the bottom, internet mini graph) ──
    private readonly List<double?> _inetSamples = new();

    /// <summary>Visible time window in samples (one per second).</summary>
    private int GraphWindowSeconds => Math.Clamp(_config.GraphMaxSeconds, 10, 300);

    /// <summary>Show/hide the graphs per settings.</summary>
    private void ApplyGraphSettings()
    {
        var vis = _config.GraphsEnabled ? Visibility.Visible : Visibility.Collapsed;
        GraphSection.Visibility = vis;
        InetGraphSection.Visibility = vis;
        _vm.NetworkGraphsOn = _config.GraphsEnabled && _config.NetworkGraphsEnabled;
    }

    private System.Windows.Threading.DispatcherTimer? _graphTimer;

    private void InitGraph()
    {
        _graphTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _graphTimer.Tick += (_, _) => OnGraphTick();
        _graphTimer.Start();
        Closed += (_, _) =>
        {
            _graphTimer.Stop();
            System.Windows.Media.CompositionTarget.Rendering -= OnSpinTick;
        };
    }

    private void OnGraphTick()
    {
        if (!_config.GraphsEnabled) return;
        _vm.UpdateGraphSources();
        foreach (var g in _vm.DeviceGraphs)
            g.AddSample(_vm.GraphValue(g.SelectedSource?.Ip), GraphWindowSeconds, TickIntervalSeconds());
        Sample(_inetSamples, _vm.InternetGraphValue());
        RenderInternetGraph();
        if (_config.NetworkGraphsEnabled)
            foreach (var net in _vm.Networks)
                net.AddGraphSample(GraphWindowSeconds, TickIntervalSeconds());
    }

    private void Sample(List<double?> series, double? value)
    {
        series.Add(value);
        while (series.Count > GraphWindowSeconds) series.RemoveAt(0);
    }

    private void OnAddDeviceGraph(object sender, RoutedEventArgs e) => _vm.AddDeviceGraph();

    private void OnRemoveDeviceGraph(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceGraphViewModel g)
            _vm.RemoveDeviceGraph(g);
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => RenderInternetGraph();

    private void RenderInternetGraph()
        => RenderSeries(_inetSamples, InetGraphCanvas, InetGraphLine, InetGraphMaxText, InetGraphMinText);

    private void RenderSeries(List<double?> samples, System.Windows.Controls.Canvas canvas,
                              System.Windows.Shapes.Polyline line,
                              TextBlock maxText, TextBlock minText)
    {
        var r = GraphSeries.Compute(samples, canvas.ActualWidth, canvas.ActualHeight,
                                    TickIntervalSeconds());
        line.Points = r.Points;
        maxText.Text = r.MaxText;
        minText.Text = r.MinText;

        for (int i = canvas.Children.Count - 1; i >= 0; i--)
            if (canvas.Children[i] is FrameworkElement fe && "tick".Equals(fe.Tag))
                canvas.Children.RemoveAt(i);
        var brush = BrushFor(Core.Palette.MidGray);
        foreach (var tick in r.Ticks)
        {
            var lbl = new TextBlock
            {
                Text = tick.Label, FontSize = 8, Foreground = brush,
                Width = 28, TextAlignment = TextAlignment.Center, Tag = "tick",
            };
            System.Windows.Controls.Canvas.SetLeft(lbl, tick.X);
            System.Windows.Controls.Canvas.SetBottom(lbl, 0);
            canvas.Children.Add(lbl);
        }
    }

    private int TickIntervalSeconds() => _config.GraphMaxSeconds switch
    {
        <= 30  => 5,
        <= 60  => 10,
        <= 120 => 30,
        _      => 60,
    };

    private string GetBarColor(string key) => key switch
    {
        "online" => _config.ColorOnline, "offline" => _config.ColorOffline,
        "success" => _config.ColorSuccess, "failed" => _config.ColorFailed,
        _ => _config.ColorSkipped,
    };

    private void SetBarColor(string key, string hex)
    {
        switch (key)
        {
            case "online": _config.ColorOnline = hex; break;
            case "offline": _config.ColorOffline = hex; break;
            case "success": _config.ColorSuccess = hex; break;
            case "failed": _config.ColorFailed = hex; break;
            default: _config.ColorSkipped = hex; break;
        }
    }

    private string ActiveConfPath()
    {
        var dir = _config.ConfigDirectory.Trim().Length > 0
            ? _config.ConfigDirectory.Trim()
            : ScanConfig.DefaultOutputDirectory;
        return Path.Combine(Path.GetFullPath(dir), ScanConfig.ConfigFileName);
    }

    private void PersistConfig()
    {
        try
        {
            var path = ActiveConfPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConfigManager.Save(path, _config);
        }
        catch { /* persisting is best-effort */ }
    }

    /// <summary>The field shows the full file path; only its folder is stored —
    /// the file name is always ip_scanner.conf.</summary>
    private static string ConfDirFromInput(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return ScanConfig.DefaultConfigDirectory;
        if (t.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(t) is { Length: > 0 } dir ? dir : ScanConfig.DefaultConfigDirectory;
        return t;
    }

    private void OnBrowseConfDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.ConfDirLabel,
            Filter = Loc.ConfFileFilter,
            FileName = ScanConfig.ConfigFileName,
            OverwritePrompt = false,
        };
        if (DirOf(ConfDirBox.Text.Length > 0 ? ConfDirBox.Text : ".") is { } dir)
            dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) ConfDirBox.Text = MakeRelative(dlg.FileName);
    }

    private void OnConfDirChanged(object sender, TextChangedEventArgs e)
        => ValidatePath(ConfDirBox, ConfDirError, allowEmpty: true);

    private void ApplyUiScale()
    {
        double f = Math.Clamp(_config.UiScalePercent, 50, 200) / 100.0;
        RootLayout.LayoutTransform = f == 1.0 ? null
            : new System.Windows.Media.ScaleTransform(f, f);
    }

    // MAC/hostname techniques, best-first (index = rank). The engine runs all
    // of them in parallel per device, shows the FIRST result immediately and
    // swaps in a better-ranked one when it arrives later.
    //   MAC:      ARP (rank 0) > NetBIOS (rank 3)
    //   Hostname: reverse DNS (1) > mDNS (2) > NetBIOS (3)
    private static readonly Func<string, (string? mac, string? host)>[] Enrichers =
    {
        ip => (ArpHelper.Resolve(ip), null),
        ip => (null, HostnameResolver.Resolve(ip)),
        ip => (null, MdnsHelper.Resolve(ip, 1500)),
        ip => { var (name, mac) = NetBiosHelper.Lookup(ip); return (mac, name); },
    };

    private async void OnScanStopClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsScanning) _vm.Stop();
        else await StartScan();
    }

    private async void OnSubnetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !_vm.IsScanning) await StartScan();
    }

    private async Task StartScan()
    {
        // Build manual subnet "ip/cidr" from the box + dropdown (empty = auto-detect).
        var raw = SubnetBox.Text.Trim();
        if (raw.Length == 0)
        {
            _vm.ManualSubnet = null;
        }
        else
        {
            string ipPart; int cidr;
            if (raw.Contains('/'))                       // custom mask typed in the box
            {
                var parts = raw.Split('/');
                ipPart = parts[0].Trim();
                cidr = ParseCidr(parts[1]);
            }
            else
            {
                ipPart = raw;
                // Editable dropdown: free text wins, otherwise the selected item.
                var cidrText = string.IsNullOrWhiteSpace(CidrBox.Text)
                    ? (CidrBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                    : CidrBox.Text;
                cidr = ParseCidr(cidrText);
            }
            if (cidr < 24 && !ConfirmLargeRange(cidr)) return;
            _vm.ManualSubnet = $"{ipPart}/{cidr}";
        }

        try
        {
            ApplySelectedPingCount();
            _vm.Config = _config;
            await _vm.RunScanAsync();
            if (raw.Length == 0 && _vm.Networks.Count > 0)
                SubnetBox.Text = _vm.Networks[0].Cidr.Split('/')[0];
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Embedded settings overlay ──
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadSettings(_config);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnSettingsCancel(object sender, RoutedEventArgs e)
    {
        ApplyInstant();
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnOverlayBackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay))
        {
            ApplyInstant();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        // Reset removes the persisted config files and applies the defaults
        // without writing a new file.
        foreach (var p in new[] { ActiveConfPath(), ConfPathFor(_config.DatabasePath),
                                  ConfPathFor(new ScanConfig().DatabasePath) })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        _config = new ScanConfig();
        _vm.Config = _config;
        LoadSettings(_config);
        ApplyUiScale();
        ApplyBarColors();
        ApplyDefaultPingCount();
        ApplyGraphSettings();
    }

    /// <summary>Paths inside the working directory become "./" relative paths.</summary>
    private static string MakeRelative(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var baseDir = Path.GetFullPath(".").TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), baseDir,
                              StringComparison.OrdinalIgnoreCase))
                return "./";
            if (full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "./" + Path.GetRelativePath(baseDir, full).Replace('\\', '/');
            return path;
        }
        catch { return path; }
    }

    /// <summary>Existing directory of a (possibly relative) path, or null.</summary>
    private static string? DirOf(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path);
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private void OnMergeDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.DbFileFilter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = new KnownDevicesDb(_config.DatabasePath).MergeFrom(dlg.FileName);
            MessageBox.Show(this, Loc.MergeDone(n), Loc.MergeDb, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Config file lives next to the database.</summary>
    private static string ConfPathFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", ScanConfig.ConfigFileName);

    /// <summary>Manual hostname/group overrides live next to the database too.</summary>
    private static string OverridesPathFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", ScanConfig.OverridesFileName);

    private static ScanConfig LoadPersistedConfig()
    {
        try
        {
            // 1. Try the default location (next to the exe); fall back to the
            //    pre-2.6 location in the scans folder for existing installs.
            var def = ConfPathFor(new ScanConfig().DatabasePath);
            if (!File.Exists(def))
            {
                var legacy = Path.Combine(Path.GetFullPath(ScanConfig.DefaultOutputDirectory),
                                          ScanConfig.ConfigFileName);
                if (File.Exists(legacy)) def = legacy;
            }
            ConfigManager.MigrateIfNeeded(def);
            var cfg = File.Exists(def) ? ConfigManager.Load(def) : new ScanConfig();

            // 2. If the loaded config has a non-default DB path, its sibling conf wins.
            var atDb = ConfPathFor(cfg.DatabasePath);
            if (!string.Equals(atDb, def, StringComparison.OrdinalIgnoreCase) && File.Exists(atDb))
            {
                ConfigManager.MigrateIfNeeded(atDb);
                cfg = ConfigManager.Load(atDb);
            }

            // 3. A configured conf directory always takes priority.
            var dir = cfg.ConfigDirectory.Trim();
            if (dir.Length > 0)
            {
                var custom = Path.Combine(Path.GetFullPath(dir), ScanConfig.ConfigFileName);
                if (File.Exists(custom))
                {
                    ConfigManager.MigrateIfNeeded(custom);
                    cfg = ConfigManager.Load(custom);
                }
            }
            return cfg;
        }
        catch { return new ScanConfig(); }
    }

    private bool _loadingSettings;

    /// <summary>Instant save: every settings change applies and persists at once.</summary>
    private void ApplyInstant()
    {
        if (_loadingSettings) return;
        var prev = _config.PinnedIps;
        _config = ReadSettings();
        _vm.Config = _config;
        // Avoid ObservableCollection mutations (which cause a layout pass that
        // resets the TextBox caret) unless PinnedIps actually changed.
        if (!_config.PinnedIps.SequenceEqual(prev))
            _vm.RefreshPinnedNames();
        ApplyDefaultPingCount();
        SyncExportToggles();
        ApplyGraphSettings();
        PersistConfig();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => ApplyInstant();

    // Text scale only commits when the field loses focus — applying it on every
    // keystroke would zoom wildly while typing.
    private void OnUiScaleCommit(object sender, RoutedEventArgs e) => ApplyUiScale();

    /// <summary>Preset the ping dropdown from the configured default.</summary>
    // Sentinel so the header ping dropdown is only synced when the configured
    // default actually changes — not on unrelated settings like export toggles.
    private int _lastSyncedDefaultPingCount = int.MinValue;

    private void ApplyDefaultPingCount()
    {
        if (_config.PingCount == _lastSyncedDefaultPingCount) return;
        _lastSyncedDefaultPingCount = _config.PingCount;
        PingCountBox.Text = _config.PingCount == ScanConfig.InfinitePingCount
            ? "∞" : _config.PingCount.ToString();
    }

    private void SyncExportToggles()
    {
        TxtToggle.IsChecked = _config.FileOutput;
        CsvToggle.IsChecked = _config.ExportCsv;
    }

    // Sidebar TXT/CSV pills drive the settings checkboxes (single source of truth).
    private void OnExportToggle(object sender, RoutedEventArgs e)
    {
        FileOutputBox.IsChecked = TxtToggle.IsChecked == true;
        ExportCsvBox.IsChecked = CsvToggle.IsChecked == true;
        ApplyInstant();
    }

    private static readonly string[] LanguageModes = { "auto", "de", "en" };

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageBox.SelectedIndex < 0) return;
        var mode = LanguageModes[LanguageBox.SelectedIndex];
        if (mode == _config.Language) return;
        _config.Language = mode;
        Loc.SetLanguage(mode);
        PersistConfig();
        LocSource.Instance.Refresh();   // re-evaluates all {loc:L} bindings
        RefreshDynamicTexts();
    }

    /// <summary>Re-applies texts that are set from code (not via bindings).</summary>
    private void RefreshDynamicTexts()
    {
        _loadingSettings = true;
        try
        {
            LoadSettings(_config);   // language combo items, chip tooltips
        }
        finally { _loadingSettings = false; }
        UpdateScanButton();
        _vm.UpdateGraphSources();            // relabels the "all devices" entry
        _vm.RefreshAllRows();                // ONLINE/OFFLINE status texts
    }

    private void OnExportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.ConfFileFilter,
            FileName = ScanConfig.ConfigFileName,
        };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        try { ConfigManager.Save(dlg.FileName, ReadSettings()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnImportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.ConfFileFilter };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LoadSettings(ConfigManager.Load(dlg.FileName));
            ApplyInstant();   // imported values take effect + persist immediately
            ApplyUiScale();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadSettings(ScanConfig c)
    {
        _loadingSettings = true;
        try
        {
        _subnetChips.Load(c.Subnets);
        _pinnedChips.Load(c.PinnedIps);
        GraphsEnabledBox.IsChecked = c.GraphsEnabled;
        NetworkGraphsBox.IsChecked = c.NetworkGraphsEnabled;
        GraphMaxBox.Text = c.GraphMaxSeconds.ToString();
        ScanThreadsBox.Text = c.ScanThreads.ToString();
        UiScaleBox.Text = c.UiScalePercent.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        StartupPingsBox.Text = c.StartupPingCount.ToString();
        DefaultPingsBox.Text = c.PingCount.ToString();
        OfflineRecheckBox.Text = c.OfflineRecheckSeconds.ToString();
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetTimeoutBox.Text = c.InternetTimeoutMs.ToString();
        _hostChips.Load(c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        DbPathBox.Text = c.DatabasePath;
        ConfDirBox.Text = Path.Combine(
            c.ConfigDirectory.Length > 0 ? c.ConfigDirectory : ScanConfig.DefaultConfigDirectory,
            ScanConfig.ConfigFileName);
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        KnownDbBox.IsChecked = c.KnownDevicesDb;
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageModes, c.Language));
        TxtToggle.IsChecked = c.FileOutput;
        CsvToggle.IsChecked = c.ExportCsv;
        }
        finally { _loadingSettings = false; }
    }

    // ── Live syntax validation for the path fields ──
    // Path fields: validate on every keystroke but only persist on LostFocus
    // or overlay close — otherwise Directory.CreateDirectory fires per character.
    private void OnOutputDirValidate(object sender, TextChangedEventArgs e)
        => ValidatePath(OutputDirBox, OutputDirError);

    private void OnDbPathValidate(object sender, TextChangedEventArgs e)
        => ValidatePath(DbPathBox, DbPathError);

    private void OnPathFieldCommit(object sender, RoutedEventArgs e) => ApplyInstant();

    private static bool IsValidPathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { Path.GetFullPath(path); return true; }
        catch { return false; }
    }

    private void ValidatePath(TextBox box, TextBlock error, bool allowEmpty = false)
    {
        bool ok = (allowEmpty && box.Text.Trim().Length == 0) || IsValidPathText(box.Text);
        box.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        if (ok)
        {
            error.Visibility = Visibility.Collapsed;
        }
        else
        {
            box.BorderBrush = (System.Windows.Media.Brush)FindResource("Red");
            error.Text = Loc.InvalidPath;
            error.Visibility = Visibility.Visible;
        }
    }

    /// <summary>"a.b.c.d" or "a.b.c.d/1..32".</summary>
    private static bool IsValidSubnetEntry(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length > 2 || !Core.Net.Ipv4.IsValid(parts[0].Trim())) return false;
        return parts.Length == 1 ||
               (int.TryParse(parts[1].Trim(), out var cidr) && cidr is >= 1 and <= 32);
    }

    private ScanConfig ReadSettings()
    {
        int I(string s, int d) => int.TryParse(s.Trim(), out var v) ? v : d;

        int defaultPings = I(DefaultPingsBox.Text, 10);
        return new ScanConfig
        {
            Subnets = _subnetChips.Entries.Select(en => en.ToString()).ToList(),
            PinnedIps = _pinnedChips.Entries.Select(en => en.ToString()).ToList(),
            ConfigDirectory = MakeRelative(ConfDirFromInput(ConfDirBox.Text)),
            GraphsEnabled = GraphsEnabledBox.IsChecked == true,
            NetworkGraphsEnabled = NetworkGraphsBox.IsChecked == true,
            GraphMaxSeconds = Math.Clamp(I(GraphMaxBox.Text, 300), 10, 300),
            PingCount = defaultPings is ScanConfig.InfinitePingCount or > 0 ? defaultPings : 10,
            ScanThreads = I(ScanThreadsBox.Text, 50),
            UiScalePercent = Math.Clamp(I(UiScaleBox.Text, 100), 50, 200),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            StartupPingCount = Math.Clamp(I(StartupPingsBox.Text, 10), 0, 10_000),
            OfflineRecheckSeconds = Math.Clamp(I(OfflineRecheckBox.Text, 5), 0, 3600),
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetTimeoutMs = Math.Clamp(I(InternetTimeoutBox.Text, 1000), 100, 10_000),
            InternetHosts = _hostChips.Entries.Select(en => en.ToString()).ToList(),
            OutputDirectory = MakeRelative(OutputDirBox.Text.Trim()),
            DatabasePath = DbPathBox.Text.Trim().Length > 0
                ? MakeRelative(DbPathBox.Text.Trim()) : ScanConfig.DefaultDatabasePath,
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            KnownDevicesDb = KnownDbBox.IsChecked == true,
            Language = LanguageModes[Math.Max(0, LanguageBox.SelectedIndex)],
            // Colors have no settings UI — carry them over from the live config.
            ColorOnline = _config.ColorOnline, ColorOffline = _config.ColorOffline,
            ColorSuccess = _config.ColorSuccess,
            ColorFailed = _config.ColorFailed, ColorSkipped = _config.ColorSkipped,
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.OutputDir };
        if (DirOf(OutputDirBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) OutputDirBox.Text = MakeRelative(dlg.FolderName);
    }

    private void OnClearDb(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.ClearDbConfirm, Loc.Confirm, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { new KnownDevicesDb(_config.DatabasePath).Clear(); } catch { /* ignore */ }
        }
    }

    private void OnBrowseDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.DbFile,
            Filter = Loc.DbFileFilter,
            FileName = ScanConfig.DefaultDatabaseFileName,
            OverwritePrompt = false,    // existing db is opened, not replaced
        };
        if (DirOf(DbPathBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) DbPathBox.Text = MakeRelative(dlg.FileName);
    }

    // Extract a CIDR number from the dropdown text ("/24", "24", "/16" ...).
    private static int ParseCidr(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= 32 ? n : 24;
    }

    // Warn before scanning a large range (/16 = 256 subnets, /8 = 65536).
    private bool ConfirmLargeRange(int cidr)
    {
        int subnets = cidr >= 16 ? 256 : 65536;
        var r = MessageBox.Show(this, Loc.LargeRangeMsg(cidr, subnets, (long)subnets * 254),
            Loc.LargeRange, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private void ApplySelectedPingCount()
    {
        // Editable dropdown: free text wins, fall back to the selected item.
        var text = string.IsNullOrWhiteSpace(PingCountBox.Text)
            ? PingCountBox.SelectedItem?.ToString()
            : PingCountBox.Text.Trim();
        _config.PingCount = text == "∞" || text == "-1" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) && n > 0 ? n : 10;
    }
}
