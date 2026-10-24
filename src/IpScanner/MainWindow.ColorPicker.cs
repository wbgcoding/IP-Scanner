using System.Windows;
using System.Windows.Controls;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Progress-bar colors (legend squares act as color pickers) ──
    private void ApplyBarColors()
    {
        DevBar.Color1 = BrushFor(_config.ColorOnline);  LegOnline.Background = BrushFor(_config.ColorOnline);
        DevBar.Color2 = BrushFor(_config.ColorOffline); LegOffline.Background = BrushFor(_config.ColorOffline);
        PingBar.Color1 = BrushFor(_config.ColorSuccess); LegSuccess.Background = BrushFor(_config.ColorSuccess);
        PingBar.Color2 = BrushFor(_config.ColorFailed);  LegFailed.Background = BrushFor(_config.ColorFailed);
        PingBar.Color3 = BrushFor(_config.ColorSkipped); LegSkipped.Background = BrushFor(_config.ColorSkipped);
    }

    // ── Themed color picker (popup with palette swatches + hex field) ──
    // The OK button routes the chosen color to whoever opened the popup.
    private Action<string>? _pickerApply;

    private const double SwatchSize = 24;
    private const double SwatchMargin = 3;

    private void InitColorPicker()
    {
        foreach (var hex in Core.Palette.Swatches)
        {
            var swatch = new System.Windows.Controls.Border
            {
                Width = SwatchSize, Height = SwatchSize, CornerRadius = new CornerRadius(6),
                Margin = new Thickness(SwatchMargin), Cursor = System.Windows.Input.Cursors.Hand,
                Background = BrushFor(hex), Tag = hex, ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += OnSwatchPick;
            SwatchPanel.Children.Add(swatch);
        }
        // Fixed content width so the popup's own width (which varies with whether
        // PickerResetBtn is visible) never stretches the grid apart.
        SwatchPanel.Width = SwatchPanel.Columns * (SwatchSize + 2 * SwatchMargin);
    }

    private static System.Windows.Media.SolidColorBrush BrushFor(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>Hex string ("#RRGGBB") of a currently-resolved brush resource -
    /// used to preselect the picker with whatever color is actually on screen.</summary>
    private string HexOfResource(string key)
    {
        var c = ((System.Windows.Media.SolidColorBrush)FindResource(key)).Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ── Accent color: overrides the theme's own "Mauve" highlight
    // (titles, focus border, selection, selected nav item) app-wide. Setting it
    // directly on Application.Resources (rather than inside the merged theme
    // dictionary) means it survives ThemeManager.Switch() untouched - dark/light
    // swap the merged dictionary, this stays. Empty = no override, removed. ──
    private void ApplyAccentColor()
    {
        if (_config.AccentColor.Length == 0) Application.Current.Resources.Remove("Mauve");
        else Application.Current.Resources["Mauve"] = BrushFor(_config.AccentColor);
        AccentColorSwatch.Background = BrushFor(_config.AccentColor.Length > 0 ? _config.AccentColor : HexOfResource("Mauve"));
    }

    private void OnAccentColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var current = _config.AccentColor.Length > 0 ? _config.AccentColor : HexOfResource("Mauve");
        OpenColorPicker(AccentColorSwatch, current,
            hex => { _config.AccentColor = hex; ApplyAccentColor(); PersistConfigDebounced(); },
            () => { _config.AccentColor = ""; ApplyAccentColor(); PersistConfigDebounced(); });
    }

    private void OnLegendColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string key } square) return;
        OpenColorPicker(square, GetBarColor(key), hex =>
        {
            SetBarColor(key, hex);
            ApplyBarColors();
            PersistConfigDebounced();
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
        PlaceAvoidingBottomEdge(ColorPickerPopup, target);
        ColorPickerPopup.IsOpen = true;
    }

    private void OnPickerReset(object sender, RoutedEventArgs e)
    {
        _pickerReset?.Invoke();
        ColorPickerPopup.IsOpen = false;
    }

    private void OnSwatchPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Select only (hex field + preview update) - applied when OK is pressed.
        if (sender is System.Windows.Controls.Border { Tag: string hex })
            HexBox.Text = hex;
    }

    /// <summary>Only one of the entry-edit / cell-edit popups is open at a time -
    /// opening one closes the other instead of letting both pile up on screen.
    /// The color picker is excluded: it's a nested helper opened from within an
    /// already-open entry-edit popup and must not close its parent.</summary>
    private void CloseOtherEditPopups(System.Windows.Controls.Primitives.Popup keep)
    {
        if (!ReferenceEquals(keep, EntryEditPopup)) EntryEditPopup.IsOpen = false;
        if (!ReferenceEquals(keep, CellEditPopup)) CellEditPopup.IsOpen = false;
        if (!ReferenceEquals(keep, ProfileEditPopup)) ProfileEditPopup.IsOpen = false;
    }

    /// <summary>Opens downward by default; flips to open upward when the popup
    /// wouldn't fit between the anchor and the bottom of the window.</summary>
    private void PlaceAvoidingBottomEdge(System.Windows.Controls.Primitives.Popup popup, UIElement anchor)
    {
        if (popup.Child is not FrameworkElement child) { popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; return; }
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double neededHeight = child.DesiredSize.Height + 12;
        double anchorBottom = anchor.TransformToAncestor(this).Transform(new Point(0, anchor.RenderSize.Height)).Y;
        popup.Placement = ActualHeight - anchorBottom < neededHeight
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
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

    /// <summary>Validated, '#'-prefixed, upper-case hex color, or null if invalid.</summary>
    private static string? NormalizeHex(string s) =>
        Core.Palette.IsHexColor(s) ? "#" + s.Trim().TrimStart('#').ToUpperInvariant() : null;

    private void OnHexColorChanged(object sender, TextChangedEventArgs e)
    {
        if (NormalizeHex(HexBox.Text) is { } hex) HexPreview.Background = BrushFor(hex);
    }

    private void OnHexColorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnHexColorApply(sender, e);
    }

    private void OnHexColorApply(object sender, RoutedEventArgs e)
    {
        if (NormalizeHex(HexBox.Text) is { } hex) ApplyPickedColor(hex);
    }

    private void ApplyPickedColor(string hex)
    {
        _pickerApply?.Invoke(hex);
        ColorPickerPopup.IsOpen = false;
    }

}
