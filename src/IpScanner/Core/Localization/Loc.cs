using System.Globalization;

namespace IpScanner.Core.Localization;

/// <summary>
/// Tiny two-language (DE/EN) string table. Language is auto-detected from the
/// system UI culture once at startup; English is the fallback for anything
/// that isn't German. Exposed as static properties so XAML can bind via x:Static.
/// </summary>
public static class Loc
{
    public static bool German { get; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);

    private static string S(string de, string en) => German ? de : en;

    // ── Header / controls ──
    public static string Scan => S("▶ Scan", "▶ Scan");
    public static string Stop => S("■ Stopp", "■ Stop");
    public static string PingsLabel => S("Pings:", "Pings:");
    public static string SubnetTooltip => S(
        "Basis-IP, z.B. 192.168.1.0  (leer = automatisch). Eigene Maske: 10.0.0.0/22",
        "Base IP, e.g. 192.168.1.0  (empty = auto). Custom mask: 10.0.0.0/22");
    public static string MaskTooltip => S(
        "Netzmaske: /24 (254 Hosts), /16 (256 Subnetze), /8. Eigener Wert im IP-Feld.",
        "Mask: /24 (254 hosts), /16 (256 subnets), /8. Custom value in the IP field.");

    // ── Progress / legend ──
    public static string Devices => S("Geräte", "Devices");
    public static string Pings => S("Pings", "Pings");
    public static string Online => S("Online", "Online");
    public static string Offline => S("Offline", "Offline");
    public static string Unknown => S("Unbekannt", "Unknown");
    public static string Success => S("Erfolg", "Success");
    public static string Fail => S("Fehlschlag", "Failed");
    public static string Skipped => S("Übersprungen", "Skipped");

    // ── Table headers ──
    public static string IpAddress => S("IP-Adresse", "IP address");
    public static string Status => S("Status", "Status");
    public static string Group => S("Gruppe", "Group");
    public static string Hostname => S("Hostname", "Hostname");
    public static string Avg => S("Ø", "Avg");
    public static string Min => S("Min", "Min");
    public static string Max => S("Max", "Max");
    public static string Last => S("Letzt", "Last");
    public static string Progress => S("Fortschritt", "Progress");
    public static string Mac => S("MAC", "MAC");

    // ── Sidebar ──
    public static string Network => S("Netzwerk", "Network");
    public static string OwnIp => S("Eigene IP", "Own IP");
    public static string Gateway => S("Gateway", "Gateway");
    public static string Mask => S("Maske", "Mask");
    public static string Dns => S("DNS", "DNS");
    public static string Interface => S("Interface", "Interface");
    public static string AvgLatency => S("Ø Latenz", "Avg latency");
    public static string InternetLatency => S("INTERNET-LATENZ", "INTERNET LATENCY");
    public static string LastExport => S("LETZTER EXPORT", "LAST EXPORT");

    // ── Phases ──
    public static string PhaseReady => S("Bereit", "Ready");
    public static string PhaseSearching => S("Suche Geräte …", "Discovering …");
    public static string PhaseAnalysis => S("Analyse …", "Analysis …");

    // ── Settings ──
    public static string Settings => S("Einstellungen", "Settings");
    public static string Save => S("Speichern", "Save");
    public static string Cancel => S("Abbrechen", "Cancel");
    public static string TabNetwork => S("Netzwerk", "Network");
    public static string TabPing => S("Ping", "Ping");
    public static string TabInternet => S("Internet", "Internet");
    public static string TabOutput => S("Ausgabe", "Output");
    public static string TabPerformance => S("Performance", "Performance");
    public static string TabDatabase => S("Datenbank", "Database");
    public static string Subnets => S("Subnetze (eine pro Zeile)", "Subnets (one per line)");
    public static string PinnedIps => S("Angeheftete IPs (eine pro Zeile)", "Pinned IPs (one per line)");
    public static string PingCount => S("Ping-Anzahl", "Ping count");
    public static string PingInterval => S("Ping-Intervall (ms)", "Ping interval (ms)");
    public static string OfflineAfter => S("Offline nach N Fehlversuchen", "Offline after N failures");
    public static string InitPings => S("Such-Pings je IP", "Discovery pings per IP");
    public static string HighPressure => S("High-Pressure-Modus", "High-pressure mode");
    public static string EnableInternet => S("Internet-Ping aktivieren", "Enable internet ping");
    public static string InternetHosts => S("Internet-Hosts (einer pro Zeile)", "Internet hosts (one per line)");
    public static string OutputDir => S("Ausgabeverzeichnis", "Output directory");
    public static string Browse => S("Durchsuchen …", "Browse …");
    public static string WriteTxt => S("TXT-Bericht schreiben", "Write TXT report");
    public static string ExportCsv => S("CSV exportieren", "Export CSV");
    public static string AnalysisThreads => S("Analyse-Threads", "Analysis threads");
    public static string DiscoveryThreads => S("Such-Threads", "Discovery threads");
    public static string KnownDb => S("Known-Devices-Datenbank aktivieren", "Enable known-devices database");
    public static string ClearDb => S("Datenbank leeren", "Clear database");
    public static string ClearDbConfirm => S("Known-Devices-Datenbank wirklich leeren?", "Really clear the known-devices database?");
    public static string Confirm => S("Bestätigen", "Confirm");
    public static string ScanError => S("Scan-Fehler", "Scan error");
    public static string LargeRange => S("Großer Bereich", "Large range");
    public static string LargeRangeMsg(int cidr, int subnets, long hosts) => German
        ? $"/{cidr} umfasst {subnets} Subnetze (~{hosts:N0} Hosts). Das kann sehr lange dauern. Fortfahren?"
        : $"/{cidr} spans {subnets} subnets (~{hosts:N0} hosts). This can take very long. Continue?";
}
