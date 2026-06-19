using System.IO;
using System.Text.RegularExpressions;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

/// <summary>Reads ip_scanner.conf (flat key=value, # comments).</summary>
public static class ConfigManager
{
    // All scalar keys that must be present in a fully-written conf file.
    // If any are absent the file is from an older version and gets rewritten.
    private static readonly HashSet<string> RequiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping_count", "ping_interval_ms", "offline_after_failed_pings", "init_ping_count",
        "startup_ping_count", "offline_recheck_seconds", "enable_internet_ping",
        "internet_timeout_ms", "internet_hosts", "known_devices_db", "database_path",
        "config_directory", "graphs_enabled", "network_graphs_enabled", "graph_max_seconds", "output_directory",
        "file_output", "export_csv", "scan_threads", "ui_scale", "language", "check_for_updates",
        "color_online", "color_offline", "color_success", "color_failed", "color_skipped",
    };

    /// <summary>If the conf file is missing keys from the current version, rewrite it
    /// with defaults filled in while keeping all existing values. No-op if up to date.</summary>
    public static void MigrateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        var flat = ParseFlat(path);
        if (RequiredKeys.All(flat.ContainsKey)) return;
        Save(path, Load(path));
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    public static ScanConfig Load(string path)
    {
        var cfg = new ScanConfig();
        if (!File.Exists(path)) return cfg;

        var flat = ParseFlat(path);
        var subnets = new SortedDictionary<int, string>();

        foreach (var (key, value) in flat)
        {
            switch (key)
            {
                case "ping_count":                  cfg.PingCount = ParseInt(value, -1, 10_000_000, 10); break;
                case "ping_interval_ms":            cfg.PingIntervalMs = ParseInt(value, 0, 10_000, 100); break;
                case "offline_after_failed_pings":  cfg.OfflineAfterFailedPings = ParseInt(value, 1, 100, 5); break;
                case "init_ping_count":             cfg.InitPingCount = ParseInt(value, 1, 100, 1); break;
                case "startup_ping_count":          cfg.StartupPingCount = ParseInt(value, 0, 10_000, 10); break;
                case "offline_recheck_seconds":     cfg.OfflineRecheckSeconds = ParseInt(value, 0, 3600, 5); break;
                case "enable_internet_ping":        cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_timeout_ms":         cfg.InternetTimeoutMs = ParseInt(value, 100, 10_000, 1000); break;
                case "internet_hosts":              cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db":            cfg.KnownDevicesDb = ParseBool(value); break;
                case "database_path":               if (value.Length > 0) cfg.DatabasePath = value; break;
                case "config_directory":            cfg.ConfigDirectory = value; break;
                case "graphs_enabled":              cfg.GraphsEnabled = ParseBool(value); break;
                case "network_graphs_enabled":      cfg.NetworkGraphsEnabled = ParseBool(value); break;
                case "graph_max_seconds":           cfg.GraphMaxSeconds = ParseInt(value, 10, 300, 300); break;
                case "check_for_updates":           cfg.CheckForUpdates = ParseBool(value); break;
                case "output_directory":            cfg.OutputDirectory = value; break;
                case "file_output":                 cfg.FileOutput = ParseBool(value); break;
                case "export_csv":                  cfg.ExportCsv = ParseBool(value); break;
                case "scan_threads":                cfg.ScanThreads = ParseInt(value, 0, 1000, 100); break;
                case "ui_scale":                    cfg.UiScalePercent = ParseInt(value, 50, 200, 100); break;
                case "language":
                    var lang = value.Trim().ToLowerInvariant();
                    cfg.Language = lang is "de" or "en" ? lang : "auto";
                    break;
                case "pinned_ips":                  cfg.PinnedIps = ParseIpList(value); break;
                case "color_online":                cfg.ColorOnline = ParseColor(value, cfg.ColorOnline); break;
                case "color_offline":               cfg.ColorOffline = ParseColor(value, cfg.ColorOffline); break;
                case "color_success":               cfg.ColorSuccess = ParseColor(value, cfg.ColorSuccess); break;
                case "color_failed":                cfg.ColorFailed = ParseColor(value, cfg.ColorFailed); break;
                case "color_skipped":               cfg.ColorSkipped = ParseColor(value, cfg.ColorSkipped); break;
                default:
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
        return cfg;
    }

    public static void Save(string path, ScanConfig c)
    {
        string B(bool v) => v ? "true" : "false";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("#  IP-Scanner configuration");
        sb.AppendLine("#  Lines starting with # are comments. Invalid values fall back to defaults.");
        sb.AppendLine("#  This file is overwritten when settings are saved in the app.");
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
        sb.AppendLine("# graph_max_seconds  Visible time span of the graphs (10-300 s). Default 300.");
        sb.AppendLine($"graph_max_seconds = {c.GraphMaxSeconds}");
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
        sb.AppendLine(@"# database_path  Path to the database file. Default .\scanner.db");
        sb.AppendLine($"database_path = {c.DatabasePath}");
        sb.AppendLine(@"# config_directory  Folder of this configuration file. Default .\ (next to the program).");
        sb.AppendLine($"config_directory = {c.ConfigDirectory}");
        sb.AppendLine();

        sb.AppendLine("# -- Performance ---------------------------------------------");
        sb.AppendLine("# scan_threads  Parallel ping workers per scan (0-1000, 0 = one per device). Default 100.");
        sb.AppendLine($"scan_threads = {c.ScanThreads}");
        sb.AppendLine("# ui_scale  Text size in percent (50-200). Default 100.");
        sb.AppendLine($"ui_scale = {c.UiScalePercent}");
        sb.AppendLine("# language  Language: auto, de, en. Default auto.");
        sb.AppendLine($"language = {c.Language}");
        sb.AppendLine("# check_for_updates  Check GitHub for a newer release on startup. Default true.");
        sb.AppendLine($"check_for_updates = {B(c.CheckForUpdates)}");
        sb.AppendLine();

        // Hex without '#' — the parser treats '#' as a comment marker.
        string H(string color) => color.TrimStart('#');
        sb.AppendLine("# -- Colors (progress bars, click the legend squares to change; RRGGBB) --");
        sb.AppendLine($"color_online = {H(c.ColorOnline)}");
        sb.AppendLine($"color_offline = {H(c.ColorOffline)}");
        sb.AppendLine($"color_success = {H(c.ColorSuccess)}");
        sb.AppendLine($"color_failed = {H(c.ColorFailed)}");
        sb.AppendLine($"color_skipped = {H(c.ColorSkipped)}");

        File.WriteAllText(path, sb.ToString());
    }

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
            var value = line[(idx + 1)..].Split('#')[0].Split(';')[0].Trim();

            if (key.Length > 0) d[key] = value;
        }
        return d;
    }

    private static int ParseInt(string s, int lo, int hi, int fallback)
        => int.TryParse(s.Trim(), out var v) ? Clamp(v, lo, hi) : fallback;

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
