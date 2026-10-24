using System.Globalization;
using IpScanner.Core.Export;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Cli;

/// <summary>Turns the argument list into <see cref="CliOptions"/>. Driven by <see cref="CliFlags.All"/>;
/// no flag is known to the parser by any other route.</summary>
public static class CliParser
{
    private static readonly Dictionary<string, CliFlag> ByName = BuildIndex();

    private static Dictionary<string, CliFlag> BuildIndex()
    {
        var map = new Dictionary<string, CliFlag>(StringComparer.Ordinal);
        foreach (var f in CliFlags.All)
            foreach (var n in f.Names) map.Add(n, f);
        return map;
    }

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions { CommandLine = string.Join(' ', args.Select(Quote)) };
        bool flagsEnded = false;
        string? tcpFlag = null;
        for (int i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (flagsEnded || token.Length < 2 || token[0] != '-')
            {
                o.Targets.Add(token);
                continue;
            }
            if (token == "--") { flagsEnded = true; continue; }

            if (!Resolve(token, out var flag, out var name, out var inline))
                return Fail(o, Unknown(token));

            string? value = null;
            if (flag.Arg != CliArg.None)
            {
                if (inline is not null) value = inline;
                else if (i + 1 < args.Count)
                {
                    value = args[++i];
                    // "-oN -sV host" would otherwise swallow -sV as a file name.
                    if (value.Length > 1 && value[0] == '-')
                        return Fail(o, $"Option '{name}' needs a value ({flag.ArgName}), got the option '{value}'. Write {name}={value} if the value really starts with a dash.");
                }
                else return Fail(o, $"Option '{name}' needs a value ({flag.ArgName}).");
            }
            else if (inline is not null)
                return Fail(o, $"Option '{name}' takes no value.");

            var error = Apply(o, flag, name, value, ref tcpFlag);
            if (error is not null) return Fail(o, error);
        }
        return new CliParseResult(o, o.Help || o.Version ? null : Validate(o));
    }

    private static CliParseResult Fail(CliOptions o, string error) => new(o, error);

    private static bool Resolve(string token, out CliFlag flag, out string name, out string? inline)
    {
        inline = null;
        if (ByName.TryGetValue(token, out flag!)) { name = token; return true; }
        int eq = token.IndexOf('=');
        if (eq > 0 && ByName.TryGetValue(token[..eq], out flag!) && flag.Arg != CliArg.None)
        {
            name = token[..eq];
            inline = token[(eq + 1)..];
            return true;
        }
        // -p22,80 and -T4: the value sits right behind a two character flag.
        if (!token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2
            && ByName.TryGetValue(token[..2], out flag!) && flag.Arg == CliArg.ValueOrAttached)
        {
            name = token[..2];
            inline = token[2..];
            return true;
        }
        name = token;
        flag = null!;
        return false;
    }

    private static string? Apply(CliOptions o, CliFlag flag, string name, string? value, ref string? tcpFlag)
    {
        switch (flag.Id)
        {
            case "iL": o.IncludeFile = value; break;
            case "exclude": o.Exclude = string.IsNullOrEmpty(o.Exclude) ? value : o.Exclude + "," + value; break;
            case "exclude-file": o.ExcludeFile = value; break;
            case "6": o.Ipv6 = true; break;

            case "sn":
                if (o.Mode == CliMode.List) return "-sn and -sL cannot be combined.";
                o.Mode = CliMode.Ping;
                break;
            case "Pn": o.SkipDiscovery = true; break;
            case "sL":
                if (o.Mode == CliMode.Ping) return "-sn and -sL cannot be combined.";
                o.Mode = CliMode.List;
                break;
            case "sU": o.Udp = true; break;
            case "sT": return SetTcp(o, ScanType.Connect, name, ref tcpFlag);
            case "sS": return SetTcp(o, ScanType.Syn, name, ref tcpFlag);
            case "sF": return SetTcp(o, ScanType.Fin, name, ref tcpFlag);
            case "sX": return SetTcp(o, ScanType.Xmas, name, ref tcpFlag);
            case "sN": return SetTcp(o, ScanType.Null, name, ref tcpFlag);
            case "sA": return SetTcp(o, ScanType.Ack, name, ref tcpFlag);
            case "sW": return SetTcp(o, ScanType.Window, name, ref tcpFlag);
            case "sM": return SetTcp(o, ScanType.Maimon, name, ref tcpFlag);

            case "p":
                if (!PortSpec.TryParse(value, out _)) return $"Invalid port list '{value}'. Use for example 22,80,8000-9000 or T:80,U:53.";
                o.Ports = value;
                break;
            case "F": o.Fast = true; break;
            case "top-ports":
                if (!TryInt(value, 1, 65535, out var top)) return $"Option '{name}' needs a number from 1 to 65535, got '{value}'.";
                o.TopPorts = top;
                break;

            case "sV": o.ServiceDetection = true; break;
            case "version-intensity":
                if (!TryInt(value, 0, 9, out var vi)) return $"Option '{name}' needs a number from 0 to 9, got '{value}'.";
                o.VersionIntensity = vi;
                o.ServiceDetection = true;
                break;
            case "O": o.OsDetection = true; break;
            case "checks":
                if (CliChecks.Parse(value) is null) return $"Invalid check list '{value}'. Use safe, discovery, intrusive or a comma separated mix.";
                o.Checks = value;
                break;

            case "T":
                if (!TryInt(value, 0, 5, out var t)) return $"Option '{name}' needs a number from 0 to 5, got '{value}'.";
                o.Timing = t;
                break;
            case "min-rate":
                if (!TryInt(value, 1, int.MaxValue, out var mn)) return $"Option '{name}' needs a positive number, got '{value}'.";
                o.MinRate = mn;
                break;
            case "max-rate":
                if (!TryInt(value, 1, int.MaxValue, out var mx)) return $"Option '{name}' needs a positive number, got '{value}'.";
                o.MaxRate = mx;
                break;
            case "max-retries":
                if (!TryInt(value, 0, 100, out var mr)) return $"Option '{name}' needs a number from 0 to 100, got '{value}'.";
                o.MaxRetries = mr;
                break;
            case "host-timeout":
                if (!TryDuration(value, out var ht)) return $"Option '{name}' needs a time like 500ms, 30s, 5m or 1h, got '{value}'.";
                o.HostTimeoutMs = ht;
                break;

            case "oN": return SetOutput(o, ReportFormat.Text, name, value);
            case "oX": return SetOutput(o, ReportFormat.Xml, name, value);
            case "oJ": return SetOutput(o, ReportFormat.Json, name, value);
            case "oG": return SetOutput(o, ReportFormat.Grepable, name, value);
            case "reason": o.Reason = true; break;
            case "open": o.OpenOnly = true; break;

            case "allow-full-scan": o.AllowFullScan = true; break;
            case "allow-large": o.AllowLarge = true; break;
            case "allow-intrusive": o.AllowIntrusive = true; break;
            case "allow-aggressive": o.AllowAggressive = true; break;
            case "i-know-what-i-am-doing": o.AcknowledgeRisk = true; break;

            case "interface": o.Interface = value; break;
            case "v": o.Verbosity += name.Length - 1; break;
            case "d": o.Debug = true; break;
            case "help": o.Help = true; break;
            case "version": o.Version = true; break;
        }
        return null;
    }

    private static string? SetTcp(CliOptions o, ScanType type, string name, ref string? seen)
    {
        if (seen is not null && seen != name) return $"Only one TCP scan type at a time: '{seen}' and '{name}' were both given.";
        seen = name;
        o.TcpType = type;
        return null;
    }

    private static string? SetOutput(CliOptions o, ReportFormat format, string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return $"Option '{name}' needs a file name or - for stdout.";
        if (o.Outputs.ContainsKey(format)) return $"Option '{name}' was given twice.";
        if (path == "-" && o.Outputs.ContainsValue("-")) return "Only one output can go to stdout.";
        if (path != "-" && SameFile(o, path)) return $"Option '{name}' writes to a file that another output already uses.";
        o.Outputs[format] = path;
        return null;
    }

    private static bool SameFile(CliOptions o, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        foreach (var other in o.Outputs.Values)
        {
            if (other == "-") continue;
            try { if (string.Equals(Path.GetFullPath(other), full, StringComparison.OrdinalIgnoreCase)) return true; }
            catch (Exception) { /* an unusable path fails later */ }
        }
        return false;
    }

    /// <summary>Cross-flag rules that no single flag can check.</summary>
    private static string? Validate(CliOptions o)
    {
        if (o.Ports is not null && (o.Fast || o.TopPorts is not null)) return "Use only one of -p, -F and --top-ports.";
        if (o.Fast && o.TopPorts is not null) return "Use only one of -F and --top-ports.";
        if (o.MinRate is { } min && o.MaxRate is { } max && min > max) return "--min-rate must not be above --max-rate.";
        if (o.Mode == CliMode.List && (o.TcpType is not null || o.Udp || o.Ports is not null || o.Fast || o.TopPorts is not null
                || o.ServiceDetection || o.OsDetection || o.Checks is not null))
            return "-sL sends nothing to the targets and cannot combine with a port scan, -sV, -O or --checks.";
        if (o.Mode == CliMode.Ping && o.SkipDiscovery) return "-sn finds hosts only and cannot combine with -Pn (which skips the host search).";
        if (o.Mode == CliMode.Ping && (o.TcpType is not null || o.Udp || o.Ports is not null || o.Fast || o.TopPorts is not null
                || o.ServiceDetection || o.OsDetection || o.Checks is not null))
            return "-sn finds hosts only and cannot combine with port scan options, -sV, -O or --checks.";
        return null;
    }

    private static bool TryInt(string? text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>"500ms", "30s", "5m", "1h"; a bare number is seconds.</summary>
    internal static bool TryDuration(string? text, out int ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().ToLowerInvariant();
        long factor = 1000;
        string number = text;
        foreach (var (suffix, f) in new[] { ("ms", 1L), ("s", 1000L), ("m", 60_000L), ("h", 3_600_000L) })
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal)) continue;
            factor = f;
            number = text[..^suffix.Length];
            break;
        }
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
        if (n > int.MaxValue / factor) return false;
        long total = n * factor;
        ms = (int)total;
        return true;
    }

    internal static string Quote(string arg) => arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    private static string Unknown(string token)
    {
        var shown = token.Contains('=') ? token[..token.IndexOf('=')] : token;
        var best = Suggest(shown);
        return best is null ? $"Unknown option '{shown}'. Try --help." : $"Unknown option '{shown}'. Did you mean '{best}'?";
    }

    internal static string? Suggest(string token)
    {
        var names = ByName.Keys.Where(n => n.StartsWith("--", StringComparison.Ordinal) == token.StartsWith("--", StringComparison.Ordinal)).ToList();
        var ci = names.FirstOrDefault(n => string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
        if (ci is not null) return ci;
        if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 3)
        {
            var prefix = names.Where(n => n.StartsWith(token, StringComparison.Ordinal)).ToList();
            if (prefix.Count == 1) return prefix[0];
        }
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var n in names)
        {
            int d = Distance(token, n);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        // A short flag is too close to everything else; only long names get a distance suggestion.
        return best is not null && token.StartsWith("--", StringComparison.Ordinal) && bestDist <= 2 ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
