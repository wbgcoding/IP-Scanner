using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using IpScanner.Core.Cli;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

public sealed class UnsafeConfigMergeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads ip_scanner.conf (flat key=value, # comments).</summary>
public static class ConfigManager
{
    // All scalar keys that must be present in a fully-written conf file.
    // If any are absent the file is from an older version and gets rewritten.
    private static readonly HashSet<string> RequiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping_count", "ping_interval_ms", "offline_after_failed_pings", "init_ping_count",
        "startup_ping_count", "offline_recheck_seconds", "hostname_retry_minutes", "enable_internet_ping",
        "internet_timeout_ms", "internet_hosts", "known_devices_db", "scan_history", "database_path",
        "config_directory", "network_interface_ip", "graphs_enabled", "network_graphs_enabled", "graph_max_seconds", "output_directory",
        "file_output", "export_csv", "scan_threads", "ui_scale", "language", "check_for_updates", "verbose_log",
        "auto_graph_pinned", "show_offline_db", "minimize_to_tray", "theme", "sidebar_width",
        "color_online", "color_offline", "color_success", "color_failed", "color_skipped",
        "sidebar_exports_expanded", "sidebar_networks_expanded", "sidebar_pinned_expanded", "sidebar_internet_expanded", "table_sort", "table_widths", "manual_export_scope", "manual_export_format", "heat_ms_1", "heat_ms_2", "heat_ms_3", "heat_ms_4",
        "settings_page", "tools_last_page", "accent_color", "date_number_format", "compact_rows", "hidden_columns",
        "repeat_scan_minutes", "notify_scan_done", "notify_new_device", "start_minimized",
        "export_ports", "html_report", "db_retention_days", "archive_retention_days", "archive_backup_retention_days", "archive_compaction_days", "show_skipped_legend",
        "discovery_modes", "tcp_ping_ports", "udp_ping_ports", "os_fingerprint",
        "active_profile", "profile_names",
        "port_scan_connect_timeout_ms", "port_scan_banner_timeout_ms", "port_scan_parallel",
        "custom_ports",
    };

    // Keys that now live in the sealed settings file. Load still reads them (an older conf), a
    // save removes them from the file unless the sealed write is still pending.
    private static readonly HashSet<string> RetiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "snmp_community", "recent_wake_targets", "never_show_admin_hint",
    };

    // The six Settings-overlay categories, in nav order.
    private static readonly string[] SettingsPages = { "Scan", "Display", "Internet", "Graphs", "Data", "App", "About", "Help" };

    // The six Tools-window pages, in nav order.
    private static readonly string[] ToolsPages = { "WakeOnLan", "PortCheck", "DnsLookup", "PingRoute", "Calculator", "MacTool", "Certificate", "HostDiscovery", "ServiceDetection", "OsEstimate", "Checks", "ScanExport" };

    // Table columns that can be hidden - IP/status/hostname stay mandatory.
    // "loss" and "jitter" are hidden by default (see ScanConfig.HiddenColumns).
    private static readonly string[] OptionalColumnKeys =
        { "group", "avg", "min", "max", "last", "loss", "jitter", "progress", "mac", "vendor", "method" };

    /// <summary>If the conf file is missing keys from the current version, rewrite it
    /// with defaults filled in while keeping all existing values. No-op if up to date.</summary>
    public static void MigrateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        var flat = ParseFlat(path);
        if (RequiredKeys.All(flat.ContainsKey)) return;
        try { Save(path, Load(path)); }
        catch (UnsafeConfigMergeException) { /* the next interactive save asks before overwriting */ }
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    public static ScanConfig Load(string path)
    {
        var cfg = new ScanConfig();
        if (!File.Exists(path)) return cfg;

        var flat = ParseFlat(path);
        var subnets = new SortedDictionary<int, string>();
        var presets = new SortedDictionary<int, string>();

        foreach (var (key, value) in flat)
        {
            switch (key)
            {
                case "ping_count":                  cfg.PingCount = ParseInt(value, ScanConfig.InfinitePingCount, ScanConfig.Bounds.PingCountMax, ScanConfig.Bounds.PingCountDefault); break;
                case "ping_interval_ms":            cfg.PingIntervalMs = ParseInt(value, ScanConfig.Bounds.PingIntervalMsMin, ScanConfig.Bounds.PingIntervalMsMax, ScanConfig.Bounds.PingIntervalMsDefault); break;
                case "offline_after_failed_pings":  cfg.OfflineAfterFailedPings = ParseInt(value, ScanConfig.Bounds.OfflineAfterFailedPingsMin, ScanConfig.Bounds.OfflineAfterFailedPingsMax, ScanConfig.Bounds.OfflineAfterFailedPingsDefault); break;
                case "init_ping_count":             cfg.InitPingCount = ParseInt(value, ScanConfig.Bounds.InitPingCountMin, ScanConfig.Bounds.InitPingCountMax, ScanConfig.Bounds.InitPingCountDefault); break;
                case "startup_ping_count":          cfg.StartupPingCount = ParseInt(value, ScanConfig.Bounds.StartupPingCountMin, ScanConfig.Bounds.StartupPingCountMax, ScanConfig.Bounds.StartupPingCountDefault); break;
                case "offline_recheck_seconds":     cfg.OfflineRecheckSeconds = ParseInt(value, ScanConfig.Bounds.OfflineRecheckSecondsMin, ScanConfig.Bounds.OfflineRecheckSecondsMax, ScanConfig.Bounds.OfflineRecheckSecondsDefault); break;
                case "hostname_retry_minutes":      cfg.HostnameRetryMinutes = ParseInt(value, ScanConfig.Bounds.HostnameRetryMinutesMin, ScanConfig.Bounds.HostnameRetryMinutesMax, ScanConfig.Bounds.HostnameRetryMinutesDefault); break;
                case "enable_internet_ping":        cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_timeout_ms":         cfg.InternetTimeoutMs = ParseInt(value, ScanConfig.Bounds.InternetTimeoutMsMin, ScanConfig.Bounds.InternetTimeoutMsMax, ScanConfig.Bounds.InternetTimeoutMsDefault); break;
                case "internet_hosts":              cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db":            cfg.KnownDevicesDb = ParseBool(value); break;
                case "scan_history":                cfg.ScanHistory = ParseBool(value); break;
                case "database_path":               if (value.Length > 0) cfg.DatabasePath = value; break;
                case "config_directory":            cfg.ConfigDirectory = value; break;
                case "network_interface_ip":        cfg.NetworkInterfaceIp = value; break;
                case "graphs_enabled":              cfg.GraphsEnabled = ParseBool(value); break;
                case "network_graphs_enabled":      cfg.NetworkGraphsEnabled = ParseBool(value); break;
                case "auto_graph_pinned":           cfg.AutoGraphForPinned = ParseBool(value); break;
                case "show_offline_db":             cfg.ShowOfflineFromDb = ParseBool(value); break;
                case "minimize_to_tray":            cfg.MinimizeToTray = ParseBool(value); break;
                case "theme":
                    var theme = value.Trim().ToLowerInvariant();
                    cfg.Theme = theme is "auto" or "dark" or "light" ? theme : "dark";
                    break;
                case "graph_max_seconds":           cfg.GraphMaxSeconds = ParseInt(value, ScanConfig.Bounds.GraphMaxSecondsMin, ScanConfig.Bounds.GraphMaxSecondsMax, ScanConfig.Bounds.GraphMaxSecondsDefault); break;
                case "sidebar_width":                cfg.SidebarWidth = ParseInt(value, ScanConfig.Bounds.SidebarWidthMin, ScanConfig.Bounds.SidebarWidthMax, ScanConfig.Bounds.SidebarWidthDefault); break;
                case "sidebar_exports_expanded":     cfg.SidebarExportsExpanded = ParseBool(value); break;
                case "table_sort":                   cfg.TableSort = value; break;
                case "table_widths":                 cfg.TableWidths = value; break;
                case "sidebar_networks_expanded":    cfg.SidebarNetworksExpanded = ParseBool(value); break;
                case "sidebar_pinned_expanded":      cfg.SidebarPinnedExpanded = ParseBool(value); break;
                case "sidebar_internet_expanded":    cfg.SidebarInternetExpanded = ParseBool(value); break;
                case "check_for_updates":           cfg.CheckForUpdates = ParseBool(value); break;
                case "verbose_log":                 cfg.VerboseLog = ParseBool(value); break;
                case "output_directory":            cfg.OutputDirectory = value; break;
                case "file_output":                 cfg.FileOutput = ParseBool(value); break;
                case "export_csv":                  cfg.ExportCsv = ParseBool(value); break;
                case "scan_threads":                cfg.ScanThreads = ParseInt(value, ScanConfig.Bounds.ScanThreadsMin, ScanConfig.Bounds.ScanThreadsMax, ScanConfig.Bounds.ScanThreadsDefault); break;
                case "ui_scale":                    cfg.UiScalePercent = ParseInt(value, ScanConfig.Bounds.UiScalePercentMin, ScanConfig.Bounds.UiScalePercentMax, ScanConfig.Bounds.UiScalePercentDefault); break;
                case "language":
                    var lang = value.Trim().ToLowerInvariant();
                    cfg.Language = lang == "auto" || Core.Localization.Loc.IsSupported(lang) ? lang : "auto";
                    break;
                case "pinned_ips":                  cfg.PinnedIps = ParseIpList(value); break;
                case "color_online":                cfg.ColorOnline = ParseColor(value, cfg.ColorOnline); break;
                case "color_offline":               cfg.ColorOffline = ParseColor(value, cfg.ColorOffline); break;
                case "color_success":               cfg.ColorSuccess = ParseColor(value, cfg.ColorSuccess); break;
                case "color_failed":                cfg.ColorFailed = ParseColor(value, cfg.ColorFailed); break;
                case "color_skipped":               cfg.ColorSkipped = ParseColor(value, cfg.ColorSkipped); break;
                case "heat_ms_1":                   cfg.HeatMs1 = ParseDouble(value, ScanConfig.Bounds.HeatMs1Default); break;
                case "heat_ms_2":                   cfg.HeatMs2 = ParseDouble(value, ScanConfig.Bounds.HeatMs2Default); break;
                case "heat_ms_3":                   cfg.HeatMs3 = ParseDouble(value, ScanConfig.Bounds.HeatMs3Default); break;
                case "heat_ms_4":                   cfg.HeatMs4 = ParseDouble(value, ScanConfig.Bounds.HeatMs4Default); break;
                case "settings_page":                cfg.SettingsPage = SettingsPages.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : "Scan"; break;
                case "manual_export_scope":          cfg.ManualExportScope = Enum.TryParse<Core.Export.ExportScope>(value, true, out var scope) && Enum.IsDefined(scope) ? scope.ToString() : "Filtered"; break;
                case "manual_export_format":         cfg.ManualExportFormat = Enum.TryParse<Core.Export.ExportFormat>(value, true, out var format) && Enum.IsDefined(format) ? format.ToString() : "Csv"; break;
                case "tools_last_page":              cfg.ToolsLastPage = ToolsPages.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : "WakeOnLan"; break;
                case "recent_wake_targets":          cfg.RecentWakeTargets = ParseIpList(value).Take(5).ToList(); break;
                case "accent_color":                 cfg.AccentColor = ParseColor(value, cfg.AccentColor); break;
                case "date_number_format":
                    var dnf = value.Trim().ToLowerInvariant();
                    cfg.DateNumberFormat = dnf == "auto" || Core.Localization.Loc.IsSupported(dnf) ? dnf : "auto";
                    break;
                case "compact_rows":                 cfg.CompactRows = ParseBool(value); break;
                case "hidden_columns":
                    cfg.HiddenColumns = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(k => OptionalColumnKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                    break;
                case "repeat_scan_minutes":
                    var repeatMin = ParseInt(value, 0, int.MaxValue, 0);
                    cfg.RepeatScanMinutes = ScanConfig.Bounds.RepeatScanMinutesOptions.Contains(repeatMin) ? repeatMin : 0;
                    break;
                case "notify_scan_done":                cfg.NotifyScanDone = ParseBool(value); break;
                case "notify_new_device":               cfg.NotifyNewDevice = ParseBool(value); break;
                case "start_minimized":                 cfg.StartMinimized = ParseBool(value); break;
                case "export_ports":                    cfg.ExportPorts = ParseBool(value); break;
                case "html_report":                     cfg.HtmlReport = ParseBool(value); break;
                case "db_retention_days":
                    var retentionDays = ParseInt(value, 0, int.MaxValue, ScanConfig.Bounds.DbRetentionDaysDefault);
                    cfg.DbRetentionDays = ScanConfig.Bounds.DbRetentionDaysOptions.Contains(retentionDays)
                        ? retentionDays : ScanConfig.Bounds.DbRetentionDaysDefault;
                    break;
                case "archive_retention_days":
                    var archiveDays = ParseInt(value, 0, int.MaxValue, ScanConfig.Bounds.ArchiveRetentionDaysDefault);
                    cfg.ArchiveRetentionDays = ScanConfig.Bounds.ArchiveRetentionDaysOptions.Contains(archiveDays)
                        ? archiveDays : ScanConfig.Bounds.ArchiveRetentionDaysDefault;
                    break;
                case "archive_backup_retention_days":
                    // A hand-edited value inside the documented range is kept as is (the settings combo shows it as an extra entry).
                    cfg.ArchiveBackupRetentionDays = ParseInt(value, 0, 3650, ScanConfig.Bounds.ArchiveBackupRetentionDaysDefault);
                    break;
                case "archive_compaction_days":
                    cfg.ArchiveCompactionDays = ParseInt(value, 0, 3650, ScanConfig.Bounds.ArchiveCompactionDaysDefault);
                    break;
                case "show_skipped_legend":              cfg.ShowSkippedLegend = ParseBool(value); break;
                case "discovery_modes":                  cfg.DiscoveryModes = Scanner.Discovery.DiscoveryModes.Format(Scanner.Discovery.DiscoveryModes.Parse(value)); break;
                case "tcp_ping_ports":                   cfg.TcpPingPorts = Scanner.Discovery.DiscoveryModes.NormalizePorts(value, Scanner.Discovery.DiscoveryModes.DefaultTcpPorts); break;
                case "os_fingerprint":                   cfg.OsFingerprint = ParseBool(value); break;
                case "scan_port_range":                  cfg.ScanPortRange = ScanPlan.NormalizePorts(value); break;
                case "port_service_detection":           cfg.PortServiceDetection = ParseBool(value); break;
                case "udp_ping_ports":                   cfg.UdpPingPorts = Scanner.Discovery.DiscoveryModes.NormalizePorts(value, Scanner.Discovery.DiscoveryModes.DefaultUdpPorts); break;
                case "active_profile":                   cfg.ActiveProfile = value; break;
                case "profile_names":                    cfg.ProfileNames = ParseIpList(value); break;
                case "port_scan_connect_timeout_ms":     cfg.PortScanConnectTimeoutMs = ParseInt(value, ScanConfig.Bounds.PortScanConnectTimeoutMsMin, ScanConfig.Bounds.PortScanConnectTimeoutMsMax, ScanConfig.Bounds.PortScanConnectTimeoutMsDefault); break;
                case "port_scan_banner_timeout_ms":      cfg.PortScanBannerTimeoutMs = ParseInt(value, ScanConfig.Bounds.PortScanBannerTimeoutMsMin, ScanConfig.Bounds.PortScanBannerTimeoutMsMax, ScanConfig.Bounds.PortScanBannerTimeoutMsDefault); break;
                case "port_scan_timing":                  cfg.PortScanTiming = ParseInt(value, ScanConfig.Bounds.PortScanTimingMin, ScanConfig.Bounds.PortScanTimingMax, ScanConfig.Bounds.PortScanTimingDefault); break;
                case "port_scan_retries":                 cfg.PortScanRetries = ParseInt(value, ScanConfig.Bounds.PortScanRetriesMin, ScanConfig.Bounds.PortScanRetriesMax, ScanConfig.Bounds.PortScanRetriesDefault); break;
                case "port_scan_delay_ms":                cfg.PortScanDelayMs = ParseInt(value, ScanConfig.Bounds.PortScanDelayMsMin, ScanConfig.Bounds.PortScanDelayMsMax, ScanConfig.Bounds.PortScanDelayMsDefault); break;
                case "port_scan_intensity":               cfg.PortScanIntensity = ParseInt(value, ScanConfig.Bounds.PortScanIntensityMin, ScanConfig.Bounds.PortScanIntensityMax, ScanConfig.Bounds.PortScanIntensityDefault); break;
                case "port_scan_max_rate":                cfg.PortScanMaxRate = ParseInt(value, ScanConfig.Bounds.PortScanMaxRateMin, ScanConfig.Bounds.PortScanMaxRateMax, ScanConfig.Bounds.PortScanMaxRateDefault); break;
                case "port_scan_parallel":                cfg.PortScanParallel = ParseInt(value, ScanConfig.Bounds.PortScanParallelMin, ScanConfig.Bounds.PortScanParallelMax, ScanConfig.Bounds.PortScanParallelDefault); break;
                case "custom_ports":                      cfg.CustomPorts = ParseIpList(value); break;
                case "snmp_community":                    if (value.Length > 0) cfg.SnmpCommunity = value; break;
                default:
                    var pm = Regex.Match(key, @"^scan_presets(?:_(\d+))?$");
                    if (pm.Success)
                    {
                        int pidx = 0;
                        if (value.Length > 0 && (!pm.Groups[1].Success || int.TryParse(pm.Groups[1].Value, out pidx)))
                            presets[pidx] = value;
                        break;
                    }
                    var m = Regex.Match(key, @"^subnet(?:_(\d+))?$");
                    if (m.Success && value.Length > 0)
                    {
                        int idx = 0;
                        if (!m.Groups[1].Success || int.TryParse(m.Groups[1].Value, out idx))
                            subnets[idx] = value;
                    }
                    break;
            }
        }

        cfg.Subnets = subnets.Values.Distinct().ToList();
        cfg.ScanPresets = presets.Values.Distinct().Take(ScanPresets.MaxUserPresets).ToList();
        // A conf from before the archive had its own retention: the archive followed the history
        // setting, so it keeps doing that (0 = unlimited stays unlimited) instead of silently
        // dropping to the new default and purging old runs on the next scan.
        if (!flat.ContainsKey("archive_retention_days")) cfg.ArchiveRetentionDays = cfg.DbRetentionDays;
        EnsureAscending(cfg);
        // An active_profile missing from profile_names (hand edit, or a name that an old
        // list separator split apart) is added to the list: falling back to the default
        // profile would let the next profile switch overwrite the default lists.
        if (cfg.ActiveProfile.Length > 0 && !cfg.ProfileNames.Contains(cfg.ActiveProfile))
            cfg.ProfileNames.Add(cfg.ActiveProfile);
        return cfg;
    }

    /// <summary>Palette.Heat assumes HeatMs1..4 are ascending - a hand-edited
    /// .conf with them out of order is corrected silently on load rather than
    /// producing a heatmap that gets colder as latency rises.</summary>
    private static void EnsureAscending(ScanConfig cfg)
    {
        var sorted = new[] { cfg.HeatMs1, cfg.HeatMs2, cfg.HeatMs3, cfg.HeatMs4 }.OrderBy(v => v).ToArray();
        cfg.HeatMs1 = sorted[0]; cfg.HeatMs2 = sorted[1]; cfg.HeatMs3 = sorted[2]; cfg.HeatMs4 = sorted[3];
    }

    public static bool Save(string path, ScanConfig c, Func<bool>? confirmOverwrite = null)
    {
        string B(bool v) => v ? "true" : "false";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("#  IP-Scanner configuration");
        sb.AppendLine("#  Lines starting with # are comments. Invalid values fall back to defaults.");
        sb.AppendLine(Loc.German
            ? "#  Von Hand eingefügte Kommentare bleiben erhalten."
            : "#  Comments you add by hand are preserved.");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();

        sb.AppendLine("# -- Network -------------------------------------------------");
        sb.AppendLine("# subnet, subnet_2, ...  Networks to scan (CIDR). Empty = auto-detect.");
        if (c.Subnets.Count == 0)
            sb.AppendLine("#subnet = 192.168.1.0/24");
        for (int i = 0; i < c.Subnets.Count; i++)
            sb.AppendLine(i == 0 ? $"subnet = {c.Subnets[i]}" : $"subnet_{i + 1} = {c.Subnets[i]}");
        sb.AppendLine("# pinned_ips  Always ping and show at the top. Comma-separated.");
        sb.AppendLine(c.PinnedIps.Count > 0 ? $"pinned_ips = {string.Join(", ", c.PinnedIps)}" : "#pinned_ips = 192.168.1.1, 192.168.1.10");
        sb.AppendLine();

        sb.AppendLine("# -- Ping behaviour ------------------------------------------");
        sb.AppendLine("# ping_count  Pings per device (1-10000000). Default 10.");
        sb.AppendLine($"ping_count = {c.PingCount}");
        sb.AppendLine("# ping_interval_ms  Pause between pings of the same host (0-10000 ms). Default 100.");
        sb.AppendLine($"ping_interval_ms = {c.PingIntervalMs}");
        sb.AppendLine("# offline_after_failed_pings  Mark offline after N failed pings (1-100). Default 5.");
        sb.AppendLine($"offline_after_failed_pings = {c.OfflineAfterFailedPings}");
        sb.AppendLine("# init_ping_count  Pings per IP during discovery (1-100). Default 1.");
        sb.AppendLine($"init_ping_count = {c.InitPingCount}");
        sb.AppendLine("# startup_ping_count  Pings per device for the auto-scan after launch (0-10000). Default 10.");
        sb.AppendLine($"startup_ping_count = {c.StartupPingCount}");
        sb.AppendLine("# offline_recheck_seconds  Re-check offline IPs every N seconds (0 = off). Default 5.");
        sb.AppendLine($"offline_recheck_seconds = {c.OfflineRecheckSeconds}");
        sb.AppendLine("# hostname_retry_minutes  Retry hostname lookup for nameless devices every N minutes, independent of any active scan (0 = off). Default 10.");
        sb.AppendLine($"hostname_retry_minutes = {c.HostnameRetryMinutes}");
        sb.AppendLine();

        sb.AppendLine("# -- Internet latency ----------------------------------------");
        sb.AppendLine("# enable_internet_ping  Also ping public hosts. Default true.");
        sb.AppendLine($"enable_internet_ping = {B(c.EnableInternetPing)}");
        sb.AppendLine("# internet_timeout_ms  Ping timeout for internet hosts (100-10000 ms). Default 1000.");
        sb.AppendLine($"internet_timeout_ms = {c.InternetTimeoutMs}");
        sb.AppendLine("# internet_hosts  IPs to measure, comma-separated.");
        sb.AppendLine($"internet_hosts = {string.Join(", ", c.InternetHosts)}");
        sb.AppendLine();

        sb.AppendLine("# -- Graphs --------------------------------------------------");
        sb.AppendLine("# graphs_enabled  Show latency graphs in the sidebar. Default true.");
        sb.AppendLine($"graphs_enabled = {B(c.GraphsEnabled)}");
        sb.AppendLine("# network_graphs_enabled  Show a latency history under each network. Default true.");
        sb.AppendLine($"network_graphs_enabled = {B(c.NetworkGraphsEnabled)}");
        sb.AppendLine("# auto_graph_pinned  Open the latency graph automatically for pinned devices. Default false.");
        sb.AppendLine($"auto_graph_pinned = {B(c.AutoGraphForPinned)}");
        sb.AppendLine("# show_offline_db  Show known devices from the database that are currently offline. Default true.");
        sb.AppendLine($"show_offline_db = {B(c.ShowOfflineFromDb)}");
        sb.AppendLine("# minimize_to_tray  Minimize to the system tray instead of the taskbar. Default false.");
        sb.AppendLine($"minimize_to_tray = {B(c.MinimizeToTray)}");
        sb.AppendLine("# theme  UI colors: auto (follow Windows), dark or light. Default dark. Applied after restart.");
        sb.AppendLine($"theme = {c.Theme}");
        sb.AppendLine("# graph_max_seconds  Visible time span of the graphs (10-300 s). Default 300.");
        sb.AppendLine($"graph_max_seconds = {c.GraphMaxSeconds}");
        sb.AppendLine("# sidebar_width  Sidebar width in pixels (280-560), drag the handle to resize. Default 340.");
        sb.AppendLine($"sidebar_width = {c.SidebarWidth}");
        sb.AppendLine("# sidebar_exports_expanded  Whether the sidebar's recent-files section starts expanded. Default false.");
        sb.AppendLine($"sidebar_exports_expanded = {B(c.SidebarExportsExpanded)}");
        sb.AppendLine("# table_sort  Last device-table column sorted plus direction, e.g. \"Avg:desc\". Empty = no explicit sort yet.");
        sb.AppendLine($"table_sort = {c.TableSort}");
        sb.AppendLine("# table_widths  Per-column pixel widths as \"name:width,name:width,...\". Unknown names are ignored.");
        sb.AppendLine($"table_widths = {c.TableWidths}");
        sb.AppendLine("# sidebar_networks_expanded / sidebar_pinned_expanded / sidebar_internet_expanded");
        sb.AppendLine("# Whether the sidebar's subnets/pinned-IPs/internet-hosts sections start expanded. Default true.");
        sb.AppendLine($"sidebar_networks_expanded = {B(c.SidebarNetworksExpanded)}");
        sb.AppendLine($"sidebar_pinned_expanded = {B(c.SidebarPinnedExpanded)}");
        sb.AppendLine($"sidebar_internet_expanded = {B(c.SidebarInternetExpanded)}");
        sb.AppendLine();

        sb.AppendLine("# -- Output --------------------------------------------------");
        sb.AppendLine(@"# output_directory  Where reports are saved. Default .\Scans");
        sb.AppendLine($"output_directory = {c.OutputDirectory}");
        sb.AppendLine("# file_output  Write a TXT report after each scan. Default false.");
        sb.AppendLine($"file_output = {B(c.FileOutput)}");
        sb.AppendLine("# export_csv  Also export a CSV file. Default false.");
        sb.AppendLine($"export_csv = {B(c.ExportCsv)}");
        sb.AppendLine();

        sb.AppendLine("# -- Database ------------------------------------------------");
        sb.AppendLine("# known_devices_db  Remember devices per network in the database. Default true.");
        sb.AppendLine($"known_devices_db = {B(c.KnownDevicesDb)}");
        sb.AppendLine("# scan_history  Keep each completed run in the encrypted local archive. Default true.");
        sb.AppendLine($"scan_history = {B(c.ScanHistory)}");
        sb.AppendLine(@"# database_path  Path to the database file. Default .\scanner.db");
        sb.AppendLine($"database_path = {c.DatabasePath}");
        sb.AppendLine(@"# config_directory  Folder of this configuration file. Default .\ (next to the program).");
        sb.AppendLine($"config_directory = {c.ConfigDirectory}");
        sb.AppendLine("# network_interface_ip  IPv4 address of the network adapter to scan and ping from. Empty = automatic.");
        sb.AppendLine($"network_interface_ip = {c.NetworkInterfaceIp}");
        sb.AppendLine();

        sb.AppendLine("# -- Performance ---------------------------------------------");
        sb.AppendLine("# scan_threads  Parallel ping workers per scan, including the automatic sweep");
        sb.AppendLine("# after startup (0-1000, 0 = one per device). Default 100.");
        sb.AppendLine($"scan_threads = {c.ScanThreads}");
        sb.AppendLine("# ui_scale  Text size in percent (50-200). Default 100.");
        sb.AppendLine($"ui_scale = {c.UiScalePercent}");
        sb.AppendLine("# language  UI language: auto or a code (en, de, es, fr, zh, hi, ar, pt, ru, ja). Default auto.");
        sb.AppendLine($"language = {c.Language}");
        sb.AppendLine("# check_for_updates  Check GitHub for a newer release on startup. Default true.");
        sb.AppendLine($"check_for_updates = {B(c.CheckForUpdates)}");
        sb.AppendLine("# verbose_log  Also log informational and debug rows, not only errors and warnings. Default false.");
        sb.AppendLine($"verbose_log = {B(c.VerboseLog)}");
        sb.AppendLine();

        sb.AppendLine("# -- Latency colors -------------------------------------------");
        sb.AppendLine("# heat_ms_1..4  Latency thresholds in ms for the green/lime/yellow/peach/red");
        sb.AppendLine("# heatmap (1-10000, must stay ascending). Default 50/100/200/400.");
        sb.AppendLine($"heat_ms_1 = {c.HeatMs1.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"heat_ms_2 = {c.HeatMs2.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"heat_ms_3 = {c.HeatMs3.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"heat_ms_4 = {c.HeatMs4.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("# settings_page  Last-open category in the Settings window (Scan/Display/");
        sb.AppendLine("# Internet/Graphs/Data/App). Default Scan.");
        sb.AppendLine($"settings_page = {c.SettingsPage}");
        sb.AppendLine("# Manual export defaults: All/Filtered/Selected and Txt/Csv/Html/Json.");
        sb.AppendLine($"manual_export_scope = {c.ManualExportScope}");
        sb.AppendLine($"manual_export_format = {c.ManualExportFormat}");
        sb.AppendLine();
        sb.AppendLine("# tools_last_page  Last-open page in the Tools window (WakeOnLan/PortCheck/");
        sb.AppendLine("# DnsLookup/PingRoute/Calculator/Certificate). Default WakeOnLan.");
        sb.AppendLine($"tools_last_page = {c.ToolsLastPage}");
        sb.AppendLine();

        // Hex without '#' - the parser treats '#' as a comment marker.
        string H(string color) => color.TrimStart('#');
        sb.AppendLine("# -- Colors (progress bars, click the legend squares to change; RRGGBB) --");
        sb.AppendLine($"color_online = {H(c.ColorOnline)}");
        sb.AppendLine($"color_offline = {H(c.ColorOffline)}");
        sb.AppendLine($"color_success = {H(c.ColorSuccess)}");
        sb.AppendLine($"color_failed = {H(c.ColorFailed)}");
        sb.AppendLine($"color_skipped = {H(c.ColorSkipped)}");
        sb.AppendLine();
        sb.AppendLine("# accent_color  Custom accent (titles, focus border, selection; RRGGBB).");
        sb.AppendLine("# Empty = follow the active theme's own accent.");
        sb.AppendLine($"accent_color = {H(c.AccentColor)}");
        sb.AppendLine();
        sb.AppendLine("# date_number_format  auto (follow Windows) or a language code (en, de, ...).");
        sb.AppendLine($"date_number_format = {c.DateNumberFormat}");
        sb.AppendLine();
        sb.AppendLine("# compact_rows  Denser table rows (22px instead of 28px). Default false.");
        sb.AppendLine($"compact_rows = {B(c.CompactRows)}");
        sb.AppendLine();
        sb.AppendLine("# hidden_columns  Comma-separated, from: group,avg,min,max,last,loss,jitter,progress,mac,vendor,method.");
        sb.AppendLine($"hidden_columns = {string.Join(",", c.HiddenColumns)}");
        sb.AppendLine();
        sb.AppendLine("# -- Scan comfort ----------------------------------------------");
        sb.AppendLine("# repeat_scan_minutes  Auto-repeat the scan every N minutes: 0 (off), 5, 15, 30 or 60.");
        sb.AppendLine($"repeat_scan_minutes = {c.RepeatScanMinutes}");
        sb.AppendLine("# notify_scan_done  Tray balloon when a scan finishes. Default false.");
        sb.AppendLine($"notify_scan_done = {B(c.NotifyScanDone)}");
        sb.AppendLine("# notify_new_device  Tray balloon on a newly seen, not-yet-known device. Default false.");
        sb.AppendLine($"notify_new_device = {B(c.NotifyNewDevice)}");
        sb.AppendLine("# start_minimized  Start the app minimized to the tray. Default false.");
        sb.AppendLine($"start_minimized = {B(c.StartMinimized)}");
        sb.AppendLine();
        sb.AppendLine("# -- Data settings ---------------------------------------------");
        sb.AppendLine("# export_ports  Probe each online device's open ports for the TXT/CSV report. Default false.");
        sb.AppendLine($"export_ports = {B(c.ExportPorts)}");
        sb.AppendLine("# html_report  Also write an HTML report alongside TXT/CSV. Default false.");
        sb.AppendLine($"html_report = {B(c.HtmlReport)}");
        sb.AppendLine("# db_retention_days  Purge known-devices rows unseen this long: 30, 90, 365 or 0 (unlimited).");
        sb.AppendLine($"db_retention_days = {c.DbRetentionDays}");
        sb.AppendLine("# archive_retention_days  Delete scan archive runs older than this after each scan: 30, 90, 365 or 0 (unlimited).");
        sb.AppendLine($"archive_retention_days = {c.ArchiveRetentionDays}");
        sb.AppendLine("# archive_backup_retention_days  Retain encrypted archive backups for this many days. 0 keeps them all.");
        sb.AppendLine($"archive_backup_retention_days = {c.ArchiveBackupRetentionDays}");
        sb.AppendLine("# archive_compaction_days  Combine completed runs this old into daily aggregates. 0 disables compaction.");
        sb.AppendLine($"archive_compaction_days = {c.ArchiveCompactionDays}");
        sb.AppendLine("# show_skipped_legend  Show the \"skipped\" pings legend square. Default false.");
        sb.AppendLine($"show_skipped_legend = {B(c.ShowSkippedLegend)}");
        sb.AppendLine("# discovery_modes  Host discovery modes, comma-separated: arp, icmp, icmp-ts, icmp-mask, tcp, tcp-syn, tcp-ack, udp, list, skip. Default icmp.");
        sb.AppendLine($"discovery_modes = {c.DiscoveryModes}");
        sb.AppendLine("# tcp_ping_ports  Ports for the TCP discovery modes, comma-separated. Default 80,443,22,445.");
        sb.AppendLine($"tcp_ping_ports = {c.TcpPingPorts}");
        sb.AppendLine("# udp_ping_ports  Ports for the UDP discovery mode, comma-separated. Default 53,161,137.");
        sb.AppendLine($"udp_ping_ports = {c.UdpPingPorts}");
        sb.AppendLine("# os_fingerprint  Probe an open and a closed port of a device to refine its OS estimate. Needs the packet reader. Default false.");
        sb.AppendLine($"os_fingerprint = {B(c.OsFingerprint)}");
        sb.AppendLine("# scan_port_range  Ports of a scan as typed after -p: empty = the common ports, fast = the 100 most common, - = all, or a list such as 22,80,8000-9000.");
        sb.AppendLine($"scan_port_range = {c.ScanPortRange}");
        sb.AppendLine("# port_service_detection  Identify the service and version behind each open port, as -sV does. Default true.");
        sb.AppendLine($"port_service_detection = {B(c.PortServiceDetection)}");
        sb.AppendLine("# scan_presets, scan_presets_2, ...  Saved scan presets, one per line: name | flags (the same flags as the command line).");
        if (c.ScanPresets.Count == 0)
            sb.AppendLine("#scan_presets = Office | -T4 -p 22,80,443 -sV");
        for (int i = 0; i < c.ScanPresets.Count; i++)
            sb.AppendLine(i == 0 ? $"scan_presets = {c.ScanPresets[i]}" : $"scan_presets_{i + 1} = {c.ScanPresets[i]}");
        sb.AppendLine();
        sb.AppendLine("# -- Profiles --------------------------------------------------");
        sb.AppendLine("# active_profile  Name of the active profile, empty = the default profile above.");
        sb.AppendLine($"active_profile = {c.ActiveProfile}");
        sb.AppendLine("# profile_names  Additional profiles' names, comma-separated. Their own");
        sb.AppendLine("# subnets/pinned IPs/internet hosts are kept in the encrypted settings file next to the database.");
        sb.AppendLine($"profile_names = {string.Join(", ", c.ProfileNames)}");
        sb.AppendLine();
        sb.AppendLine("# -- Advanced network settings ---------------------------------");
        sb.AppendLine("# port_scan_connect_timeout_ms  TCP/UDP \"knock\" timeout per port (100-5000 ms). Default 500.");
        sb.AppendLine($"port_scan_connect_timeout_ms = {c.PortScanConnectTimeoutMs}");
        sb.AppendLine("# port_scan_banner_timeout_ms  Version-banner read timeout once a port answers (100-5000 ms). Default 600.");
        sb.AppendLine($"port_scan_banner_timeout_ms = {c.PortScanBannerTimeoutMs}");
        sb.AppendLine("# port_scan_parallel  Process-wide parallel port probes (1-500). Default 40.");
        sb.AppendLine($"port_scan_parallel = {c.PortScanParallel}");
        sb.AppendLine("# port_scan_timing  Timing template 0 (paranoid) to 5 (insane). Default 3.");
        sb.AppendLine($"port_scan_timing = {c.PortScanTiming}");
        sb.AppendLine("# port_scan_retries  Extra attempts for a port that did not answer at all (0-5). Default 0.");
        sb.AppendLine($"port_scan_retries = {c.PortScanRetries}");
        sb.AppendLine("# port_scan_delay_ms  Minimum gap between two port probes (0-60000 ms). Default 0.");
        sb.AppendLine($"port_scan_delay_ms = {c.PortScanDelayMs}");
        sb.AppendLine("# port_scan_max_rate  Cap on port probes per second (0 = unlimited). Default 0.");
        sb.AppendLine($"port_scan_max_rate = {c.PortScanMaxRate}");
        sb.AppendLine("# port_scan_intensity  Service detection effort on open ports (0 = listen only, 9 = every probe). Default 7.");
        sb.AppendLine($"port_scan_intensity = {c.PortScanIntensity}");
        sb.AppendLine("# custom_ports  Extra ports to probe, comma-separated \"port\" or \"port name\".");
        sb.AppendLine($"custom_ports = {string.Join(", ", c.CustomPorts)}");
        if (c.LegacySealedPending)
        {
            // Not yet safely in the sealed settings: keep the plain copies until a sealed write succeeds.
            if (c.SnmpCommunity.Length > 0 && c.SnmpCommunity != "public")
                sb.AppendLine($"snmp_community = {c.SnmpCommunity}");
            if (c.RecentWakeTargets.Count > 0)
                sb.AppendLine($"recent_wake_targets = {string.Join(", ", c.RecentWakeTargets)}");
        }

        var generated = AddSharedDescriptions(sb.ToString());
        if (File.Exists(path))
        {
            try { generated = Merge(path, generated); }
            catch (UnsafeConfigMergeException)
            {
                if (confirmOverwrite is null) throw;
                if (!confirmOverwrite()) return false;
            }
        }
        WriteAtomic(path, generated);
        return true;
    }

    private static string AddSharedDescriptions(string generated)
    {
        var newline = Environment.NewLine;
        var lines = generated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!TryAssignment(lines[i], out var key, out _, out _) ||
                !Loc.ConfigDescriptions.TryGetValue(key, out var description)) continue;
            var start = i;
            while (start > 0 && lines[start - 1].StartsWith('#') &&
                   !lines[start - 1].StartsWith("# -- ", StringComparison.Ordinal)) start--;
            var text = "# " + key + "  " + description.Description();
            if (description.Default.Length > 0) text += " Default: " + description.Default + ".";
            lines.RemoveRange(start, i - start);
            lines.Insert(start, text);
            i = start + 1;
        }
        return string.Join(newline, lines);
    }

    private static string Merge(string path, string generated)
    {
        string original;
        try
        {
            original = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            if (original.IndexOf('\0') >= 0) throw new DecoderFallbackException();
            original = original.TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException ex) { throw new UnsafeConfigMergeException("Unsupported configuration encoding.", ex); }

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var legacyHeader = lines.FindIndex(x => x == "#  This file is overwritten when settings are saved in the app.");
        if (legacyHeader >= 0)
            lines[legacyHeader] = Loc.German
                ? "#  Von Hand eingefügte Kommentare bleiben erhalten."
                : "#  Comments you add by hand are preserved.";
        var template = generated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var line in template)
        {
            if (line.StartsWith("# -- ", StringComparison.Ordinal)) section = line;
            if (TryAssignment(line, out var key, out var value, out _))
            {
                values[key] = value;
                sections[key] = section;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith(';')) continue;
            if (!TryAssignment(line, out var key, out _, out var comment))
                throw new UnsafeConfigMergeException("Unparsable configuration line.");
            if (!seen.Add(key)) throw new UnsafeConfigMergeException("Duplicate configuration key.");
            if (values.TryGetValue(key, out var replacement))
            {
                var prefix = line[..(line.IndexOf('=') + 1)];
                lines[i] = prefix + " " + replacement + comment;
            }
            else if (RetiredKeys.Contains(key))
            {
                // Moved to the sealed settings file: drop the plain copy and its comment block.
                int start = i;
                while (start > 0 && lines[start - 1].StartsWith('#') &&
                       !lines[start - 1].StartsWith("# -- ", StringComparison.Ordinal)) start--;
                lines.RemoveRange(start, i - start + 1);
                i = start - 1;
            }
            else if (key.Equals("pinned_ips", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("subnet", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("scan_presets", StringComparison.OrdinalIgnoreCase) ||
                     Regex.IsMatch(key, @"^(subnet|scan_presets)_\d+$", RegexOptions.IgnoreCase))
            {
                // A removed subnet or preset must not reappear on the next load.
                lines.RemoveAt(i--);
            }
        }

        foreach (var line in template)
        {
            if (!TryAssignment(line, out var key, out _, out _) || seen.Contains(key)) continue;
            var heading = sections[key];
            var headingAt = lines.FindIndex(x => x.Equals(heading, StringComparison.Ordinal));
            var insertAt = headingAt < 0 ? lines.Count : lines.FindIndex(headingAt + 1,
                x => x.StartsWith("# -- ", StringComparison.Ordinal));
            if (insertAt < 0) insertAt = lines.Count;
            if (headingAt < 0)
                while (insertAt > 0 && (lines[insertAt - 1].Length == 0 ||
                    lines[insertAt - 1].TrimStart().StartsWith('#') ||
                    lines[insertAt - 1].TrimStart().StartsWith(';'))) insertAt--;
            lines.Insert(insertAt, line);
            seen.Add(key);
        }
        return string.Join(newline, lines) + newline;
    }

    private static bool TryAssignment(string line, out string key, out string value, out string comment)
    {
        key = value = comment = "";
        if (line.Length == 0 || line.TrimStart().StartsWith('#') ||
            line.TrimStart().StartsWith(';')) return false;
        var match = Regex.Match(line, @"^\s*([^\s=]+)\s*=\s*(.*)$");
        if (!match.Success) return false;
        key = match.Groups[1].Value;
        var raw = match.Groups[2].Value;
        var plain = StripInlineComment(raw);
        value = plain.Trim();
        comment = raw[plain.TrimEnd().Length..];
        return true;
    }

    // Atomic write with a ".bak" of the previous generation, folder/file protection included
    // (the config stays unencrypted by design, so the ACL is its only guard).
    private static void WriteAtomic(string path, string content) => AtomicFile.WriteText(path, content);

    private static Dictionary<string, string> ParseFlat(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            var idx = line.IndexOf('=');
            if (idx < 0) continue;

            var key   = line[..idx].Trim().ToLowerInvariant();
            var value = StripInlineComment(line[(idx + 1)..]).Trim();

            if (key.Length > 0) d[key] = value;
        }
        return d;
    }

    /// <summary>Cuts a value at the first '#'/';' that starts a trailing
    /// comment - but only when that marker is preceded by whitespace (or is
    /// the very first character). A bare "#"/";" anywhere inside the value
    /// itself, like a Windows path such as "C:\Reports#2\Scans", is a
    /// perfectly legal filename character and must survive; only
    /// an actual "value #comment"-style trailing comment gets cut.</summary>
    private static string StripInlineComment(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '#' or ';' && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                return s[..i];
        }
        return s;
    }

    private static int ParseInt(string s, int lo, int hi, int fallback)
        => int.TryParse(s.Trim(), out var v) ? Clamp(v, lo, hi) : fallback;

    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, ScanConfig.Bounds.HeatMsMin, ScanConfig.Bounds.HeatMsMax) : fallback;

    private static string ParseColor(string s, string fallback)
    {
        var hex = s.Trim().TrimStart('#');
        return Core.Palette.IsHexColor(hex) ? "#" + hex.ToUpperInvariant() : fallback;
    }

    private static bool ParseBool(string s)
        => s.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on" or "enabled";

    private static List<string> ParseIpList(string s)
    {
        var result = new List<string>();
        foreach (var part in s.Replace(';', ',').Split(','))
        {
            var ip = part.Trim();
            if (ip.Length > 0 && !result.Contains(ip)) result.Add(ip);
        }
        return result;
    }
}
