using System.Linq;
using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── System tray (optional; toggled in settings) ──
    private System.Windows.Forms.NotifyIcon? _tray;
    // Maximized or normal before the last minimize, so a tray restore keeps it.
    private readonly TrayWindowState _trayState = new();
    // Auto-repeat scan timer, rebuilt whenever RepeatScanMinutes changes.
    private System.Windows.Threading.DispatcherTimer? _repeatScanTimer;

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Text = Loc.AppTitle, Visible = false };
        try
        {
            using var s = Application.GetResourceStream(new Uri("Resources/network.ico", UriKind.Relative))?.Stream;
            if (s is not null) _tray.Icon = new System.Drawing.Icon(s);
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Loading the tray icon failed", ex); /* fall back to no icon */ }
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.TrayOpen, null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.Close, null, (_, _) => { _closing = true; Close(); });
        _tray.ContextMenuStrip = menu;
        StateChanged += (_, _) =>
        {
            _trayState.Observe(WindowState);
            if (WindowState == WindowState.Minimized && _config.MinimizeToTray) Hide();
            UpdateTrayVisibility();
        };

        // Balloon on scan end / newly found device.
        _vm.NewUnknownDeviceFound += OnNewUnknownDevice;
        _vm.DeviceAlert += OnDeviceAlert;
    }

    /// <summary>Balloon for a watched device that stopped answering or answers again. Shown while
    /// either tray notification setting is on; the log line is written regardless.</summary>
    private void OnDeviceAlert(string name, Core.Scanner.OfflineAlert alert)
    {
        if (!(_config.NotifyScanDone || _config.NotifyNewDevice) || _tray is null) return;
        UpdateTrayVisibility();
        bool lost = alert.Kind == Core.Scanner.OfflineAlertKind.Lost;
        _tray.ShowBalloonTip(6000,
            lost ? Loc.TrayDeviceLostTitle : Loc.TrayDeviceBackTitle,
            lost ? string.Format(Loc.TrayDeviceLostTextFmt, name)
                 : string.Format(Loc.TrayDeviceBackTextFmt, name, Core.NumberFormat.Duration(alert.Down)),
            lost ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnNewUnknownDevice(ViewModels.DeviceViewModel vm)
    {
        if (!_config.NotifyNewDevice || _tray is null) return;
        UpdateTrayVisibility();
        _tray.ShowBalloonTip(6000, Loc.TrayNewDeviceTitle, Loc.TrayNewDeviceText(vm.Ip, vm.Hostname),
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void DisposeTray()
    {
        var tray = _tray;
        _tray = null;
        tray?.Dispose();
    }

    /// <summary>The tray icon must be visible not only while minimized
    /// to the tray, but also whenever a balloon notification could fire -
    /// otherwise ShowBalloonTip has no icon to attach itself to.</summary>
    private void UpdateTrayVisibility()
    {
        if (_tray is null) return;
        bool minimizedToTray = _config.MinimizeToTray && WindowState == WindowState.Minimized;
        _tray.Visible = minimizedToTray || _config.NotifyScanDone || _config.NotifyNewDevice;
    }

    private int _appliedRepeatMinutes = -1;

    /// <summary>(Re)builds the auto-repeat-scan timer from _config.
    /// Call whenever RepeatScanMinutes could have changed.</summary>
    private void ApplyRepeatScanTimer()
    {
        // Every settings keystroke lands here; only a changed interval may restart the countdown.
        if (_config.RepeatScanMinutes == _appliedRepeatMinutes) return;
        _appliedRepeatMinutes = _config.RepeatScanMinutes;
        _repeatScanTimer?.Stop();
        if (_config.RepeatScanMinutes <= 0) return;
        _repeatScanTimer ??= new System.Windows.Threading.DispatcherTimer();
        _repeatScanTimer.Interval = TimeSpan.FromMinutes(_config.RepeatScanMinutes);
        _repeatScanTimer.Tick -= OnRepeatScanTick;
        _repeatScanTimer.Tick += OnRepeatScanTick;
        _repeatScanTimer.Start();
    }

    private async void OnRepeatScanTick(object? sender, EventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_vm.IsScanning || _restoreGate.Active) return;   // a skipped tick is simply the next interval's
            try
            {
                ApplySelectedPingCount();
                _vm.Config = _config;
                await _vm.RunRepeatScanAsync();
            }
            catch (Exception ex) { Core.Log.Error(Core.LogArea.Scan, "Repeat scan failed", ex); }
        }, Loc.ScanError);
    }

    /// <summary>Balloon when a scan (including the startup sweep) finishes.</summary>
    private void NotifyScanFinishedIfEnabled()
    {
        if (!_config.NotifyScanDone || _tray is null) return;
        UpdateTrayVisibility();
        int online = _vm.Devices.Count(d => d.IsOnline);
        _tray.ShowBalloonTip(6000, Loc.TrayScanDoneTitle, Loc.TrayScanDoneText(online),
            System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Re-localizes the tray icon tooltip and its context menu after a language switch.</summary>
    private void RefreshTrayLanguage()
    {
        if (_tray is null) return;
        _tray.Text = Loc.AppTitle;
        if (_tray.ContextMenuStrip?.Items.Count == 2)
        {
            _tray.ContextMenuStrip.Items[0].Text = Loc.TrayOpen;
            _tray.ContextMenuStrip.Items[1].Text = Loc.Close;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = _trayState.RestoreTarget;
        Activate();
        UpdateTrayVisibility();
    }

    private void UpdateScanButton()
    {
        bool scanning = _vm.IsScanning;
        ScanStopButton.Content = scanning ? Loc.Stop : Loc.Scan;
        ScanStopButton.Style = (Style)FindResource(scanning ? "DangerButton" : "AccentButton");

        // Spin while scanning - faster with more threads, eased in/out.
        int threads = _config.ScanThreads <= 0 ? Core.Net.Ipv4.HostsPerSubnet : _config.ScanThreads;
        double seconds = Math.Clamp(120.0 / threads, 0.6, 6.0);
        Logo.SetSpeed(scanning ? 360.0 / seconds : 0.0);
    }

}
