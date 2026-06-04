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
    public static string Last => S("Letzter", "Last");
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

    public static string TotalRow => S("Ø Gesamt", "Ø Total");

    // ── Settings ──
    public static string Settings => S("Einstellungen", "Settings");
    public static string Save => S("Speichern", "Save");
    public static string Cancel => S("Abbrechen", "Cancel");
    public static string TabNetwork => S("Netzwerk", "Network");
    public static string TabPing => S("Ping", "Ping");
    public static string TabInternet => S("Internet", "Internet");
    public static string TabOutput => S("Ausgabe", "Output");
    public static string TabDatabase => S("Datenbank", "Database");
    public static string Subnets => S("Subnetze (eine pro Zeile)", "Subnets (one per line)");
    public static string PinnedIps => S("Angeheftete IPs (eine pro Zeile)", "Pinned IPs (one per line)");
    public static string PingInterval => S("Ping-Intervall", "Ping interval");
    public static string OfflineAfter => S("Offline nach X Fehlversuchen", "Offline after X failures");
    public static string InitPings => S("Such-Pings je IP", "Discovery pings per IP");
    public static string StartupPings => S("Pings bei Start", "Pings at startup");
    public static string ScanThreads => S("Threads pro Scan (0 = max)", "Threads per scan (0 = max)");
    public static string TextScale => S("Textgröße", "Text size");
    public static string OfflineRecheck => S("Offline-Recheck (0 = aus)", "Offline recheck (0 = off)");
    public static string InvalidEntry(string entry) => German
        ? $"Ungültiger Eintrag: {entry}"
        : $"Invalid entry: {entry}";
    public static string ExportConf => S("Einstellungen exportieren …", "Export settings …");
    public static string ImportConf => S("Einstellungen importieren …", "Import settings …");
    public static string TabConfig => S("Konfigurationsdatei", "Configuration file");
    public static string EnableInternet => S("Internet-Ping aktivieren", "Enable internet ping");
    public static string InternetHosts => S("Internet-Hosts (einer pro Zeile)", "Internet hosts (one per line)");
    public static string OutputDir => S("Ausgabeverzeichnis", "Output directory");
    public static string Browse => S("Durchsuchen …", "Browse …");
    public static string WriteTxt => S("TXT-Bericht schreiben", "Write TXT report");
    public static string ExportCsv => S("CSV exportieren", "Export CSV");
    public static string KnownDb => S("Known-Devices-Datenbank aktivieren", "Enable known-devices database");
    public static string ClearDb => S("Datenbank leeren", "Clear database");
    public static string MergeDb => S("Datenbanken zusammenführen …", "Merge databases …");
    public static string DbFile => S("Datenbankdatei", "Database file");
    public static string InternetTimeout => S("Ping-Timeout", "Ping timeout");
    public static string ConfNote => S(
        "Die Einstellungen werden automatisch als ip_scanner.conf im Ordner der Datenbank gespeichert und beim Start geladen.",
        "Settings are saved automatically as ip_scanner.conf next to the database and loaded at startup.");
    public static string MergeDone(int n) => German
        ? $"{n} Einträge zusammengeführt."
        : $"{n} entries merged.";
    public static string ResetDefaults => S("Auf Standard zurücksetzen", "Reset to defaults");
    public static string Threads => S("Threads", "Threads");
    public static string MaxLabel => S("max", "max");
    public static string ClearDbConfirm => S("Known-Devices-Datenbank wirklich leeren?", "Really clear the known-devices database?");
    public static string Confirm => S("Bestätigen", "Confirm");

    // ── Settings tooltips ──
    public static string TipSubnets => S(
        "Diese Netze werden bei jedem Scan zusätzlich zum oben eingegebenen Netz gepingt. Format: 192.168.2.0/24 — mehrere per Zeile oder Komma.",
        "These networks are pinged on every scan in addition to the one entered at the top. Format: 192.168.2.0/24 — multiple per line or comma.");
    public static string TipPinnedIps => S(
        "Diese IPs werden immer mitgepingt (auch außerhalb der Subnetze) und stehen ganz oben in der Tabelle.",
        "These IPs are always pinged (even outside the subnets) and stay at the top of the table.");
    public static string TipScanThreads => S(
        "Parallele Ping-Worker für Suche und Analyse. 0 = ein Thread pro Gerät (maximale Geschwindigkeit, mehr Last). Standard 100.",
        "Parallel ping workers for discovery and analysis. 0 = one thread per device (max speed, more load). Default 100.");
    public static string TipExportConf => S(
        "Speichert alle aktuellen Einstellungen in eine .conf-Datei.",
        "Saves all current settings to a .conf file.");
    public static string TipImportConf => S(
        "Lädt Einstellungen aus einer zuvor exportierten .conf-Datei.",
        "Loads settings from a previously exported .conf file.");
    public static string TipPingInterval => S(
        "Pause in Millisekunden zwischen zwei Pings an dasselbe Gerät. Kleinere Werte = schneller, mehr Netzlast.",
        "Pause in milliseconds between two pings to the same device. Lower = faster, more network load.");
    public static string TipOfflineAfter => S(
        "Nach so vielen Fehlpings in Folge gilt ein Gerät als offline und verschwindet aus der Tabelle.",
        "After this many consecutive failed pings a device counts as offline and leaves the table.");
    public static string TipInitPings => S(
        "Such-Versuche pro IP in der Discovery-Phase (stoppt bei der ersten Antwort). Höher = zuverlässiger bei trägen Geräten, langsamer.",
        "Discovery attempts per IP (stops at the first reply). Higher = more reliable for slow devices, but slower.");
    public static string TipEnableInternet => S(
        "Misst nebenbei die Latenz zu öffentlichen Hosts (Sidebar) — zeigt, ob die Internetverbindung steht.",
        "Also measures latency to public hosts (sidebar) — shows whether the internet connection is up.");
    public static string TipInternetHosts => S(
        "Öffentliche IPs für die Internet-Latenzmessung. Format: IP Leerzeichen Name, z.B. \"8.8.8.8 Google\". Ohne Name wird die IP angezeigt.",
        "Public IPs for the internet latency check. Format: IP space name, e.g. \"8.8.8.8 Google\". Without a name the IP is shown.");
    public static string TipStartupPings => S(
        "Ping-Anzahl je Gerät für den automatischen Scan direkt nach dem Programmstart. 0 = nur Geräte suchen.",
        "Ping count per device for the automatic scan right after startup. 0 = discovery only.");
    public static string TipOfflineRecheck => S(
        "Prüft während eines Durchlaufs alle N Sekunden, ob Offline-IPs online gekommen sind — neue Geräte steigen sofort in den Durchlauf ein. 0 schaltet die Prüfung ab.",
        "While a run is active, rechecks offline IPs every N seconds — devices that come online join the run immediately. 0 disables the recheck.");
    public static string TipTextScale => S(
        "Skaliert die gesamte Oberfläche inklusive aller Texte. 100 = Standardgröße.",
        "Scales the whole interface including all text. 100 = default size.");
    public static string TipOutputDir => S(
        "Ordner, in dem TXT-/CSV-Berichte nach einem Scan gespeichert werden.",
        "Folder where TXT/CSV reports are written after a scan.");
    public static string TipWriteTxt => S(
        "Schreibt nach jedem Scan einen lesbaren Textbericht mit allen Tabellenwerten.",
        "Writes a readable text report with all table values after each scan.");
    public static string TipExportCsv => S(
        "Exportiert die Ergebnisse zusätzlich als CSV — praktisch für Excel oder Skripte.",
        "Additionally exports the results as CSV — handy for Excel or scripts.");
    public static string TipKnownDb => S(
        "Merkt sich gefundene Geräte je Netzwerk in scanner.db, damit bekannte Geräte beim nächsten Scan wiedererkannt werden.",
        "Remembers discovered devices per network in scanner.db so known devices are recognized on the next scan.");
    public static string TipInternetTimeout => S(
        "ICMP-Timeout für die Internet-Host-Pings in Millisekunden. Standard 1000.",
        "ICMP timeout for the internet host pings in milliseconds. Default 1000.");
    public static string TipDbFile => S(
        "Pfad der Known-Devices-Datenbank. Liegt standardmäßig im Scans-Ordner.",
        "Path of the known-devices database. Lives in the scans folder by default.");
    public static string TipMergeDb => S(
        "Wählt die scanner.db einer anderen Programm-Instanz und führt deren Geräte in die eigene Datenbank zusammen (neuere Einträge gewinnen).",
        "Pick another instance's scanner.db and merge its devices into this database (newer entries win).");
    public static string TipResetDefaults => S(
        "Setzt alle Einstellungen auf die Standardwerte zurück (erst beim Speichern übernommen).",
        "Resets all settings to their defaults (applied when you save).");
    public static string TipClearDb => S(
        "Löscht alle gespeicherten Geräte aus der Datenbank. Kann nicht rückgängig gemacht werden.",
        "Deletes all stored devices from the database. Cannot be undone.");
    public static string ScanError => S("Scan-Fehler", "Scan error");
    public static string LargeRange => S("Großer Bereich", "Large range");
    public static string LargeRangeMsg(int cidr, int subnets, long hosts) => German
        ? $"/{cidr} umfasst {subnets} Subnetze (~{hosts:N0} Hosts). Das kann sehr lange dauern. Fortfahren?"
        : $"/{cidr} spans {subnets} subnets (~{hosts:N0} hosts). This can take very long. Continue?";
}
