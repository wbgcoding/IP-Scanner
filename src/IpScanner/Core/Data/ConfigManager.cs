using System.IO;
using System.Text.RegularExpressions;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

/// <summary>Reads ip_scanner.conf (flat key=value, # comments).</summary>
public static class ConfigManager
{
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
                case "startup_ping_count":          cfg.StartupPingCount = ParseInt(value, 0, 10_000, 5); break;
                case "offline_recheck_seconds":     cfg.OfflineRecheckSeconds = ParseInt(value, 0, 3600, 2); break;
                case "enable_internet_ping":        cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_timeout_ms":         cfg.InternetTimeoutMs = ParseInt(value, 100, 10_000, 1000); break;
                case "internet_hosts":              cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db":            cfg.KnownDevicesDb = ParseBool(value); break;
                case "database_path":               if (value.Length > 0) cfg.DatabasePath = value; break;
                case "config_directory":            cfg.ConfigDirectory = value; break;
                case "graphs_enabled":              cfg.GraphsEnabled = ParseBool(value); break;
                case "graph_max_seconds":           cfg.GraphMaxSeconds = ParseInt(value, 10, 300, 300); break;
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
                    if (m.Success && value.Length > 0 &&
                        (!m.Groups[1].Success || int.TryParse(m.Groups[1].Value, out _)))
                        subnets[m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0] = value;
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
        sb.AppendLine("#  IP-Scanner Konfiguration");
        sb.AppendLine("#  Zeilen mit # sind Kommentare. Ungultige Werte -> Standard.");
        sb.AppendLine("#  Diese Datei wird beim Speichern in der App ueberschrieben.");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();

        sb.AppendLine("# -- Netzwerk ------------------------------------------------");
        sb.AppendLine("# subnet, subnet_2, ...  Zu scannende Netze (CIDR). Leer = automatisch erkennen.");
        if (c.Subnets.Count == 0)
            sb.AppendLine("#subnet = 192.168.1.0/24");
        for (int i = 0; i < c.Subnets.Count; i++)
            sb.AppendLine(i == 0 ? $"subnet = {c.Subnets[i]}" : $"subnet_{i + 1} = {c.Subnets[i]}");
        sb.AppendLine("# pinned_ips  Immer pingen + oben anzeigen. Komma-getrennt.");
        sb.AppendLine(c.PinnedIps.Count > 0 ? $"pinned_ips = {string.Join(", ", c.PinnedIps)}" : "#pinned_ips = 192.168.1.1, 192.168.1.10");
        sb.AppendLine();

        sb.AppendLine("# -- Ping-Verhalten ------------------------------------------");
        sb.AppendLine("# ping_count  Pings je Gerat (1-10000000). Standard 10.");
        sb.AppendLine($"ping_count = {c.PingCount}");
        sb.AppendLine("# ping_interval_ms  Pause zwischen Pings desselben Hosts (0-10000 ms). Standard 100.");
        sb.AppendLine($"ping_interval_ms = {c.PingIntervalMs}");
        sb.AppendLine("# offline_after_failed_pings  Nach N Fehlpings offline (1-100). Standard 5.");
        sb.AppendLine($"offline_after_failed_pings = {c.OfflineAfterFailedPings}");
        sb.AppendLine("# init_ping_count  Pings in der Suchphase je IP (1-100). Standard 1.");
        sb.AppendLine($"init_ping_count = {c.InitPingCount}");
        sb.AppendLine("# startup_ping_count  Pings je Geraet beim Auto-Scan nach dem Start (0-10000). Standard 5.");
        sb.AppendLine($"startup_ping_count = {c.StartupPingCount}");
        sb.AppendLine("# offline_recheck_seconds  Offline-IPs alle N Sekunden erneut pruefen (0 = aus). Standard 2.");
        sb.AppendLine($"offline_recheck_seconds = {c.OfflineRecheckSeconds}");
        sb.AppendLine();

        sb.AppendLine("# -- Internet-Latenz -----------------------------------------");
        sb.AppendLine("# enable_internet_ping  Offentliche Hosts mitpingen. Standard true.");
        sb.AppendLine($"enable_internet_ping = {B(c.EnableInternetPing)}");
        sb.AppendLine("# internet_timeout_ms  Ping-Timeout fuer Internet-Hosts (100-10000 ms). Standard 1000.");
        sb.AppendLine($"internet_timeout_ms = {c.InternetTimeoutMs}");
        sb.AppendLine("# internet_hosts  Zu messende IPs, Komma-getrennt.");
        sb.AppendLine($"internet_hosts = {string.Join(", ", c.InternetHosts)}");
        sb.AppendLine();

        sb.AppendLine("# -- Graphen -------------------------------------------------");
        sb.AppendLine("# graphs_enabled  Latenz-Graphen in der Seitenleiste anzeigen. Standard true.");
        sb.AppendLine($"graphs_enabled = {B(c.GraphsEnabled)}");
        sb.AppendLine("# graph_max_seconds  Sichtbare Zeitspanne der Graphen (10-300 s). Standard 300.");
        sb.AppendLine($"graph_max_seconds = {c.GraphMaxSeconds}");
        sb.AppendLine();

        sb.AppendLine("# -- Ausgabe -------------------------------------------------");
        sb.AppendLine("# output_directory  Speicherort der Berichte. Standard ./Scans");
        sb.AppendLine($"output_directory = {c.OutputDirectory}");
        sb.AppendLine("# file_output  TXT-Bericht nach jedem Scan schreiben. Standard false.");
        sb.AppendLine($"file_output = {B(c.FileOutput)}");
        sb.AppendLine("# export_csv  Zusatzlich CSV exportieren. Standard false.");
        sb.AppendLine($"export_csv = {B(c.ExportCsv)}");
        sb.AppendLine();

        sb.AppendLine("# -- Datenbank -----------------------------------------------");
        sb.AppendLine("# known_devices_db  Gerate je Netz in der Datenbank merken. Standard true.");
        sb.AppendLine($"known_devices_db = {B(c.KnownDevicesDb)}");
        sb.AppendLine("# database_path  Pfad der Datenbankdatei. Standard ./Scans/scanner.db");
        sb.AppendLine($"database_path = {c.DatabasePath}");
        sb.AppendLine("# config_directory  Ordner dieser Konfigurationsdatei (leer = neben der Datenbank).");
        sb.AppendLine($"config_directory = {c.ConfigDirectory}");
        sb.AppendLine();

        sb.AppendLine("# -- Performance ---------------------------------------------");
        sb.AppendLine("# scan_threads  Parallele Ping-Worker je Scan (0-1000, 0=einer je Geraet). Standard 100.");
        sb.AppendLine($"scan_threads = {c.ScanThreads}");
        sb.AppendLine("# ui_scale  Textgroesse in Prozent (50-200). Standard 100.");
        sb.AppendLine($"ui_scale = {c.UiScalePercent}");
        sb.AppendLine("# language  Sprache: auto, de, en. Standard auto.");
        sb.AppendLine($"language = {c.Language}");
        sb.AppendLine();

        // Hex without '#' — the parser treats '#' as a comment marker.
        string H(string color) => color.TrimStart('#');
        sb.AppendLine("# -- Farben (Fortschrittsbalken, per Klick auf die Legende aenderbar; RRGGBB) --");
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
        return Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$") ? "#" + hex.ToUpperInvariant() : fallback;
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
