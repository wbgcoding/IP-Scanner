using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IpScanner.Core.Cli;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;

namespace IpScanner;

public partial class MainWindow
{
    private const string SeedPortList = "22,80,443";

    // Port mode entries; the position is the combo index.
    private const int PortsModeStandard = 0, PortsModeFast = 1, PortsModeAll = 2, PortsModeList = 3;

    private IReadOnlyList<ScanPreset> _presetList = [];
    private bool _presetUiSync;

    /// <summary>Fills the preset and port-mode lists, selects the entry that matches the settings
    /// (or the custom entry) and rewrites the equivalent command.</summary>
    private void RefreshPresetUi(ScanConfig cfg)
    {
        var wasSync = _presetUiSync;
        _presetUiSync = true;
        try
        {
            _presetList = ScanPresets.All(cfg);
            var names = _presetList.Select(p => p.Name).Append(Loc.PresetCustom).ToList();
            if (!ScanPresetBox.Items.Cast<object>().Select(o => o?.ToString()).SequenceEqual(names))
            {
                ScanPresetBox.Items.Clear();
                foreach (var n in names) ScanPresetBox.Items.Add(n);
            }
            var match = ScanPresets.Match(cfg);
            int idx = match is null ? _presetList.Count : _presetList.ToList().FindIndex(p => p.Name == match.Name && p.Flags == match.Flags);
            ScanPresetBox.SelectedIndex = idx < 0 ? _presetList.Count : idx;
            PresetDeleteBtn.IsEnabled = ScanPresetBox.SelectedIndex < _presetList.Count && !_presetList[ScanPresetBox.SelectedIndex].BuiltIn;
            PresetSaveBtn.IsEnabled = cfg.ScanPresets.Count < ScanPresets.MaxUserPresets;

            var modes = new[] { Loc.PortsStandardOption, Loc.PortsFastOption, Loc.PortsAllOption, Loc.PortsListOption };
            if (!ScanPortsModeBox.Items.Cast<object>().Select(o => o?.ToString()).SequenceEqual(modes))
            {
                ScanPortsModeBox.Items.Clear();
                foreach (var m in modes) ScanPortsModeBox.Items.Add(m);
            }
            PresetCommandBox.Text = ScanPresets.Command(ScanPlan.FromConfig(cfg), PresetPreviewTarget(cfg));
            UpdatePresetWarning(ScanPlan.FromConfig(cfg));
        }
        finally { _presetUiSync = wasSync; }
    }

    /// <summary>Marks the preset line when the chosen plan is noisy on the network.</summary>
    private void UpdatePresetWarning(ScanPlan plan)
    {
        var reasons = new List<string>();
        if (plan.Timing >= 4) reasons.Add(Loc.WarnFastTiming);
        if (ScanPlan.NormalizePorts(plan.Ports) == "-") reasons.Add(Loc.WarnFullRange);
        PresetWarn.ToolTip = string.Join(" ", reasons);
        PresetWarn.Visibility = reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The network the header would scan, so the copied command targets the user's own network.</summary>
    private string PresetPreviewTarget(ScanConfig cfg)
        => ScanPresets.PreviewTarget(SubnetBox?.Text, CidrBox?.Text, _vm?.DetectedCidr,
            cfg.Subnets.Select(entry => NetEntry.Parse(entry).Target));

    private void RefreshPresetCommand() => RefreshPresetUi(_config);

    /// <summary>Shows the stored port text in the mode box and the list box.</summary>
    private void SetPortsUi(string? range)
    {
        var text = ScanPlan.NormalizePorts(range);
        int mode = text.Length == 0 ? PortsModeStandard
            : text == ScanPlan.PortsFast ? PortsModeFast
            : text == "-" ? PortsModeAll : PortsModeList;
        var wasSync = _presetUiSync;
        _presetUiSync = true;
        try
        {
            ScanPortsModeBox.SelectedIndex = mode;
            if (mode == PortsModeList && ScanPortsListBox.Text != text) ScanPortsListBox.Text = text;
            ScanPortsListBox.Visibility = mode == PortsModeList ? Visibility.Visible : Visibility.Collapsed;
            PortsAllWarn.Visibility = mode == PortsModeAll ? Visibility.Visible : Visibility.Collapsed;
            if (mode != PortsModeList) ScanPortsError.Visibility = Visibility.Collapsed;
        }
        finally { _presetUiSync = wasSync; }
    }

    /// <summary>The port text the controls ask for; an invalid list keeps <paramref name="previous"/>.</summary>
    private string ReadPortRange(string previous)
    {
        switch (ScanPortsModeBox.SelectedIndex)
        {
            case PortsModeStandard: return ScanPlan.PortsStandard;
            case PortsModeFast: return ScanPlan.PortsFast;
            case PortsModeAll: return "-";
            case PortsModeList:
                var text = ScanPortsListBox.Text.Trim();
                if (text.Length > 0 && ScanPlan.NormalizePorts(text) == text)
                {
                    ScanPortsError.Visibility = Visibility.Collapsed;
                    return text;
                }
                ScanPortsError.Text = Loc.PortsListInvalid;
                ScanPortsError.Visibility = Visibility.Visible;
                return previous;
            default: return previous;
        }
    }

    private void OnScanPortsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presetUiSync || _loadingSettings || !IsLoaded) return;
        bool list = ScanPortsModeBox.SelectedIndex == PortsModeList;
        ScanPortsListBox.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        PortsAllWarn.Visibility = ScanPortsModeBox.SelectedIndex == PortsModeAll ? Visibility.Visible : Visibility.Collapsed;
        if (list && ScanPortsListBox.Text.Trim().Length == 0) ScanPortsListBox.Text = SeedPortList;
        if (!list) ScanPortsError.Visibility = Visibility.Collapsed;
        ApplyInstant();
    }

    private void OnScanPortsListChanged(object sender, TextChangedEventArgs e)
    {
        if (_presetUiSync || _loadingSettings || !IsLoaded) return;
        ApplyInstant();
    }

    private void OnScanPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presetUiSync || _loadingSettings || !IsLoaded) return;
        int idx = ScanPresetBox.SelectedIndex;
        if (idx < 0 || idx >= _presetList.Count || ScanPresets.PlanOf(_presetList[idx]) is not { } plan)
        {
            RefreshPresetUi(_config);
            return;
        }
        var target = ReadSettings();
        plan.ApplyTo(target);
        _loadingSettings = true;
        try
        {
            OsFingerprintBox.IsChecked = target.OsFingerprint;
            ExportPortsBox.IsChecked = target.ExportPorts;
            ServiceDetectionBox.IsChecked = target.PortServiceDetection;
            RefreshDiscoveryRows(target.DiscoveryModes);
            SetPortsUi(target.ScanPortRange);
            PortScanTimingSlider.Value = target.PortScanTiming;
            UpdatePortScanTimingSummary();
            LayTimingTemplateIntoBoxes();
        }
        finally { _loadingSettings = false; }
        ApplyInstant();
    }

    private void OnPresetSaveStart(object sender, RoutedEventArgs e)
    {
        PresetNameBox.Text = "";
        PresetNameError.Visibility = Visibility.Collapsed;
        PresetNamePanel.Visibility = Visibility.Visible;
        PresetNameBox.Focus();
    }

    private void OnPresetSaveCancel(object sender, RoutedEventArgs e) => PresetNamePanel.Visibility = Visibility.Collapsed;

    private void OnPresetNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnPresetSaveConfirm(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; OnPresetSaveCancel(sender, e); }
    }

    private void OnPresetSaveConfirm(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        var error = ScanPresets.ValidateName(name, ScanPresets.All(_config).Select(p => p.Name));
        if (error is null && _config.ScanPresets.Count >= ScanPresets.MaxUserPresets) error = Loc.TipPresetSave;
        if (error is not null)
        {
            PresetNameError.Text = error;
            PresetNameError.Visibility = Visibility.Visible;
            return;
        }
        _config.ScanPresets.Add(ScanPresets.ToEntry(name, ScanPlan.FromConfig(_config)));
        PresetNamePanel.Visibility = Visibility.Collapsed;
        RefreshPresetUi(_config);
        PersistConfigDebounced();
    }

    private void OnPresetDelete(object sender, RoutedEventArgs e)
    {
        int idx = ScanPresetBox.SelectedIndex;
        if (idx < 0 || idx >= _presetList.Count || _presetList[idx].BuiltIn) return;
        var chosen = _presetList[idx];
        int at = _config.ScanPresets.FindIndex(en =>
            ScanPresets.TryParseEntry(en, out var p) && p.Name == chosen.Name && p.Flags == chosen.Flags);
        if (at < 0) return;
        _config.ScanPresets.RemoveAt(at);
        RefreshPresetUi(_config);
        PersistConfigDebounced();
    }

    private void OnCopyPresetCommand(object sender, RoutedEventArgs e) => Copy(PresetCommandBox.Text, Loc.CommandCopied);
}
