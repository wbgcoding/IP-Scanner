using System.IO;
using System.Text.RegularExpressions;
using IpScanner.Core.Models;

namespace IpScanner.Core.Data;

/// <summary>Reads ip_scanner.conf (flat key=value, # comments).</summary>
public static class ConfigManager
{
    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

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
                case "high_pressure_mode":          cfg.HighPressureMode = ParseBool(value); break;
                case "enable_internet_ping":        cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_hosts":              cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db":            cfg.KnownDevicesDb = ParseBool(value); break;
                case "output_directory":            cfg.OutputDirectory = value; break;
                case "file_output":                 cfg.FileOutput = ParseBool(value); break;
                case "export_csv":                  cfg.ExportCsv = ParseBool(value); break;
                case "ping_threads":                cfg.PingThreads = ParseInt(value, 1, 1000, 100); break;
                case "init_ping_threads":           cfg.InitPingThreads = ParseInt(value, 0, 1000, 254); break;
                case "refresh_rate":                cfg.RefreshRate = ParseDouble(value, 0.1, 60.0, 1.0); break;
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
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# IP-Scanner configuration");
        sb.AppendLine($"ping_count = {c.PingCount}");
        sb.AppendLine($"ping_interval_ms = {c.PingIntervalMs}");
        sb.AppendLine($"offline_after_failed_pings = {c.OfflineAfterFailedPings}");
        sb.AppendLine($"init_ping_count = {c.InitPingCount}");
        sb.AppendLine($"high_pressure_mode = {(c.HighPressureMode ? "true" : "false")}");
        sb.AppendLine($"enable_internet_ping = {(c.EnableInternetPing ? "true" : "false")}");
        sb.AppendLine($"internet_hosts = {string.Join(", ", c.InternetHosts)}");
        sb.AppendLine($"known_devices_db = {(c.KnownDevicesDb ? "true" : "false")}");
        sb.AppendLine($"output_directory = {c.OutputDirectory}");
        sb.AppendLine($"file_output = {(c.FileOutput ? "true" : "false")}");
        sb.AppendLine($"export_csv = {(c.ExportCsv ? "true" : "false")}");
        sb.AppendLine($"ping_threads = {c.PingThreads}");
        sb.AppendLine($"init_ping_threads = {c.InitPingThreads}");
        sb.AppendLine($"refresh_rate = {c.RefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        for (int i = 0; i < c.Subnets.Count; i++)
            sb.AppendLine(i == 0 ? $"subnet = {c.Subnets[i]}" : $"subnet_{i + 1} = {c.Subnets[i]}");
        if (c.PinnedIps.Count > 0)
            sb.AppendLine($"pinned_ips = {string.Join(", ", c.PinnedIps)}");
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

    private static double ParseDouble(string s, double lo, double hi, double fallback)
        => double.TryParse(s.Trim().Replace(',', '.'),
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out var v)
           ? Clamp(v, lo, hi) : fallback;

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
