using IpScanner.Core.Export;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Cli;

/// <summary>What the scan does besides the port scan.</summary>
public enum CliMode
{
    /// <summary>Discovery, then a port scan of the live hosts.</summary>
    Scan,
    /// <summary>Discovery only (-sn).</summary>
    Ping,
    /// <summary>List the targets and their names, send nothing (-sL).</summary>
    List
}

/// <summary>The parsed command line.</summary>
public sealed class CliOptions
{
    public List<string> Targets { get; } = [];
    public string? IncludeFile { get; set; }
    public string? Exclude { get; set; }
    public string? ExcludeFile { get; set; }

    public CliMode Mode { get; set; } = CliMode.Scan;
    public bool SkipDiscovery { get; set; }

    /// <summary>The TCP scan type; null means the default (connect). A TCP type switches TCP on.</summary>
    public ScanType? TcpType { get; set; }
    public bool Udp { get; set; }

    public string? Ports { get; set; }
    public bool Fast { get; set; }
    public int? TopPorts { get; set; }

    public bool ServiceDetection { get; set; }
    public int? VersionIntensity { get; set; }
    public bool OsDetection { get; set; }
    public string? Checks { get; set; }

    public int? Timing { get; set; }
    public int? MinRate { get; set; }
    public int? MaxRate { get; set; }
    public int? MaxRetries { get; set; }
    public int? HostTimeoutMs { get; set; }

    /// <summary>Output file per format; "-" is stdout.</summary>
    public Dictionary<ReportFormat, string> Outputs { get; } = [];
    public bool Reason { get; set; }
    public bool OpenOnly { get; set; }

    public bool AllowFullScan { get; set; }
    public bool AllowLarge { get; set; }
    public bool AllowIntrusive { get; set; }
    public bool AllowAggressive { get; set; }
    /// <summary>Hides the notice a run with aggressive settings prints.</summary>
    public bool AcknowledgeRisk { get; set; }

    public string? Interface { get; set; }
    public int Verbosity { get; set; }
    public bool Debug { get; set; }
    public bool Ipv6 { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }

    /// <summary>The arguments as typed, for the report header.</summary>
    public string CommandLine { get; set; } = "";
}

/// <summary>Outcome of <see cref="CliParser.Parse"/>: the options, or the usage error to print.</summary>
public sealed record CliParseResult(CliOptions Options, string? Error)
{
    public bool Ok => Error is null;
}
