namespace IpScanner.Core.Cli;

/// <summary>How a flag takes its value.</summary>
public enum CliArg
{
    /// <summary>A switch.</summary>
    None,
    /// <summary>The value follows as the next argument or after "=".</summary>
    Value,
    /// <summary>Like <see cref="Value"/>, and a short flag may carry it attached (-p22, -T4).</summary>
    ValueOrAttached
}

/// <summary>One command line flag. The table <see cref="CliFlags.All"/> drives the parser, the help text
/// and the generated documentation, so a flag exists in exactly one place.</summary>
/// <param name="Id">Stable identifier the parser switches on.</param>
/// <param name="Names">Every spelling, short and long.</param>
/// <param name="ArgName">Placeholder shown in the help for a flag with a value.</param>
/// <param name="Group">Help section.</param>
public sealed record CliFlag(string Id, string[] Names, CliArg Arg, string? ArgName, string Group, string Help)
{
    public string Display => string.Join(", ", Names.Select(Spell));

    /// <summary>The command line tokens that spell this flag with <paramref name="value"/>, the way the parser reads them back.</summary>
    public string[] Tokens(string? value = null) => Arg switch
    {
        CliArg.None => [Names[0]],
        CliArg.ValueOrAttached when Names[0].Length == 2 => [Names[0] + CliParser.Quote(value ?? "")],
        _ => [Names[0], CliParser.Quote(value ?? "")]
    };

    private string Spell(string name) => Arg switch
    {
        CliArg.None => name,
        CliArg.ValueOrAttached when name.Length == 2 => $"{name}<{ArgName}>",
        _ => $"{name} <{ArgName}>"
    };
}

/// <summary>The flag table.</summary>
public static class CliFlags
{
    /// <summary>The flag with this id.</summary>
    public static CliFlag ById(string id) => All.First(f => f.Id == id);

    public const string GroupTarget = "Targets";
    public const string GroupScan = "Scan type";
    public const string GroupPorts = "Ports";
    public const string GroupDetect = "Detection";
    public const string GroupTiming = "Timing";
    public const string GroupOutput = "Output";
    public const string GroupSafety = "Safety gates";
    public const string GroupMisc = "Miscellaneous";

    public static readonly IReadOnlyList<string> Groups =
        [GroupTarget, GroupScan, GroupPorts, GroupDetect, GroupTiming, GroupOutput, GroupSafety, GroupMisc];

    private static CliFlag F(string id, string names, CliArg arg, string? argName, string group, string help) =>
        new(id, names.Split(' ', StringSplitOptions.RemoveEmptyEntries), arg, argName, group, help);

    public static readonly IReadOnlyList<CliFlag> All =
    [
        F("iL", "-iL", CliArg.Value, "file", GroupTarget, "Read targets from a file, one spec per line."),
        F("exclude", "--exclude", CliArg.Value, "specs", GroupTarget, "Comma separated targets to leave out."),
        F("exclude-file", "--exclude-file", CliArg.Value, "file", GroupTarget, "Read the targets to leave out from a file."),
        F("6", "-6", CliArg.None, null, GroupTarget, "Scan IPv6 targets. Required for them, and IPv4 targets are then refused."),

        F("sn", "-sn", CliArg.None, null, GroupScan, "Discovery only: find the live hosts, scan no ports."),
        F("Pn", "-Pn", CliArg.None, null, GroupScan, "Skip discovery and treat every target as up."),
        F("sL", "-sL", CliArg.None, null, GroupScan, "List the targets with their reverse names, send nothing to them."),
        F("sT", "-sT", CliArg.None, null, GroupScan, "TCP connect scan (default, needs no driver)."),
        F("sS", "-sS", CliArg.None, null, GroupScan, "TCP SYN scan (needs the packet driver)."),
        F("sU", "-sU", CliArg.None, null, GroupScan, "UDP scan; alone it replaces the TCP scan, with a TCP type it adds to it."),
        F("sF", "-sF", CliArg.None, null, GroupScan, "TCP FIN scan (needs the packet driver and --allow-aggressive)."),
        F("sX", "-sX", CliArg.None, null, GroupScan, "TCP Xmas scan (needs the packet driver and --allow-aggressive)."),
        F("sN", "-sN", CliArg.None, null, GroupScan, "TCP Null scan (needs the packet driver and --allow-aggressive)."),
        F("sA", "-sA", CliArg.None, null, GroupScan, "TCP ACK scan: maps firewall rules (needs the packet driver)."),
        F("sW", "-sW", CliArg.None, null, GroupScan, "TCP window scan (needs the packet driver)."),
        F("sM", "-sM", CliArg.None, null, GroupScan, "TCP Maimon scan (needs the packet driver and --allow-aggressive)."),

        F("p", "-p --ports", CliArg.ValueOrAttached, "ports", GroupPorts, "Ports to scan: 22,80,8000-9000, T:80,U:53; use -p- for all 65535 ports."),
        F("F", "-F", CliArg.None, null, GroupPorts, "Fast: the 100 most common ports."),
        F("top-ports", "--top-ports", CliArg.Value, "n", GroupPorts, "Scan the n most common ports."),

        F("sV", "-sV", CliArg.None, null, GroupDetect, "Identify the service and version behind each open port."),
        F("version-intensity", "--version-intensity", CliArg.Value, "0-9", GroupDetect, "How hard -sV probes (0 listens only, 9 tries every probe)."),
        F("O", "-O", CliArg.None, null, GroupDetect, "Estimate the operating system (needs the packet driver)."),
        F("checks", "--checks", CliArg.Value, "list", GroupDetect, "Run service checks: safe, discovery, intrusive (comma separated)."),

        F("T", "-T", CliArg.ValueOrAttached, "0-5", GroupTiming, "Timing template, 0 paranoid to 5 insane (default 3)."),
        F("min-rate", "--min-rate", CliArg.Value, "n", GroupTiming, "Send at least n probes per second."),
        F("max-rate", "--max-rate", CliArg.Value, "n", GroupTiming, "Send at most n probes per second."),
        F("max-retries", "--max-retries", CliArg.Value, "n", GroupTiming, "Retransmissions per probe."),
        F("host-timeout", "--host-timeout", CliArg.Value, "time", GroupTiming, "Give up on a host after this time (ms, s, m or h suffix)."),

        F("oN", "-oN", CliArg.Value, "file", GroupOutput, "Write the text report to a file (- for stdout)."),
        F("oX", "-oX", CliArg.Value, "file", GroupOutput, "Write the XML report to a file (- for stdout)."),
        F("oJ", "-oJ", CliArg.Value, "file", GroupOutput, "Write the JSON report to a file (- for stdout)."),
        F("oG", "-oG", CliArg.Value, "file", GroupOutput, "Write the grepable report to a file (- for stdout)."),
        F("reason", "--reason", CliArg.None, null, GroupOutput, "Show why each port got its state."),
        F("open", "--open", CliArg.None, null, GroupOutput, "Report only open (and open|filtered) ports."),

        F("allow-full-scan", "--allow-full-scan", CliArg.None, null, GroupSafety, "Allow a chosen scan of more than 100 ports (-p, --top-ports), up to all 65535."),
        F("allow-large", "--allow-large", CliArg.None, null, GroupSafety, "Allow more than 65536 targets."),
        F("allow-intrusive", "--allow-intrusive", CliArg.None, null, GroupSafety, "Allow the intrusive check category."),
        F("allow-aggressive", "--allow-aggressive", CliArg.None, null, GroupSafety, "Allow the FIN, Null, Xmas and Maimon scans."),
        F("i-know-what-i-am-doing", "--i-know-what-i-am-doing", CliArg.None, null, GroupSafety, "Hide the notice printed for aggressive scans (the gates above still apply)."),

        F("interface", "--interface -e", CliArg.Value, "ip|name", GroupMisc, "Send from this network adapter (IPv4 address or adapter name). Applies to IPv4 targets; IPv6 targets leave through the adapter the system routes them over."),
        F("v", "-v -vv -vvv", CliArg.None, null, GroupMisc, "More progress on stderr (-vv for per-host detail)."),
        F("d", "-d", CliArg.None, null, GroupMisc, "Debug log on stderr."),
        F("help", "-h --help", CliArg.None, null, GroupMisc, "Show this help."),
        F("version", "--version", CliArg.None, null, GroupMisc, "Show the version."),
    ];
}
