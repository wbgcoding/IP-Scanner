using IpScanner.Core.Localization;

namespace IpScanner.Core.Cli;

/// <summary>The content of the in-app help reference, built from the same tables as the command line help.</summary>
public static class HelpDoc
{
    public sealed record Tool(string Name, string Description);

    public sealed record Section(string Title, IReadOnlyList<(string Option, string Help)> Rows);

    /// <summary>What each tool does, in the UI language.</summary>
    public static IReadOnlyList<Tool> Tools() =>
    [
        new(Loc.HelpToolDiscoveryName, Loc.HelpToolDiscoveryDesc),
        new(Loc.ToolPortCheck, Loc.ToolPortCheckDesc),
        new(Loc.HelpToolServicesName, Loc.HelpToolServicesDesc),
        new(Loc.HelpToolOsName, Loc.HelpToolOsDesc),
        new(Loc.HelpToolChecksName, Loc.HelpToolChecksDesc),
        new(Loc.HelpToolExportName, Loc.HelpToolExportDesc),
        new(Loc.ToolWakeOnLan, Loc.ToolWolDesc),
        new(Loc.ToolDnsLookup, Loc.ToolDnsLookupDesc),
        new(Loc.ToolPingRoute, Loc.ToolPingRouteDesc),
        new(Loc.ToolCalculator, Loc.ToolCalculatorDesc),
        new(Loc.ToolCertificate, Loc.ToolCertificateDesc),
    ];

    /// <summary>Every command line option, grouped as in the command line help, described in the UI language.</summary>
    public static IReadOnlyList<Section> Options() =>
        CliFlags.Groups
            .Select(g => new Section(Loc.HelpGroup(g), CliFlags.All.Where(f => f.Group == g).Select(f => (f.Display, Loc.HelpOption(f.Id))).ToList()))
            .Where(s => s.Rows.Count > 0)
            .ToList();

    /// <summary>The examples of the command line help with their explanation in the UI language.</summary>
    public static IReadOnlyList<(string Command, string Description)> Examples() =>
        CliHelp.Examples.Select((e, i) => ("IP-Scanner.exe " + e.Args, Loc.HelpExample(i))).ToList();

    /// <summary>The exit codes with their meaning in the UI language.</summary>
    public static IReadOnlyList<(int Code, string Meaning)> ExitCodes() =>
        CliExit.Table.Select(e => (e.Code, Loc.HelpExit(e.Code))).ToList();
}
