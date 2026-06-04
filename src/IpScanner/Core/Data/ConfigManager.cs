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
                case "ping_count":                  cfg.PingCount = ParseInt(value, 1, 10_000_000, 10); break;
                case "ping_interval_ms":            cfg.PingIntervalMs = ParseInt(value, 0, 10_000, 100); break;
                case "offline_after_failed_pings":  cfg.OfflineAfterFailedPings = ParseInt(value, 1, 100, 5); break;
                case "init_ping_count":             cfg.InitPingCount = ParseInt(value, 1, 100, 1); break;
                case "enable_internet_ping":        cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_hosts":              cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db":            cfg.KnownDevicesDb = ParseBool(value); break;
                case "output_directory":            cfg.OutputDirectory = value; break;
                case "file_output":                 cfg.FileOutput = ParseBool(value); break;
                case "export_csv":                  cfg.ExportCsv = ParseBool(value); break;
                case "scan_threads":                cfg.ScanThreads = ParseInt(value, 0, 1000, 50); break;
                case "pinned_ips":                  cfg.PinnedIps = ParseIpList(value); break;
                default:
                    var m = Regex.Match(key, @"^subnet(?:_(\d+))?$");
                    if (m.Success && value.Length > 0)
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
        sb.AppendLine();

        sb.AppendLine("# -- Internet-Latenz -----------------------------------------");
        sb.AppendLine("# enable_internet_ping  Offentliche Hosts mitpingen. Standard true.");
        sb.AppendLine($"enable_internet_ping = {B(c.EnableInternetPing)}");
        sb.AppendLine("# internet_hosts  Zu messende IPs, Komma-getrennt.");
        sb.AppendLine($"internet_hosts = {string.Join(", ", c.InternetHosts)}");
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
        sb.AppendLine("# known_devices_db  Gerate je Netz in scanner.db merken. Standard true.");
        sb.AppendLine($"known_devices_db = {B(c.KnownDevicesDb)}");
        sb.AppendLine();

        sb.AppendLine("# -- Performance ---------------------------------------------");
        sb.AppendLine("# scan_threads  Parallele Ping-Worker je Scan (0-1000, 0=einer je Geraet). Standard 50.");
        sb.AppendLine($"scan_threads = {c.ScanThreads}");

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
