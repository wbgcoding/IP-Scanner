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

/// <summary>Selectable graph source: overall average (Ip = null) or one device.</summary>
public sealed record GraphSource(string Label, string? Ip)
{
    public override string ToString() => Label;
}

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

        // Stopping: glide the rest of the turn so the logo always comes to
        // rest in its starting position instead of freezing mid-rotation.
        if (_spinTarget == 0 && Math.Abs(_spinSpeed) < 3)
        {
            double remaining = (360 - _spinAngle) % 360;
            double step = Math.Clamp(remaining * 1.5, 12, 120) * dt;   // ease-out
            if (step >= remaining)
            {
                _spinAngle = 0;
                _spinSpeed = 0;
                _logoSpin.Angle = 0;
                LogoGlow.Opacity = 0;
                return;
            }
            _spinAngle += step;
            _logoSpin.Angle = _spinAngle;
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

    private void OpenColorPicker(UIElement target, string currentHex, Action<string> apply)
    {
        _pickerApply = apply;
        HexBox.Text = currentHex;
        ColorPickerPopup.PlacementTarget = target;
        ColorPickerPopup.IsOpen = true;
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
            RandomColor();
        }

        public void Load(IEnumerable<string> raw)
        {
            Entries.Clear();
            Entries.AddRange(raw.Select(NetEntry.Parse).Where(en => en.Target.Length > 0));
            Reset();
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

    // ── Cell editing: hostname & group via popup (double-click; ✕ = back to auto) ──
    private DeviceViewModel? _cellEditVm;
    private bool _cellEditIsGroup;

    private static DeviceViewModel? RowVm(object sender) =>
        (sender as FrameworkElement)?.DataContext as DeviceViewModel;

    private void OnHostnameCellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { } vm) return;
        var h = vm.Model.Hostname;
        OpenCellEdit(vm, isGroup: false, h is null or Device.Unknown ? "" : h,
                     (UIElement)sender, (sender as FrameworkElement)?.ActualWidth ?? 0);
        e.Handled = true;
    }

    private void OnGroupCellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RowVm(sender) is not { } vm) return;
        OpenCellEdit(vm, isGroup: true, Core.Export.CsvExporter.ExportGroup(vm.GroupId),
                     (UIElement)sender, 0);
        e.Handled = true;
    }

    private void OpenCellEdit(DeviceViewModel vm, bool isGroup, string text,
                              UIElement target, double cellWidth)
    {
        _cellEditVm = vm;
        _cellEditIsGroup = isGroup;
        CellEditBox.Text = text;
        CellEditBox.MinWidth = Math.Max(170, cellWidth);   // span the column width
        CellEditPopup.PlacementTarget = target;
        CellEditPopup.IsOpen = true;
        CellEditBox.Focus();
        CellEditBox.SelectAll();
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
            if (_cellEditIsGroup)
            {
                if (int.TryParse(CellEditBox.Text.Trim(), out var g) && g >= 0)
                    _vm.SetGroupOverride(vm, g);
            }
            else
            {
                _vm.SetHostnameOverride(vm, CellEditBox.Text);
            }
        }
        CellEditPopup.IsOpen = false;
    }

    /// <summary>✕ in the popup: drop the manual value, back to the automatic one.</summary>
    private void OnCellEditReset(object sender, RoutedEventArgs e)
    {
        if (_cellEditVm is { } vm)
        {
            if (_cellEditIsGroup) _vm.SetGroupOverride(vm, null);
            else _vm.SetHostnameOverride(vm, null);
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

    // ── Latency history graphs (device graph at the bottom, internet mini graph) ──
    private readonly List<double?> _graphSamples = new();
    private readonly List<double?> _inetSamples = new();
    private string? _graphIp;                        // null = overall average

    /// <summary>Visible time window in samples (one per second).</summary>
    private int GraphWindowSeconds => Math.Clamp(_config.GraphMaxSeconds, 10, 300);

    /// <summary>Show/hide both graphs per settings.</summary>
    private void ApplyGraphSettings()
    {
        var vis = _config.GraphsEnabled ? Visibility.Visible : Visibility.Collapsed;
        GraphSection.Visibility = vis;
        InetGraphSection.Visibility = vis;
    }

    private void InitGraph()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => OnGraphTick();
        timer.Start();
    }

    private void OnGraphTick()
    {
        if (!_config.GraphsEnabled) return;
        RefreshGraphSources();
        Sample(_graphSamples, _vm.GraphValue(_graphIp));
        Sample(_inetSamples, _vm.InternetGraphValue());
        RenderGraph();
        RenderInternetGraph();
    }

    private void Sample(List<double?> series, double? value)
    {
        series.Add(value);
        while (series.Count > GraphWindowSeconds) series.RemoveAt(0);
    }

    /// <summary>Keep the source dropdown in sync with the device list.
    /// Labels show the hostname when known, otherwise the IP.</summary>
    private void RefreshGraphSources()
    {
        if (GraphSourceBox.IsDropDownOpen) return;   // don't yank an open list
        var items = new List<GraphSource> { new(Loc.AllDevices, null) };
        items.AddRange(_vm.Devices.Select(d =>
            new GraphSource(d.Hostname != "—" ? d.Hostname : d.Ip, d.Ip)));
        if (GraphSourceBox.ItemsSource is List<GraphSource> cur &&
            cur.Count == items.Count && cur.Zip(items).All(p => p.First == p.Second)) return;
        GraphSourceBox.ItemsSource = items;
        int idx = items.FindIndex(i => i.Ip == _graphIp);
        GraphSourceBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnGraphSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        var ip = (GraphSourceBox.SelectedItem as GraphSource)?.Ip;
        if (ip == _graphIp) return;
        _graphIp = ip;
        _graphSamples.Clear();   // fresh line for the new source
        RenderGraph();
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderGraph();
        RenderInternetGraph();
    }

    private void RenderGraph()
    {
        RenderSeries(_graphSamples, GraphCanvas, GraphLine, GraphMaxText, GraphMinText, GraphTimeText);
        var last = _graphSamples.Count > 0 ? _graphSamples[^1] : null;
        GraphValueText.Text = Core.NumberFormat.Ms(last);
        GraphValueText.Foreground = BrushFor(Core.Palette.Heat(last));
    }

    private void RenderInternetGraph()
        => RenderSeries(_inetSamples, InetGraphCanvas, InetGraphLine,
                        InetGraphMaxText, InetGraphMinText, InetGraphTimeText);

    /// <summary>Covered time span: "45 s" / "2,5 min".</summary>
    private static string SpanText(int seconds) => seconds < 120
        ? $"{seconds} s"
        : (seconds / 60.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " min";

    /// <summary>Plot a sample series: line stretches over the full width, newest
    /// right, gaps for nulls; the plot floor stays above the min label.</summary>
    private void RenderSeries(List<double?> samples, System.Windows.Controls.Canvas canvas,
                              System.Windows.Shapes.Polyline line,
                              TextBlock maxText, TextBlock minText, TextBlock timeText)
    {
        const double padTop = 4, padBottom = 13;   // floor above the min label
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        var points = new System.Windows.Media.PointCollection();
        var present = samples.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (w > 0 && h > 0 && present.Count > 1)
        {
            double min = present.Min(), max = present.Max();
            if (max - min < 0.5) { max += 0.5; min = Math.Max(0, min - 0.5); }   // flat-line guard
            double stepX = w / (samples.Count - 1);
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } v) continue;
                double y = h - padBottom - (v - min) / (max - min) * (h - padTop - padBottom);
                points.Add(new Point(i * stepX, y));
            }
            maxText.Text = Core.NumberFormat.Ms(max);
            minText.Text = Core.NumberFormat.Ms(min);
        }
        else
        {
            maxText.Text = "";
            minText.Text = "";
        }
        line.Points = points;
        timeText.Text = samples.Count > 1 ? Loc.TimeWindow(SpanText(samples.Count)) : "";
    }

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

    /// <summary>Active config path: the chosen folder, or next to the database.</summary>
    private string ActiveConfPath() => _config.ConfigDirectory.Trim().Length > 0
        ? Path.Combine(Path.GetFullPath(_config.ConfigDirectory.Trim()), ScanConfig.ConfigFileName)
        : ConfPathFor(_config.DatabasePath);

    private void PersistConfig()
    {
        try
        {
            var path = ActiveConfPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConfigManager.Save(path, _config);
            // Keep a copy at the default location so the next start finds the
            // config_directory redirect.
            var def = ConfPathFor(_config.DatabasePath);
            if (!string.Equals(def, path, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(def)!);
                ConfigManager.Save(def, _config);
            }
        }
        catch { /* persisting is best-effort */ }
    }

    private void OnConfLocation(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.ConfLocation };
        if (DirOf(_config.ConfigDirectory.Length > 0 ? _config.ConfigDirectory : _config.DatabasePath) is { } dir)
            dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        _config.ConfigDirectory = dlg.FolderName;
        PersistConfig();
        ConfLocationBtn.ToolTip = $"{Loc.TipConfLocation}\n{ActiveConfPath()}";
    }

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
        => SettingsOverlay.Visibility = Visibility.Collapsed;

    // Clicking the dimmed background (not the panel) closes the overlay.
    private void OnOverlayBackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay))
            SettingsOverlay.Visibility = Visibility.Collapsed;
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
            var def = ConfPathFor(new ScanConfig().DatabasePath);
            var cfg = File.Exists(def) ? ConfigManager.Load(def) : new ScanConfig();
            // The loaded config may point to a different db folder — its conf wins.
            var at = ConfPathFor(cfg.DatabasePath);
            if (!string.Equals(at, def, StringComparison.OrdinalIgnoreCase) && File.Exists(at))
                cfg = ConfigManager.Load(at);
            // A configured config folder wins over both default locations.
            if (cfg.ConfigDirectory.Trim().Length > 0)
            {
                var redirected = Path.Combine(Path.GetFullPath(cfg.ConfigDirectory.Trim()),
                                              ScanConfig.ConfigFileName);
                if (File.Exists(redirected)) cfg = ConfigManager.Load(redirected);
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
        _config = ReadSettings();
        _vm.Config = _config;
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
    private void ApplyDefaultPingCount()
        => PingCountBox.Text = _config.PingCount == ScanConfig.InfinitePingCount
            ? "∞" : _config.PingCount.ToString();

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
        PersistConfig();
        // x:Static strings are cached — restart so the new language shows everywhere.
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
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
    private void OnOutputDirValidate(object sender, TextChangedEventArgs e)
    { ValidatePath(OutputDirBox, OutputDirError); ApplyInstant(); }

    private void OnDbPathValidate(object sender, TextChangedEventArgs e)
    { ValidatePath(DbPathBox, DbPathError); ApplyInstant(); }

    private static bool IsValidPathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { Path.GetFullPath(path); return true; }
        catch { return false; }
    }

    private void ValidatePath(TextBox box, TextBlock error)
    {
        if (IsValidPathText(box.Text))
        {
            box.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
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
            ConfigDirectory = _config.ConfigDirectory,
            GraphsEnabled = GraphsEnabledBox.IsChecked == true,
            GraphMaxSeconds = Math.Clamp(I(GraphMaxBox.Text, 300), 10, 300),
            PingCount = defaultPings is ScanConfig.InfinitePingCount or > 0 ? defaultPings : 10,
            ScanThreads = I(ScanThreadsBox.Text, 50),
            UiScalePercent = Math.Clamp(I(UiScaleBox.Text, 100), 50, 200),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            StartupPingCount = Math.Clamp(I(StartupPingsBox.Text, 5), 0, 10_000),
            OfflineRecheckSeconds = Math.Clamp(I(OfflineRecheckBox.Text, 2), 0, 3600),
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetTimeoutMs = Math.Clamp(I(InternetTimeoutBox.Text, 1000), 100, 10_000),
            InternetHosts = _hostChips.Entries.Select(en => en.ToString()).ToList(),
            OutputDirectory = OutputDirBox.Text.Trim(),
            DatabasePath = DbPathBox.Text.Trim().Length > 0 ? DbPathBox.Text.Trim() : ScanConfig.DefaultDatabasePath,
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
        if (dlg.ShowDialog(this) == true) OutputDirBox.Text = dlg.FolderName;
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
        if (dlg.ShowDialog(this) == true) DbPathBox.Text = dlg.FileName;
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
