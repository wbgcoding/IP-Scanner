using System.Globalization;
using System.Text.Json.Serialization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;

namespace IpScanner.Core.Export;

/// <summary>Scan facts that are not part of the device list: where the scan ran, how it was started and whether it finished.</summary>
public sealed record ReportMeta(string Tool, string ToolVersion, DateTimeOffset? StartedAt,
    string? Command, string? Targets, bool Partial, string Interface, string? LocalIp, string? Gateway,
    string? SubnetMask, IReadOnlyList<string> Dns)
{
    /// <summary>Time as the text report shows it (UI culture); the structured formats use <see cref="StartedAt"/>.</summary>
    [JsonIgnore]
    public string? DisplayTime { get; init; }

    /// <summary>The TCP technique of the run in lower case (connect, syn, ack, window, ...); null when no TCP scan ran.</summary>
    public string? Technique { get; init; }

    /// <summary>ACK, Window and Maimon runs report mostly closed, unfiltered or filtered ports, so the text report
    /// lists those states too, the way nmap does, instead of only the open ones.</summary>
    [JsonIgnore]
    public bool ListsAllStates => Technique is "ack" or "window" or "maimon";
}

/// <summary>One port of one host. State uses the nmap words (open, closed, filtered, open|filtered, unfiltered);
/// Reason is empty when the source only knew the port was open.</summary>
public sealed record ReportPort(int Port, string Protocol, string State, string? Reason, string? Service,
    string? Version, string? Confidence)
{
    [JsonIgnore]
    public string Label => $"{Port}/{Protocol.ToUpperInvariant()}";
    [JsonIgnore]
    public bool IsOpen => State == "open";
}

public sealed record ReportCheck(string Id, string Category, string? Service, string Outcome,
    IReadOnlyList<KeyValuePair<string, string>> Findings);

public sealed record ReportOs(OsFamily Family, int Percent, bool FromFingerprint, string? Match, int? MatchPercent, int? Hops = null);

public sealed record ReportHost(string Ip, string Status, string? Hostname, string? Mac, string? Vendor, string Group,
    string? Discovery, double? AvgMs, double? MinMs, double? MaxMs, double? LastMs, int PingsDone, string PingsTarget,
    bool FromDb, string? Note, ReportOs? Os, IReadOnlyList<ReportPort> Ports, IReadOnlyList<ReportCheck> Checks)
{
    [JsonIgnore]
    public bool IsOnline => Status == ReportBuilder.Online;

    /// <summary>"ipv4" or "ipv6" (nmap's addrtype words); derived from <see cref="Ip"/>, so older files read the same.</summary>
    [JsonPropertyName("family")]
    public string Family => ReportBuilder.FamilyOf(Ip);
}

public sealed record ReportFinding(string Ip, string? Mac, string Kind, string? Detail);

/// <summary>The single result model behind every report format: metadata, hosts with their ports, OS guess and
/// check results, and the cross-host findings. The writers in <see cref="ReportWriters"/> render it.</summary>
public sealed record ScanReport(ReportMeta Meta, IReadOnlyList<ReportHost> Hosts, IReadOnlyList<ReportFinding> Findings)
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(-1)]
    public int Schema => SchemaVersion;
}

/// <summary>Optional inputs a scan may have beyond the device list.</summary>
public sealed class ReportExtras
{
    public string? Command { get; init; }
    public string? Targets { get; init; }
    public bool Partial { get; init; }

    /// <summary>The TCP technique the run used; null for a ping-only or UDP-only run.</summary>
    public ScanType? Technique { get; init; }

    /// <summary>Port results with state and reason; wins over the open-service map for the hosts it names.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PortResult>>? PortResults { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<CheckResult>>? Checks { get; init; }
    public IReadOnlyList<ConflictFinding>? Findings { get; init; }
}

public static class ReportBuilder
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string Listed = "LISTED";

    private static readonly string[] TimestampFormats = { "yyyyMMdd_HHmmss_fff", "yyyyMMdd_HHmmss" };

    public static ScanReport Build(IEnumerable<Device> devices, NetworkInfo info, string timestamp, string? displayTime,
        IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null, ReportExtras? extras = null)
    {
        DateTimeOffset? started = DateTime.TryParseExact(timestamp, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? new DateTimeOffset(parsed) : null;
        var meta = new ReportMeta("IP-Scanner", UpdateService.CurrentVersion.ToString(3), started,
            extras?.Command, extras?.Targets, extras?.Partial ?? false, info.Interface, info.Ip, info.Gateway,
            info.SubnetMask, info.DnsServers.ToList())
        {
            DisplayTime = displayTime ?? timestamp,
            Technique = extras?.Technique is { } technique ? ScanTypes.Label(technique).ToLowerInvariant() : null,
        };

        var hosts = devices.Where(d => d.IsOnline || d.FromDb || d.Seen)
            .OrderBy(d => d.Ip, Net.IpAddressKey.TextComparer)
            .Select(d => Host(d, ports, extras))
            .ToList();
        var findings = (extras?.Findings ?? Array.Empty<ConflictFinding>())
            .Select(f => new ReportFinding(f.Ip, f.Mac, KindId(f.Kind), f.Detail)).ToList();
        return new ScanReport(meta, hosts, findings);
    }

    /// <summary>The address family word the structured formats carry; text that is no IPv6 address counts as ipv4.</summary>
    public static string FamilyOf(string ip) => Net.Ipv6Address.TryParse(ip, out _) ? "ipv6" : "ipv4";

    public static string KindId(ConflictKind kind) => kind switch
    {
        ConflictKind.MacChanged => "macChanged",
        ConflictKind.MacOnMultipleIps => "macOnMultipleIps",
        _ => "multipleMacsForOneIp"
    };

    public static string StateId(PortState state) => state switch
    {
        PortState.Open => "open",
        PortState.Closed => "closed",
        PortState.Filtered => "filtered",
        PortState.OpenFiltered => "open|filtered",
        _ => "unfiltered"
    };

    private static ReportHost Host(Device d, IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports, ReportExtras? extras)
    {
        IReadOnlyList<NetService>? services = null;
        if (ports is not null && ports.TryGetValue(d.Ip, out var found)) services = found;

        var list = new List<ReportPort>();
        if (extras?.PortResults is { } results && results.TryGetValue(d.Ip, out var rows))
        {
            foreach (var r in rows.OrderBy(r => r.Port).ThenBy(r => r.Protocol))
            {
                var s = r.Service;
                list.Add(new ReportPort(r.Port, r.ProtoLabel.ToLowerInvariant(), StateId(r.State), r.Reason, s?.Name, s?.Version,
                    s is null ? null : s.Confidence.ToString().ToLowerInvariant()));
            }
            services ??= rows.Where(r => r.IsOpen && r.Service is not null).Select(r => r.Service!).ToList();
        }
        else if (services is not null)
        {
            foreach (var s in services.OrderBy(s => s.Port).ThenBy(s => s.Protocol))
                list.Add(new ReportPort(s.Port, s.ProtoLabel.ToLowerInvariant(), "open", null, s.Name,
                    s.Version, s.Confidence.ToString().ToLowerInvariant()));
        }

        var checks = new List<ReportCheck>();
        if (extras?.Checks is { } byIp && byIp.TryGetValue(d.Ip, out var results2))
        {
            foreach (var c in results2)
                checks.Add(new ReportCheck(c.CheckId, c.Category.ToString().ToLowerInvariant(), c.Service.PortLabel,
                    c.Outcome.ToString(), c.Findings.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList()));
        }

        var est = OsDisplay.For(d, services);
        var os = est.IsKnown ? new ReportOs(est.Family, est.Percent, est.FromFingerprint, est.Match, est.MatchPercent, est.Hops) : null;
        return new ReportHost(d.Ip,
            d.IsListed ? Listed : d.IsOnline ? Online : Offline,
            d.Hostname is null or Device.Unknown ? null : d.Hostname,
            d.Mac is null or Device.Unknown ? null : d.Mac,
            MacVendorLookup.Instance.Lookup(d.Mac),
            CsvExporter.ExportGroup(d.GroupId),
            d.DiscoveryMethod, d.AvgMs, d.MinMs, d.MaxMs, d.LastFailed ? null : d.LastMs,
            d.CurrentPings,
            d.TargetPings == ScanConfig.InfinitePingCount ? "inf" : d.TargetPings.ToString(CultureInfo.InvariantCulture),
            d.FromDb, string.IsNullOrEmpty(d.Note) ? null : d.Note, os, list, checks);
    }
}
