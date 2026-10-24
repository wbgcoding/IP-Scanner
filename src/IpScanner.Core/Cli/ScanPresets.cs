using IpScanner.Core.Localization;
using IpScanner.Core.Models;

namespace IpScanner.Core.Cli;

/// <summary>A named set of scan flags. Built-in presets are fixed; user presets live in the settings.</summary>
public sealed record ScanPreset(string Name, string Flags, bool BuiltIn);

/// <summary>The scan presets: the built-in ones, the saved ones and the flag text they are written in. A preset is
/// a flag string, read by the same parser as the command line.</summary>
public static class ScanPresets
{
    public const int MaxUserPresets = 50;
    public const int MaxNameLength = 40;
    private const char Separator = '|';

    public static IReadOnlyList<ScanPreset> BuiltIn() =>
    [
        new(Loc.PresetQuick, "-T4 -F", true),
        new(Loc.PresetThorough, "-T3 -p- -sV -O", true),
        new(Loc.PresetPortsOnly, "-Pn -p-", true),
        new(Loc.PresetQuiet, "-T1 -sn", true),
    ];

    /// <summary>Built-in presets followed by the saved ones; saved entries that no longer parse are left out.</summary>
    public static IReadOnlyList<ScanPreset> All(ScanConfig config)
    {
        var list = BuiltIn().ToList();
        foreach (var entry in config.ScanPresets)
            if (TryParseEntry(entry, out var preset)) list.Add(preset);
        return list;
    }

    /// <summary>Parses a flag string such as "-T4 -F" into a plan; false with the reason when a flag is not one a
    /// preset can carry.</summary>
    public static bool TryParseFlags(string? flags, out ScanPlan plan, out string error)
    {
        plan = default!;
        error = "";
        var tokens = (flags ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var parsed = CliParser.Parse(tokens);
        if (parsed.Error is not null) { error = parsed.Error; return false; }
        var o = parsed.Options;
        var unsupported = Unsupported(o);
        if (unsupported is not null) { error = unsupported; return false; }
        plan = ScanPlan.FromOptions(o);
        return true;
    }

    /// <summary>The plan of a saved or built-in preset, or null when its flags do not parse.</summary>
    public static ScanPlan? PlanOf(ScanPreset preset) => TryParseFlags(preset.Flags, out var plan, out _) ? plan : null;

    /// <summary>The first preset whose plan equals the plan of the current settings; null means custom.</summary>
    public static ScanPreset? Match(ScanConfig config)
    {
        var current = ScanPlan.FromConfig(config);
        return All(config).FirstOrDefault(p => PlanOf(p) == current);
    }

    public const string FallbackPreviewTarget = "192.168.1.0/24";

    /// <summary>The network the previewed command should name: the typed one, else the detected
    /// one, else the first usable configured subnet, else a generic example.</summary>
    public static string PreviewTarget(string? typedAddress, string? typedCidr, string? detectedCidr, IEnumerable<string> configuredTargets)
    {
        var typed = (typedAddress ?? "").Trim();
        if (typed.Length > 0 && Net.Ipv4.IsValid(typed.Split('/')[0].Trim()))
        {
            if (typed.Contains('/')) return typed;
            var digits = new string((typedCidr ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) && n is >= 1 and <= 32 ? typed + "/" + n : typed;
        }
        if (!string.IsNullOrWhiteSpace(detectedCidr)) return detectedCidr.Trim();
        foreach (var target in configuredTargets)
        {
            var t = (target ?? "").Trim();
            if (t.Length > 0 && !t.Any(char.IsWhiteSpace)) return t;
        }
        return FallbackPreviewTarget;
    }

    /// <summary>The line that runs the plan from a shell. <paramref name="target"/> is the address, range or CIDR block.</summary>
    public static string Command(ScanPlan plan, string target, string program = "IP-Scanner.exe")
    {
        var flags = plan.Flags(runnable: true);
        return string.Join(' ', new[] { program, flags, CliParser.Quote(target) }.Where(s => s.Length > 0));
    }

    /// <summary>Error text when <paramref name="name"/> cannot name a new preset, else null.</summary>
    public static string? ValidateName(string name, IEnumerable<string> taken)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.PresetNameRequired;
        if (name.Length > MaxNameLength) return Loc.PresetNameTooLong;
        if (name.Contains(Separator) || name.IndexOfAny(['#', ';']) >= 0 || name.Any(char.IsControl)) return Loc.PresetNameInvalidChars;
        if (taken.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return Loc.PresetNameTaken;
        return null;
    }

    /// <summary>The stored form of a saved preset: "name | flags".</summary>
    public static string ToEntry(string name, ScanPlan plan) => $"{name} {Separator} {plan.Flags(runnable: false)}";

    public static bool TryParseEntry(string? entry, out ScanPreset preset)
    {
        preset = default!;
        int at = (entry ?? "").IndexOf(Separator);
        if (at <= 0) return false;
        var name = entry![..at].Trim();
        var flags = entry[(at + 1)..].Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || !TryParseFlags(flags, out _, out _)) return false;
        preset = new ScanPreset(name, flags, false);
        return true;
    }

    private static string? Unsupported(CliOptions o)
    {
        if (o.Targets.Count > 0) return "A preset holds flags only, no target.";
        if (o.Mode == CliMode.List) return "-sL is not a scan preset.";
        if (o.TcpType is not null || o.Udp) return "Scan types are not part of a preset.";
        if (o.TopPorts is not null || o.VersionIntensity is not null || o.Checks is not null)
            return "--top-ports, --version-intensity and --checks are not part of a preset.";
        if (o.Ports is { } ports && ports.Contains(':')) return "Protocol prefixes in the port list are not part of a preset.";
        if (o.MinRate is not null || o.MaxRate is not null || o.MaxRetries is not null || o.HostTimeoutMs is not null)
            return "Rate, retry and host timeout flags are not part of a preset.";
        if (o.Outputs.Count > 0 || o.Reason || o.OpenOnly) return "Output flags are not part of a preset.";
        if (o.IncludeFile is not null || o.Exclude is not null || o.ExcludeFile is not null || o.Interface is not null
            || o.Ipv6 || o.Help || o.Version || o.Debug || o.Verbosity > 0)
            return "Only timing, discovery, port and detection flags belong in a preset.";
        return null;
    }
}
