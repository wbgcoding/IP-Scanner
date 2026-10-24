using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Localization;

/// <summary>
/// UI string table. German and English are built in; further languages load from
/// embedded per-language JSON maps (English text -> translation) with English as
/// the fallback for any missing entry. Static properties so XAML binds via x:Static.
/// </summary>
public static class Loc
{
    /// <summary>Supported languages (code + endonym) for the settings dropdown.</summary>
    // English first, then the rest alphabetical by their English name (Arabic,
    // Chinese, French, German, Hindi, Japanese, Portuguese, Russian, Spanish) -
    // the endonyms themselves span too many scripts to sort meaningfully.
    // System/auto is prepended by the settings dropdown, not listed here.
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("ar", "العربية"), ("zh", "中文"), ("fr", "Français"),
        ("de", "Deutsch"), ("hi", "हिन्दी"), ("ja", "日本語"), ("pt", "Português"),
        ("ru", "Русский"), ("es", "Español"),
    };

    public static bool IsSupported(string code) => Array.Exists(Languages, l => l.Code == code);

    public static bool German { get; private set; } = SystemLang == "de";
    /// <summary>True for a right-to-left language (Arabic) - mirror the layout.</summary>
    public static bool IsRtl { get; private set; } = SystemLang == "ar";
    // Immutable once built; swapped atomically. volatile for cross-thread visibility.
    private static volatile Dictionary<string, string>? _active;   // English -> translation; null for en/de

    /// <summary>"Automatic (Deutsch)": the automatic entry of the language box, naming the language the
    /// system setting resolves to, so the closed box shows what "automatic" currently means.</summary>
    public static string LangAutoDetected
    {
        get
        {
            var code = SystemLang;
            var name = Array.Find(Languages, l => l.Code == code).Name;
            return $"{LangAuto} ({name})";
        }
    }

    private static string SystemLang
    {
        get
        {
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return IsSupported(two) ? two : "en";
        }
    }

    /// <summary>"auto" or a language code - must run before any UI loads (x:Static caches).</summary>
    public static void SetLanguage(string mode)
    {
        var code = mode.ToLowerInvariant();
        if (code == "auto" || !IsSupported(code)) code = SystemLang;
        German = code == "de";
        IsRtl = code == "ar";
        _active = code is "en" or "de" ? null : LoadLang(code);
    }

    private static readonly Dictionary<string, Dictionary<string, string>> _cache = new();
    private static readonly object _cacheLock = new();

    private static Dictionary<string, string>? LoadLang(string code)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(code, out var cached)) return cached;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith($"lang.{code}.json", StringComparison.OrdinalIgnoreCase));
                if (name is null) return null;
                using var s = asm.GetManifestResourceStream(name);
                var map = s is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                if (map is not null) _cache[code] = map;
                return map;
            }
            catch { return null; }   // missing/corrupt file -> English fallback
        }
    }

    /// <summary>When set, every S() call records its English key here - the
    /// translation-coverage test uses this to know exactly which keys every language
    /// file must have, without re-parsing the source.</summary>
    internal static HashSet<string>? Collector;

    [ThreadStatic] private static bool _forceEnglish;

    /// <summary>Runs <paramref name="build"/> with every string in English, whatever the active language.</summary>
    public static T InEnglish<T>(Func<T> build)
    {
        var before = _forceEnglish;
        _forceEnglish = true;
        try { return build(); }
        finally { _forceEnglish = before; }
    }

    private static string S(string de, string en)
    {
        Collector?.Add(en);
        if (_forceEnglish) return en;
        return _active is not null && _active.TryGetValue(en, out var t) && t.Length > 0 ? t
           : German ? de : en;
    }

    private static string SF(string de, string en, params object[] args) => string.Format(S(de, en), args);

    // ── App / shared ──
    public static string AppTitle => "IP-Scanner";
    public static string Ok => S("OK", "OK");
    public static string NotAvailable => S("N/A", "N/A");
    public static string StatusOnline => Online.ToUpperInvariant();
    public static string StatusOffline => Offline.ToUpperInvariant();
    public static string TxtExport => S("TXT Export", "TXT Export");
    public static string CsvExport => S("CSV Export", "CSV Export");
    public static string ManualExportTitle => S("Scan-Ergebnisse exportieren", "Export scan results");
    public static string ManualExportScope => S("Welche Geräte?", "Which devices?");
    public static string CurrentView => S("Aktuelle Ansicht", "Current view");
    public static string SelectedDevices => S("Ausgewählte Geräte", "Selected devices");
    public static string ManualExportFormat => S("Dateiformat", "File format");
    public static string ManualExportAction => S("Exportieren …", "Export …");
    public static string CopyView => S("Ansicht kopieren", "Copy view");
    public static string CopyViewText => S("Als Text", "As text");
    public static string CopyViewTable => S("Als Markdown-Tabelle", "As Markdown table");
    public static string NoExportRows => S("In diesem Bereich gibt es keine Geräte. Wähle einen anderen Bereich oder starte einen Scan.",
        "There are no devices in this scope. Choose another scope or run a scan.");
    public static string ManualExportFailed => S("Export fehlgeschlagen", "Export failed");
    public static string ExportsHeaderFmt => S("Dateien ({0})", "Files ({0})");
    public static string ReportTitle => S("NETZWERK-SCAN-BERICHT", "NETWORK SCAN REPORT");
    public static string ReportTimestamp => S("Zeitstempel", "Timestamp");
    public static string ReportNetworkInfo => S("Netzwerkinformationen", "Network Information");
    public static string ReportUnknown => S("Unbekannt", "Unknown");
    public static string DbFileFilter => S(
        "Datenbank (*.db)|*.db|Alle Dateien (*.*)|*.*",
        "Database (*.db)|*.db|All files (*.*)|*.*");
    // For the plain-CSV database export/import, not the .db merge dialog above.
    public static string DbCsvFileFilter => S(
        "CSV-Datei (*.csv)|*.csv|Alle Dateien (*.*)|*.*",
        "CSV file (*.csv)|*.csv|All files (*.*)|*.*");
    public static string ConfFileFilter => S(
        "Konfiguration (*.conf)|*.conf|Alle Dateien (*.*)|*.*",
        "Configuration (*.conf)|*.conf|All files (*.*)|*.*");
    // Copy/save a device's open ports.
    public static string CopyOpenPorts => S("Offene Ports kopieren", "Copy open ports");
    public static string CopyRowAsText => S("Zeile als Text kopieren", "Copy row as text");
    public static string SavePorts => S("Ports speichern", "Save ports");
    public static string TipSavePorts => S(
        "Offene Ports dieses Geräts als Textdatei speichern", "Save this device's open ports as a text file");
    public static string SavePortsTitle => S("Offene Ports speichern", "Save open ports");
    public static string PortsFileFilter => S(
        "Text (*.txt)|*.txt|Alle Dateien (*.*)|*.*", "Text (*.txt)|*.txt|All files (*.*)|*.*");
    public static string TipOpenScans => S("Scan-Ordner öffnen", "Open the scans folder");
    public static string TipResetValue => S("Zurück zum automatischen Wert", "Back to the automatic value");
    public static string AllDevices => S("Alle Geräte", "All devices");
    public static string LatencyHistory => S("LATENZ-VERLAUF", "LATENCY HISTORY");
    // Latency history over days - view switcher on the detail panel.
    public static string RangeMinute => S("Letzte Minute", "Last minute");
    public static string RangeDay => S("24 Stunden", "24 hours");
    public static string RangeWeek => S("7 Tage", "7 days");
    public static string TipAddGraph => S(
        "Weiteren Graphen für ein anderes Gerät hinzufügen (max. 10)",
        "Add another graph for a different device (max. 10)");
    public static string TipRemoveGraph => S("Graphen entfernen", "Remove graph");
    public static string TipGraphSource => S(
        "Quelle des Graphen: Gesamtdurchschnitt oder ein einzelnes Gerät.",
        "Graph source: overall average or a single device.");
    public static string TipEditCell => S(
        "Doppelklick zum Bearbeiten. Änderungen werden dauerhaft gespeichert.",
        "Double-click to edit. Changes are stored permanently.");
    public static string TipGroupColorCell => S(
        "Von Hand gewählte Farbe. Sie hat Vorrang vor der automatischen Gruppenfarbe; Zurücksetzen stellt die automatische wieder her.",
        "Manually chosen color. It overrides the automatic group color; Reset restores the automatic one.");
    public static string TipPinIp => S(
        "Klick heftet diese IP an (immer oben in der Tabelle).",
        "Click to pin this IP (always at the top of the table).");
    public static string TipUnpinIp => S(
        "Klick entfernt den Pin.",
        "Click to remove the pin.");
    public static string ConfDirLabel => S("Pfad der Konfigurationsdatei", "Configuration file path");
    public static string TipConfDir => S(
        "Speicherort der ip_scanner.conf. Standard: neben der Programmdatei. Der Dateiname bleibt ip_scanner.conf.",
        "Where ip_scanner.conf is stored. Default: next to the program file. The file name stays ip_scanner.conf.");
    public static string TotalLabel => S("Gesamt", "Total");
    public static string NamePlaceholder => S("Name", "Name");
    public static string TipChipColor => S("Farbe wählen", "Pick a color");
    public static string TipRemoveChip => S("Eintrag entfernen", "Remove entry");
    public static string TipEditEntry => S("Klicken zum Bearbeiten", "Click to edit");
    public static string TipConfirmRemove => S("Nochmal klicken zum Entfernen", "Click again to remove");
    public static string TipPrimaryNetworkLocked => S("Automatisch erkanntes Netzwerk, kann nicht entfernt werden", "Auto-detected network, cannot be removed");
    // Undo hint after removing a sidebar entry.
    public static string EntryRemovedFmt => S("{0} entfernt", "{0} removed");
    public static string EntryRemoved(string name) => string.Format(EntryRemovedFmt, name);
    public static string UndoLabel => S("Rückgängig", "Undo");
    public static string ErrorDialogCause => S("Etwas ist unerwartet fehlgeschlagen.", "Something failed unexpectedly.");
    public static string ErrorDialogNextStep => S("Versuche es erneut. Tritt der Fehler weiter auf, hilft ein Blick ins Protokoll.", "Try again. If it keeps happening, check the log for details.");
    // Central failure wording (FailureText.Describe): a cause and a next step per exception family.
    public static string FailNoWriteFmt => S("Keine Schreibrechte für „{0}“.", "No write access to \"{0}\".");
    public static string FailNoWrite => S("Keine Schreibrechte.", "No write access.");
    public static string FailNoWriteNext => S("Wähle einen anderen Ordner oder gib dir dort Schreibrechte.", "Choose a different folder or grant yourself write access there.");
    public static string FailFileBusyFmt => S("Die Datei „{0}“ ist in einem anderen Programm geöffnet oder der Datenträger ist voll.", "The file \"{0}\" is open in another program or the disk is full.");
    public static string FailFileBusy => S("Die Datei ist in einem anderen Programm geöffnet oder der Datenträger ist voll.", "The file is open in another program or the disk is full.");
    public static string FailFileBusyNext => S("Schließe das andere Programm oder gib Speicherplatz frei und versuche es dann erneut.", "Close the other program or free up disk space, then try again.");
    public static string FailBadFormatFmt => S("Die Datei „{0}“ hat nicht das erwartete Format.", "The file \"{0}\" is not in the expected format.");
    public static string FailBadFormat => S("Die Datei hat nicht das erwartete Format.", "The file is not in the expected format.");
    public static string FailBadFormatNext => S("Wähle eine Datei, die mit IP-Scanner exportiert wurde.", "Choose a file that was exported by IP-Scanner.");
    public static string FailDatabase => S("Die Datenbank ist gesperrt oder beschädigt.", "The database is locked or damaged.");
    public static string FailDatabaseNext => S("Schließe andere IP-Scanner-Fenster und versuche es erneut.", "Close other IP-Scanner windows and try again.");
    public static string FailForeignKey => S("Die Daten wurden unter einem anderen Windows-Konto verschlüsselt.", "The data was encrypted under a different Windows account.");
    public static string FailForeignKeyNext => S("Öffne sie mit dem ursprünglichen Konto.", "Open it with the original account.");
    public static string FailUpdateSignature => S("Das Update wurde nicht installiert, weil die Signatur ungültig ist.", "The update was not installed because its signature is invalid.");
    public static string FailUpdateSignatureNext => S("Der Download könnte manipuliert sein; installiere ihn nicht von Hand.", "The download may have been tampered with; do not install it by hand.");
    public static string NoticeKeyUnavailable => S(
        "Deine gespeicherten Notizen und Einstellungen wurden unter einem anderen Windows-Konto verschlüsselt oder die Schlüsseldatei scanner.key fehlt. Sie bleiben unangetastet, bis du das Programm mit dem ursprünglichen Konto öffnest.",
        "Your saved notes and settings were encrypted under a different Windows account or the key file scanner.key is missing. They stay untouched until you open the program with the original account.");
    public static string NoticeDataRestored => S(
        "Eine beschädigte Datendatei wurde beiseitegelegt und die letzte Sicherung geladen.",
        "A damaged data file was moved aside and the last backup was loaded.");
    public static string NoticeDataUnreadable => S(
        "Eine beschädigte Datendatei ließ sich nicht lesen. Sie wurde beiseitegelegt und das Programm mit leeren Daten gestartet.",
        "A damaged data file could not be read. It was moved aside and the program started with empty data.");
    public static string NoticeDataNewer => S(
        "Einige gespeicherte Daten stammen von einer neueren IP-Scanner-Version. Sie wurden nicht angetastet und sind hier schreibgeschützt, Änderungen daran werden nicht gespeichert.",
        "Some saved data was written by a newer version of IP-Scanner. It was left untouched and is read-only here, so changes to it are not saved.");
    public static string NoticeProfileNotSaved => S(
        "Die Profildaten konnten nicht gespeichert werden, deshalb wurde nichts geändert. Prüfe, ob die Dateien scanner.key und settings.dat neben der Datenbank lesbar und beschreibbar sind.",
        "The profile data could not be saved, so nothing was changed. Check that scanner.key and settings.dat next to the database are readable and writable.");
    public static string ErrorDialogDetails => S("Details", "Details");
    public static string ErrorDialogCopyDetails => S("Details kopieren", "Copy details");
    public static string ErrorDialogCopied => S("Kopiert!", "Copied!");
    public static string ErrorDialogOpenLog => S("Protokoll öffnen", "Open log");
    public static string NoNetworks => S("Keine Netzwerke", "No networks");
    public static string NoPinnedIps => S("Keine angehefteten IPs", "No pinned IPs");
    public static string NoInternetHosts => S("Keine Internetziele", "No internet hosts");
    public static string TipAddEntry => S("Eintrag hinzufügen (auch mit Enter)", "Add entry (Enter works too)");
    public static string AddNetwork => S("Netzwerk hinzufügen", "Add network");
    public static string AddPinnedIp => S("Angeheftete IP hinzufügen", "Add pinned IP");
    // A pinned hostname target that could not be resolved this run.
    public static string PinnedCannotResolve => S("nicht auflösbar", "cannot resolve");
    public static string AddHost => S("Host hinzufügen", "Add host");
    public static string TabGraphs => S("Graphen", "Graphs");
    public static string EnableGraphs => S("Graphen anzeigen", "Show graphs");
    public static string GraphMaxTime => S("Max. Zeitspanne", "Max time span");
    public static string EnableNetworkGraphs => S("Netzwerk-Graphen anzeigen", "Show network graphs");
    public static string AutoGraphPinned => S(
        "Verlauf bei angehefteten Geräten automatisch öffnen",
        "Open the history automatically for pinned devices");
    public static string TipAutoGraphPinned => S(
        "Öffnet den Latenz-Verlauf eines angehefteten Geräts automatisch unter seiner Zeile.",
        "Automatically expands the latency history under each pinned device's row.");
    public static string TipEnableNetworkGraphs => S(
        "Kleiner Latenz-Verlauf unter der Übersicht jedes Netzwerks.",
        "Small latency history under each network overview.");
    public static string TipEnableGraphs => S(
        "Blendet die Latenz-Graphen in der Seitenleiste ein oder aus.",
        "Shows or hides the latency graphs in the sidebar.");
    public static string TipGraphMaxTime => S(
        "Sichtbare Zeitspanne der Graphen in Sekunden (10-300).",
        "Visible time span of the graphs in seconds (10-300).");
    // The network/internet graph zoom toggle - "1m" (one-minute buckets)
    // vs "30s" (thirty-second buckets), not a duration value of its own.
    public static string ZoomOneMinute => S("1 Min", "1m");
    public static string ZoomThirtySeconds => S("30 s", "30s");

    // ── Header / controls ──
    public static string Scan => S("▶ Scan", "▶ Scan");
    public static string Stop => S("■ Stopp", "■ Stop");
    public static string PingsLabel => S("Pings:", "Pings:");
    public static string SubnetTooltip => S(
        "Basis-IP, z.B. 192.168.1.0  (leer = automatisch). Eigene Maske: 10.0.0.0/22.  Nur IPv4. IPv6-Ziele über die Seitenleiste oder die Werkzeuge scannen.",
        "Base IP, e.g. 192.168.1.0  (empty = auto). Custom mask: 10.0.0.0/22  IPv4 only. Scan IPv6 targets from the sidebar or the tools.");
    public static string MaskTooltip => S(
        "Netzmaske: /24 (254 Hosts) bis /16 (256 Netzwerke, ~65.000 Hosts). Eigener Wert im IP-Feld.",
        "Mask: /24 (254 hosts) up to /16 (256 networks, ~65,000 hosts). Custom value in the IP field.");

    // ── Progress / legend ──
    public static string Devices => S("Geräte", "Devices");
    public static string Pings => S("Pings", "Pings");
    public static string Online => S("Online", "Online");
    public static string Offline => S("Offline", "Offline");
    public static string OnlineOffline => S("Online/Offline", "Online/Offline");
    public static string Success => S("Erfolg", "Success");
    public static string Fail => S("Fehlschlag", "Failed");
    public static string Skipped => S("Übersprungen", "Skipped");
    public static string PingCountSkippedTipFmt => S("Enthält {0} übersprungene Pings für Offline-Geräte", "Includes {0} pings skipped for offline devices");
    public static string PingCountSkippedTip(long n) => string.Format(PingCountSkippedTipFmt, n.ToString("N0"));

    // ── Table headers ──
    public static string IpAddress => S("IP-Adresse", "IP address");
    // Pinned-entry edit field label; the entry may be an address or a hostname.
    public static string IpOrHostname => S("IP-Adresse oder Hostname", "IP address or hostname");
    public static string Status => S("Status", "Status");
    public static string Group => S("Gruppe", "Group");
    public static string Hostname => S("Hostname", "Hostname");
    public static string Avg => S("Ø", "Avg");
    public static string Min => S("Min", "Min");
    public static string Max => S("Max", "Max");
    public static string Last => S("Letzter", "Last");
    public static string Progress => S("Fortschritt", "Progress");
    public static string Mac => S("MAC", "MAC");
    public static string Vendor => S("Hersteller", "Vendor");
    public static string Loss => S("Verlust", "Loss");
    public static string Jitter => S("Schwankung", "Jitter");
    public static string LossTip => S(
        "Anteil der Pings ohne Antwort, seit dem letzten Auftauchen des Geräts.",
        "Share of pings with no reply, since the device last appeared.");
    public static string JitterTip => S(
        "Mittlere Abweichung zwischen aufeinanderfolgenden Antwortzeiten - ein Maß für die Stabilität der Verbindung.",
        "Mean difference between consecutive response times - a measure of how stable the connection is.");

    // ── Device detail view + context menu ──
    public static string Services => S("Dienste", "Services");
    public static string NoServices => S("Keine offenen Dienste gefunden.", "No open services found.");
    public static string ServicesNotCheckedYet => S("Noch nicht geprüft.", "Not checked yet.");
    public static string ServicesOfflineText => S(
        "Gerät ist offline, keine Prüfung möglich.", "Device is offline, cannot check.");
    // Typo-tolerant search over the service list.
    public static string NoServicesMatchFilter => S(
        "Kein Dienst passt zur Suche.", "No service matches the search.");
    public static string ServiceSearchPlaceholder => S("Dienste durchsuchen …", "Search services …");
    public static string DeviceNote => S("Notiz", "Note");
    public static string DeviceNoteHint => S(
        "Mehrzeilige Notiz zu diesem Gerät. Wird beim Verlassen des Feldes gespeichert.",
        "Multi-line note about this device. Saved when you leave the field.");
    public static string NoteTooLongFmt => S(
        "Die Notiz ist zu lang ({0}/{1} Zeichen).",
        "The note is too long ({0}/{1} characters).");
    public static string ScanningPorts => S("Ports werden gescannt …", "Scanning ports …");
    public static string ScanAllPorts => S("Alle Ports prüfen", "Scan all ports");
    public static string TipScanAllPorts => S(
        "Prüft alle 65535 Ports statt der bekannten Liste. Dauert deutlich länger und wird von Firewalls/Netzwerküberwachung als Portscan erkannt.",
        "Checks all 65535 ports instead of the known list. Takes much longer and firewalls/network monitoring will flag it as a port scan.");
    public static string FullPortScanWarningTitle => S(
        "Vollständige Portprüfung - nur im eigenen Netz.",
        "Full port scan - your own network only.");
    public static string FullPortScanWarningBodyFmt => S(
        "Dieser Vorgang prüft {0} Ports und sieht für Überwachungssysteme wie ein Angriff aus. Führe ihn nur in Netzen aus, für die du zuständig bist, und nur zu Testzwecken. In fremden Netzen kann das rechtliche Folgen haben.",
        "This checks {0} ports and can look like an attack to monitoring systems. Run it only on networks you are responsible for, and only for testing. Scanning other networks may have legal consequences.");
    public static string FullPortScanWarningBody(int ports) => string.Format(FullPortScanWarningBodyFmt, ports);
    public static string FullPortScanOutsideNote => S(
        "Diese Adresse liegt außerhalb deiner Netze. Prüfe nur Systeme, die du prüfen darfst.",
        "This address is outside your networks. Only scan systems you are allowed to test.");
    public static string FullPortScanWarningConfirm => S("Verstanden, prüfen", "Understood, scan");
    public static string IntrusiveCheckWarningTitle => S(
        "Eindringliche Prüfung",
        "Intrusive check");
    public static string IntrusiveCheckWarningBody => S(
        "Eine DHCP-Discover-Anfrage wird an das ganze Netzsegment gesendet, nicht nur an das Zielgerät. Führe sie nur im eigenen Netz aus.",
        "A DHCP discover broadcast reaches the whole network segment, not just the target. Only run it on your own network.");
    public static string IntrusiveCheckWarningConfirm => S("Verstanden, ausführen", "Understood, run");
    public static string TipIntrusiveCheckConfirm => S(
        "Führt die eindringliche Prüfung jetzt aus.",
        "Runs the intrusive check now.");
    public static string ScanningAllPortsFmt => S("Prüfe alle Ports … {0}/{1}", "Scanning all ports … {0}/{1}");
    public static string ScanningSelectedPortsFmt => S("Prüfe Ports … {0}/{1} Geräte", "Checking ports … {0}/{1} devices");
    public static string PortResultFmt => S("Ergebnis von {0}", "Result from {0}");
    public static string PartialPortResultFmt => S("Teilergebnis von {0}", "Partial result from {0}");
    public static string PortResultLoadFailed => S(
        "Gespeichertes Ergebnis konnte nicht geladen werden. Prüfe den Datenbankzugriff und prüfe die Ports erneut.",
        "Saved result could not be loaded. Check database access and scan the ports again.");
    public static string ImportKeptValuesFmt => S("Eigener Wert beibehalten für: {0}", "Kept your current value for: {0}");
    public static string ImportKeptValues(string keys) => string.Format(ImportKeptValuesFmt, keys);
    public static string PortCheckFailed => S("Die Portprüfung ist fehlgeschlagen.", "The port check failed.");
    public static string ManualExportCheckPorts => S(
        "Ports noch nicht geprüfter Geräte prüfen (dauert länger)",
        "Check ports of devices not checked yet (takes longer)");
    public static string PortResultSaveFailed => S(
        "Portergebnis konnte nicht gespeichert werden. Prüfe den Datenbankzugriff und prüfe die Ports erneut.",
        "Port result could not be saved. Check database access and scan the ports again.");
    // Found-count + ETA next to the full-port-scan progress bar.
    public static string FullPortScanFoundFmt => S("{0} gefunden", "{0} found");
    public static string EtaSecondsFmt => S("noch ~{0} s", "~{0} s left");
    public static string EtaMinutesFmt => S("noch ~{0} Min", "~{0} min left");
    public static string EtaHoursFmt => S("noch ~{0} Std", "~{0} h left");
    // Main table progress bar ETA (minutes + seconds, distinct wording
    // from the single-unit port-scan ETA above).
    public static string ProgressEtaMinSecFmt => S("noch ca. {0} min {1} s", "about {0} min {1} s left");
    public static string ProgressEtaSecFmt => S("noch ca. {0} s", "about {0} s left");
    public static string CopyIp => S("IP kopieren", "Copy IP");
    public static string CopyMac => S("MAC kopieren", "Copy MAC");
    public static string CopyHostname => S("Hostname kopieren", "Copy hostname");
    // On-demand hostname re-resolution + opt-out for a device.
    public static string ResolveHostnameAgain => S("Namen neu auflösen", "Resolve name again");
    public static string StopHostnameRetry => S("Nicht mehr nach Namen suchen", "Stop looking up this name");
    // Mute a routinely-offline device (e.g. a printer switched off at night).
    public static string MuteDevice => S("Gerät stummschalten", "Mute device");
    // Accept a rogue (unknown) device into the known-devices database.
    public static string MarkAsKnown => S("Als bekannt markieren", "Mark as known");

    // IP conflicts and MAC changes, sidebar "Funde" section + detail-view hint.
    public static string FindingsSectionHeaderFmt => S("Funde ({0})", "Findings ({0})");
    public static string FindingsSectionHeaderText(int count) => string.Format(FindingsSectionHeaderFmt, count);
    public static string TipFindingsSection => S(
        "IP-/MAC-Auffälligkeiten dieses Durchlaufs. Geräte mit einer zufälligen (lokal verwalteten) MAC werden hier absichtlich nicht gemeldet, da sie ihre MAC routinemäßig wechseln.",
        "IP/MAC anomalies from this run. Devices with a randomized (locally administered) MAC are deliberately not reported here since they change their MAC routinely.");
    public static string ConflictMacChangedFmt => S("MAC an {0} geändert: {1} → {2}", "MAC changed at {0}: {1} → {2}");
    public static string ConflictMacChangedMsg(string ip, string oldMac, string newMac) => string.Format(ConflictMacChangedFmt, ip, oldMac, newMac);
    public static string ConflictMacOnMultipleIpsFmt => S("MAC {0} auch bei {1}", "MAC {0} also seen at {1}");
    public static string ConflictMacOnMultipleIpsMsg(string mac, string otherIps) => string.Format(ConflictMacOnMultipleIpsFmt, mac, otherIps);
    public static string ConflictMultipleMacsFmt => S("Mehrere MAC-Adressen bei {0}", "Multiple MAC addresses at {0}");
    public static string ConflictMultipleMacsMsg(string ip) => string.Format(ConflictMultipleMacsFmt, ip);
    public static string WakeDevice => S("Aufwecken (WoL)", "Wake (WoL)");
    public static string WakeTooltip => S(
        "Sendet ein Magic Packet. Funktioniert nur innerhalb desselben Netzwerksegments.",
        "Sends a magic packet. Only works within the same network segment.");
    public static string WakeNoMacTooltip => WakeNoMacMsg;
    public static string WakeFailedMsg => S(
        "Das Aufweck-Paket konnte nicht gesendet werden. Eine Firewall oder ein getrennter Netzwerkadapter kann das verhindern.",
        "The wake-up packet could not be sent. A firewall or a disconnected network adapter can block it.");
    public static string WakeNoMacMsg => S(
        "Für dieses Gerät ist keine MAC-Adresse bekannt. Scanne es einmal, während es eingeschaltet ist.",
        "No MAC address is known for this device. Scan it once while it is switched on.");
    public static string WakeSentFmt => S(
        "Magic Packet an {0} gesendet. Ob das Gerät aufwacht, hängt von dessen WoL-Unterstützung ab.",
        "Magic packet sent to {0}. Whether the device wakes up depends on its own WoL support.");
    public static string WakeSentMsg(string ip) => string.Format(WakeSentFmt, ip);

    // ── Wake-on-LAN tool ──
    public static string WakeMacLabel => S("MAC-Adresse", "MAC address");
    public static string WakeMacHint => S("z. B. AA:BB:CC:DD:EE:FF", "e.g. AA:BB:CC:DD:EE:FF");
    public static string WakeFromKnownDevices => S("Aus bekannten Geräten", "From known devices");
    public static string WakeNetworkCard => S("Netzwerkkarte", "Network card");
    public static string WakeKnownDevices => S("Bekannte Geräte", "Known devices");
    public static string ScanToolSavePathLabel => S("Speicherpfad", "Save path");
    public static string WakeSendButton => S("Aufwecken", "Wake up");
    public static string WakeStopButton => S("Abbrechen", "Stop");
    public static string WakeRecentLabel => S("Zuletzt verwendet", "Recently used");
    public static string WakeSendingStatus => S("Sende Magic Packet …", "Sending magic packet …");
    public static string WakeWaitingFmt => S("Warte auf Antwort … {0}", "Waiting for a reply … {0}");
    public static string WakeWaitingMsg(string countdown) => string.Format(WakeWaitingFmt, countdown);
    public static string WakeSentNoIpMsg => S(
        "Magic Packet gesendet. Keine IP-Adresse bekannt, daher keine automatische Prüfung.",
        "Magic packet sent. No IP address known, so no automatic check.");
    public static string WakeDeviceBackMsg => S("Gerät ist wieder erreichbar.", "Device is reachable again.");
    public static string WakeNoAnswerMsg => S(
        "Keine Antwort innerhalb von 2 Minuten.", "No answer within 2 minutes.");
    public static string WakeStoppedMsg => S("Abgebrochen.", "Stopped.");

    // ── Port check tool ──
    public static string PortCheckTargetLabel => S("Ziel", "Target");
    public static string PortCheckTargetHint => S("IP-Adresse oder Name", "IP address or hostname");
    public static string PortCheckResolvedFmt => S("Aufgelöst: {0}", "Resolved: {0}");
    public static string PortCheckResolvedMsg(string ip) => string.Format(PortCheckResolvedFmt, ip);
    public static string PortCheckResolveFailed => S(
        "Ziel konnte nicht aufgelöst werden.", "Could not resolve the target.");
    public static string PortCheckOutsideSubnet => S(
        "Ziel liegt außerhalb deiner Netze", "Target is outside your networks");
    public static string PortCheckScopeCommon => S("Übliche Dienste", "Common services");
    public static string PortCheckScopeTop1000 => S("Top 1000", "Top 1000");
    public static string PortCheckScanType => S("Scanart", "Scan type");
    public static string PortCheckShowAll => S("Alle Zustände zeigen", "Show all states");
    public static string PortCheckColState => S("Zustand", "State");
    public static string PortCheckColReason => S("Grund", "Reason");
    public static string PortStateOpen => S("Port offen", "Port open");
    public static string PortStateClosed => S("Port geschlossen", "Port closed");
    public static string PortStateFiltered => S("Gefiltert", "Filtered");
    public static string PortStateOpenFiltered => S("Offen oder gefiltert", "Open or filtered");
    public static string PortStateUnfiltered => S("Ungefiltert", "Unfiltered");
    public static string PortReasonSynAck => S("SYN/ACK empfangen", "SYN/ACK received");
    public static string PortReasonRst => S("RST empfangen", "RST received");
    public static string PortReasonRstWindow => S("RST mit offenem Fenster", "RST with open window");
    public static string PortReasonNoResponse => S("Keine Antwort", "No response");
    public static string PortReasonIcmp => S("ICMP nicht erreichbar", "ICMP unreachable");
    public static string PortReasonUdpReply => S("UDP-Antwort", "UDP reply");
    public static string PortReasonRefused => S("Verbindung abgelehnt", "Connection refused");
    public static string PortReasonConnected => S("Verbunden", "Connected");
    public static string PortDriverMissing => S("Paketstreiber nicht installiert", "Packet driver not installed");
    public static string PortTransportMissing => S(
        "Paketstreiber noch nicht mit dem Scanner verbunden",
        "Packet driver not connected to the scanner yet");
    public static string PortScanTypeUnavailableFmt => S("Nicht verfügbar: {0}", "Not available: {0}");
    public static string PortRawScanFailedFmt => S("Scan abgebrochen, der Paketstreiber meldet einen Fehler: {0}", "Scan stopped, the packet driver reported an error: {0}");
    public static string TipPortScanType => S(
        "Wie die TCP-Ports geprüft werden. Connect funktioniert überall. Die anderen Arten senden Rohpakete und brauchen den Paketstreiber, ohne ihn sind sie ausgegraut.",
        "How the TCP ports are probed. Connect works everywhere. The other types send raw packets and need the packet driver; without it they stay greyed out.");
    public static string TipPortShowAll => S(
        "Zeigt auch geschlossene, gefilterte und ungefilterte Ports mit dem Grund für jeden Zustand, nicht nur die offenen.",
        "Also lists closed, filtered and unfiltered ports with the reason for each state, not only the open ones.");
    public static string TipScanConnect => S(
        "Vollständiger TCP-Verbindungsaufbau über das System. Funktioniert überall und liest Dienst-Banner.",
        "Full TCP handshake through the system. Works everywhere and reads service banners.");
    public static string TipScanSyn => S(
        "Halboffener Scan: sendet nur das erste Paket und baut die Verbindung nie fertig auf. Schnell und unauffällig. Braucht den Paketstreiber.",
        "Half-open scan: sends only the first packet and never completes the connection. Fast and quiet. Needs the packet driver.");
    public static string TipScanAck => S(
        "Sendet ACK-Pakete, um Firewall-Regeln abzubilden. Zeigt, ob ein Port gefiltert ist, nicht ob er offen ist. Braucht den Paketstreiber.",
        "Sends ACK packets to map firewall rules. Shows whether a port is filtered, not whether it is open. Needs the packet driver.");
    public static string TipScanWindow => S(
        "Wie ACK, liest aber die Fenstergröße der Antwort, die auf manchen Systemen offene Ports verrät. Braucht den Paketstreiber.",
        "Like ACK, but reads the window size of the reply, which reveals open ports on some systems. Needs the packet driver.");
    public static string TipScanFin => S(
        "Sendet FIN-Pakete. Geschlossene Ports antworten, offene schweigen. Einbruchserkennung wertet das als Angriff. Braucht den Paketstreiber.",
        "Sends FIN packets. Closed ports answer, open ones stay silent. Intrusion detection flags this as an attack. Needs the packet driver.");
    public static string TipScanNull => S(
        "Sendet Pakete ohne gesetzte Flags. Geschlossene Ports antworten, offene schweigen. Einbruchserkennung wertet das als Angriff. Braucht den Paketstreiber.",
        "Sends packets with no flags set. Closed ports answer, open ones stay silent. Intrusion detection flags this as an attack. Needs the packet driver.");
    public static string TipScanXmas => S(
        "Sendet Pakete mit den Flags FIN, PSH und URG. Geschlossene Ports antworten, offene schweigen. Einbruchserkennung wertet das als Angriff. Braucht den Paketstreiber.",
        "Sends packets with the FIN, PSH and URG flags set. Closed ports answer, open ones stay silent. Intrusion detection flags this as an attack. Needs the packet driver.");
    public static string TipScanMaimon => S(
        "Sendet FIN/ACK-Pakete. Manche Systeme antworten nur bei geschlossenem Port. Einbruchserkennung wertet das als Angriff. Braucht den Paketstreiber.",
        "Sends FIN/ACK packets. Some systems answer only when the port is closed. Intrusion detection flags this as an attack. Needs the packet driver.");
    public static string PortCheckScopeRange => S("Bereich", "Range");
    public static string PortCheckScopeList => S("Liste", "List");
    public static string PortCheckRangeFromLabel => S("Von", "From");
    public static string PortCheckRangeToLabel => S("Bis", "To");
    public static string PortCheckListHint => S("z. B. 22, 80, 443", "e.g. 22, 80, 443");
    public static string PortCheckStartButton => S("Prüfen", "Check");
    public static string PortCheckCancelButton => S("Abbrechen", "Cancel");
    public static string PortCheckCopyButton => S("Ports kopieren", "Copy ports");
    public static string PortCheckProgressFmt => S(
        "{0} von {1} geprüft, {2} gefunden", "{0} of {1} checked, {2} found");
    public static string PortCheckProgressMsg(int done, int total, int found) => string.Format(PortCheckProgressFmt, done, total, found);
    public static string PortCheckNoResults => S(
        "Keine offenen Ports gefunden.", "No open ports found.");
    public static string PortCheckInvalidRange => S(
        "Ungültiger Bereich (1-65535, von ≤ bis).", "Invalid range (1-65535, from ≤ to).");
    public static string PortCheckInvalidList => S("Ungültige Liste.", "Invalid list.");

    public static string PortCheckServiceChecksHeader => S("Dienstprüfungen", "Service checks");
    public static string PortCheckCheckSafe => S("Sicher", "Safe");
    public static string TipCheckSafe => S(
        "Harmlose Prüfungen wie HTTP-Titel oder TLS-Zertifikat, die den Dienst nur normal abfragen.",
        "Harmless checks like HTTP title or TLS certificate that only query the service the normal way.");
    public static string PortCheckCheckDiscovery => S("Erkennung", "Discovery");
    public static string TipCheckDiscovery => S(
        "Prüfungen, die Geräte- oder Dienstinformationen abfragen, etwa SNMP oder UPnP.",
        "Checks that query device or service information, such as SNMP or UPnP.");
    public static string PortCheckCheckIntrusive => S("Eindringlich", "Intrusive");
    public static string TipCheckIntrusive => S(
        "Prüfungen, die über das Zielgerät hinausgehen, etwa ein DHCP-Discover ans ganze Netzsegment. Erfordert eine Bestätigung.",
        "Checks that reach beyond the target device, such as a DHCP discover to the whole network segment. Needs confirmation.");
    public static string PortCheckRunChecksButton => S("Prüfungen starten", "Run checks");
    public static string TipRunChecks => S(
        "Führt die ausgewählten Dienstprüfungen für die offenen Ports aus.",
        "Runs the selected service checks against the open ports.");
    public static string PortCheckStopChecksButton => S("Stoppen", "Stop");
    public static string TipStopChecks => S(
        "Bricht die laufenden Dienstprüfungen ab.",
        "Cancels the service checks that are running.");
    public static string PortCheckColCheck => S("Prüfung", "Check");
    public static string PortCheckColFinding => S("Befund", "Finding");
    public static string PortCheckChecksRunningFmt => S("Prüfe … {0}/{1}", "Checking … {0}/{1}");
    public static string PortCheckChecksRunning(int done, int total) => string.Format(PortCheckChecksRunningFmt, done, total);
    public static string PortCheckChecksDone => S("Fertig.", "Done.");
    public static string PortCheckChecksNoFindings => S("Keine Befunde.", "No findings.");

    public static string CheckNameHttpTitle => S("HTTP-Titel", "HTTP title");
    public static string CheckNameHttpHeaders => S("HTTP-Sicherheitsheader", "HTTP security headers");
    public static string CheckNameTlsCert => S("TLS-Zertifikat", "TLS certificate");
    public static string CheckNameSnmpSysDescr => S("SNMP-Systembeschreibung", "SNMP system description");
    public static string CheckNameUpnpDevice => S("UPnP-Gerät", "UPnP device");
    public static string CheckNameMdnsServices => S("mDNS-Dienste", "mDNS services");
    public static string CheckNameNetBiosNames => S("NetBIOS-Namen", "NetBIOS names");
    public static string CheckNameDhcpDiscover => S("DHCP-Discover", "DHCP discover");

    public static string CheckOutcomeNothingFound => S("Nichts gefunden", "Nothing found");
    public static string CheckOutcomeNoAnswer => S("Keine Antwort", "No answer");

    public static string FindingTitle => S("Titel", "Title");
    public static string FindingServer => S("Server", "Server");
    public static string FindingPoweredBy => S("Betrieben mit", "Powered by");
    public static string FindingMissingHeader => S("Fehlender Header", "Missing header");
    public static string FindingAllHeadersSet => S("Alle Sicherheitsheader gesetzt", "All security headers set");
    public static string FindingProtocol => S("Protokoll", "Protocol");
    public static string FindingSubject => S("Antragsteller", "Subject");
    public static string FindingIssuer => S("Aussteller", "Issuer");
    public static string FindingValidTo => S("Gültig bis", "Valid to");
    public static string FindingCertExpired => S("Zertifikat abgelaufen", "Certificate expired");
    public static string FindingSysDescr => S("Systembeschreibung", "System description");
    public static string FindingUpnpLocation => S("UPnP-Adresse", "UPnP location");
    public static string FindingFriendlyName => S("Anzeigename", "Friendly name");
    public static string FindingManufacturer => S("Hersteller", "Manufacturer");
    public static string FindingModelName => S("Modell", "Model name");
    public static string FindingModelNumber => S("Modellnummer", "Model number");
    public static string FindingMdnsService => S("mDNS-Dienst", "mDNS service");
    public static string FindingNetbiosName => S("NetBIOS-Name", "NetBIOS name");
    public static string FindingDhcpServer => S("DHCP-Server", "DHCP server");
    public static string FindingDhcpRouter => S("Router", "Router");
    public static string FindingDhcpDns => S("DNS-Server", "DNS server");
    public static string FindingDhcpLease => S("Lease-Dauer (Sekunden)", "Lease time (seconds)");

    public static string DnsTargetLabel => S("Ziel", "Target");
    public static string DnsTargetHint => S("Adresse oder Name", "Address or name");
    public static string DnsModeAuto => S("Automatisch", "Automatic");
    public static string DnsModeForward => S("Name → Adresse", "Name → address");
    public static string DnsModeReverse => S("Adresse → Name", "Address → name");
    public static string DnsStartButton => S("Auflösen", "Resolve");
    public static string DnsAddressesLabel => S("Adressen", "Addresses");
    public static string DnsCanonicalLabel => S("Name", "Name");
    public static string DnsAliasesLabel => S("Weitere Namen", "Aliases");
    public static string DnsServersLabel => S("Zuständige Namensserver", "Configured DNS servers");
    public static string DnsNotFound => S("Name nicht gefunden.", "Name not found.");
    public static string DnsTimeout => S("Zeitüberschreitung bei der Anfrage.", "The lookup timed out.");
    public static string DnsCopyButton => S("Kopieren", "Copy");

    public static string PingRoutePingTab => S("Dauerping", "Ping");
    public static string PingRouteRouteTab => S("Weg zum Ziel", "Route");
    public static string PingIntervalLabel => S("Intervall", "Interval");
    public static string PingPayloadLabel => S("Paketgröße", "Payload size");
    public static string PingTimeoutLabel => S("Zeitlimit (ms)", "Timeout (ms)");
    public static string PingInvalidTimeout => S(
        "Ungültiges Zeitlimit (100-10000 ms).", "Invalid timeout (100-10000 ms).");
    public static string PingStartButton => S("Starten", "Start");
    public static string PingSentLabel => S("Gesendet", "Sent");
    public static string PingReceivedLabel => S("Empfangen", "Received");
    public static string PingLossLabel => S("Verlust", "Loss");
    public static string PingStatsFmt => S(
        "Min {0} / Ø {1} / Max {2} / Letzter {3} ms", "Min {0} / avg {1} / max {2} / last {3} ms");
    public static string PingStatsMsg(string min, string avg, string max, string last)
        => string.Format(PingStatsFmt, min, avg, max, last);
    public static string RouteHopLabel => S("Station", "Hop");
    public static string RouteAddressLabel => S("Adresse", "Address");
    public static string RouteNameLabel => S("Name", "Name");
    public static string RoutePingLabel => S("Ping", "Ping");
    public static string PingCopyButton => S("Kopieren", "Copy");
    public static string RouteCopyButton => S("Kopieren", "Copy");

    public static string CalcSubnetTitle => S("Netzrechner", "Subnet calculator");
    public static string CalcMaskHint => S("Maske oder /Präfix", "Mask or /prefix");
    public static string CalcInvalidSubnet => S("Ungültige Adresse oder Maske.", "Invalid address or mask.");
    public static string CalcNetworkLabel => S("Netz", "Network");
    public static string CalcBroadcastLabel => S("Rundsendeadresse", "Broadcast");
    public static string CalcFirstHostLabel => S("Erster Rechner", "First host");
    public static string CalcLastHostLabel => S("Letzter Rechner", "Last host");
    public static string CalcUsableHostsLabel => S("Nutzbare Rechner", "Usable hosts");
    public static string CalcWildcardLabel => S("Wildcard-Maske", "Wildcard mask");
    public static string CalcCidrLabel => S("Präfix", "Prefix");
    public static string CalcLastAddressLabel => S("Letzte Adresse", "Last address");
    public static string CalcAddressCountLabel => S("Adressen", "Addresses");
    public static string CalcContainsLabel => S("Enthält", "Contains");
    public static string CalcContainsHint => S("Adresse prüfen, ob sie im Netz liegt", "Address to test against the network");
    public static string CalcContainsYes => S("Ja, die Adresse liegt im Netz.", "Yes, the address is inside the network.");
    public static string CalcContainsNo => S("Nein, die Adresse liegt außerhalb.", "No, the address is outside the network.");
    public static string CalcContainsInvalid => S("Keine gültige Adresse.", "Not a valid address.");

    public static string CalcVendorTitle => S("Herstellersuche", "Vendor lookup");
    public static string CalcMacHint => S("MAC oder erste drei Byte", "MAC or first three bytes");
    public static string CalcVendorNotFound => S("Kein Treffer.", "No match.");
    public static string CalcVendorFooterFmt => S(
        "{0} Einträge, Stand {1}", "{0} entries, as of {1}");
    public static string CalcVendorFooterMsg(int count, string date) => string.Format(CalcVendorFooterFmt, count, date);
    public static string CalcVendorFooterNoDateFmt => S("{0} Einträge", "{0} entries");
    public static string CalcVendorFooterNoDateMsg(int count) => string.Format(CalcVendorFooterNoDateFmt, count);

    public static string CalcArpTitle => S("Adresstabelle", "Address table");
    public static string CalcArpLocalOnly => S("nur im eigenen Netz", "local network only");
    public static string CalcArpNotFound => S("Keine MAC-Adresse gefunden.", "No MAC address found.");
    public static string CalcLookupButton => S("Nachschlagen", "Look up");

    public static string Port => S("Port", "Port");
    public static string Service => S("Dienst", "Service");
    public static string CertInvalidPort => S("Ungültiger Port (1-65535).", "Invalid port (1-65535).");
    public static string CertConnectFailed => S("Verbindung fehlgeschlagen oder Zeitüberschreitung.", "Connection failed or timed out.");
    public static string CertSubjectLabel => S("Inhaber", "Subject");
    public static string CertIssuerLabel => S("Aussteller", "Issuer");
    public static string CertValidFromLabel => S("Gültig ab", "Valid from");
    public static string CertValidToLabel => S("Gültig bis", "Valid to");
    public static string CertDaysRemainingFmt => S("{0} Tage verbleibend", "{0} days remaining");
    public static string CertDaysRemainingMsg(int days) => string.Format(CertDaysRemainingFmt, days);
    public static string CertExpiredFmt => S("Seit {0} Tagen abgelaufen", "Expired {0} days ago");
    public static string CertExpiredMsg(int days) => string.Format(CertExpiredFmt, days);
    public static string CertSigAlgLabel => S("Signaturalgorithmus", "Signature algorithm");
    public static string CertKeySizeLabel => S("Schlüssellänge", "Key size");
    public static string CertFingerprintLabel => S("SHA-256-Fingerabdruck", "SHA-256 fingerprint");
    public static string CertSanLabel => S("Alternative Namen", "Subject alternative names");
    public static string CertChainLabel => S("Zertifikatskette", "Certificate chain");
    public static string CertChainValid => S("Kette gültig, keine Fehler.", "Chain is valid, no errors.");
    public static string CertChainUntrusted => S(
        "Die Kette konnte nicht geprüft werden (z. B. unbekannte oder selbstsignierte Stelle).",
        "The chain could not be verified (e.g. an unknown or self-signed authority).");
    public static string CertChainNameMismatch => S(
        "Der Name im Zertifikat passt nicht zum Ziel.", "The certificate's name does not match the target.");
    public static string CertChainNoCertificate => S("Kein Zertifikat wurde vorgelegt.", "No certificate was presented.");

    public static string FilterPlaceholder => S(
        "Suche - z. B. ip:192.168.1 oder port:443", "Search - e.g. ip:192.168.1 or port:443");
    // Quick filter pills next to the search box.
    public static string FilterOnlineOnly => S("Nur online", "Online only");
    public static string FilterPinnedOnly => S("Nur angeheftet", "Pinned only");
    public static string FilterUnknownOnly => S("Nur unbekannt", "Unknown only");
    public static string TrayOpen => S("Öffnen", "Open");
    public static string MinimizeToTray => S("In den Infobereich minimieren", "Minimize to system tray");
    public static string TipMinimizeToTray => S(
        "Beim Minimieren in den Infobereich (Tray) statt in die Taskleiste. Doppelklick auf das Symbol öffnet wieder.",
        "When minimized, hide to the system tray instead of the taskbar. Double-click the icon to reopen.");
    public static string CheckForUpdatesLabel => S("Beim Start auf Updates prüfen", "Check for updates on startup");
    public static string TipCheckForUpdates => S(
        "Fragt beim Programmstart GitHub nach einer neueren Version.",
        "Asks GitHub for a newer release when the app starts.");

    // ── Sidebar ──
    public static string Network => S("Netzwerk", "Network");
    public static string OwnIp => S("Eigene IP", "Own IP");
    public static string Ipv6KindGlobal => S("Global", "Global");
    public static string Ipv6KindUla => S("ULA", "ULA");
    public static string Ipv6KindLinkLocal => S("Link-lokal", "Link-local");
    public static string TipIpv6KindGlobal => S("Globale Adresse, aus dem Internet erreichbar", "Global address, reachable from the internet");
    public static string TipIpv6KindUla => S("Private Adresse nur in deinem Netz (Unique Local)", "Private address inside your network only (unique local)");
    public static string TipIpv6KindLinkLocal => S("Nur auf dieser Netzverbindung gültig, wird nicht weitergeleitet", "Valid on this link only, never routed");
    public static string Gateway => S("Gateway", "Gateway");
    public static string Mask => S("Maske", "Mask");
    public static string Dns => S("DNS", "DNS");
    public static string Interface => S("Interface", "Interface");
    public static string NotScannedYet => S("Noch nicht gescannt", "Not scanned yet");
    public static string ScanNowBtn => S("Jetzt scannen", "Scan now");
    public static string AvgLatency => S("Ø Latenz", "Avg latency");
    public static string InternetLatency => S("INTERNET-LATENZ", "INTERNET LATENCY");

    public static string TotalRow => S("Ø Gesamt", "Ø Total");
    public static string SelectedCountFmt => S("{0} ausgewählt", "{0} selected");
    public static string SelectedCount(int n) => string.Format(SelectedCountFmt, n);
    public static string PinSelected => S("Auswahl anheften", "Pin selection");
    // Permanent "last scan finished at" timestamp next to the totals row.
    public static string LastScanFmt => S("Letzter Scan: {0}", "Last scan: {0}");
    public static string ShowOfflineDb => S("Bekannte offline zeigen", "Show known offline");
    public static string TipShowOfflineDb => S(
        "Aus der Datenbank bekannte, aktuell offline Geräte ein- oder ausblenden.",
        "Show or hide known devices from the database that are currently offline.");

    // ── Settings ──
    public static string Settings => S("Einstellungen", "Settings");
    public static string ConfigMergeWarning => S(
        "Die Einstellungsdatei lässt sich nicht sicher zusammenführen. Überschreiben (deine Kommentare gehen verloren) oder abbrechen?",
        "The settings file cannot be merged safely. Overwrite it (your comments will be lost) or cancel?");
    public static string ConfigMergeOverwrite => S("Überschreiben", "Overwrite");
    public static string Close => S("Schließen", "Close");
    public static string OpenInBrowser => S("Im Browser öffnen: {0}", "Open in browser: {0}");
    public static string OpenWithApp => S("Mit dem passenden Programm öffnen: {0}", "Open with the matching app: {0}");
    public static string ServiceProbableHint => S(
        "UDP-Antwort erhalten - offen ist wahrscheinlich, aber nicht bewiesen.",
        "UDP answered - open is likely but not proven.");
    public static string ServiceNetworkAnsweredHint => S(
        "Vom Netzwerk beantwortet, nicht zwingend vom Gerät.",
        "Answered by the network, not necessarily by the device.");
    public static string Minimize => S("Minimieren", "Minimize");
    public static string Maximize => S("Maximieren", "Maximize");
    public static string Restore => S("Wiederherstellen", "Restore");

    // ── Tools window ──
    public static string Tools => S("Werkzeuge", "Tools");
    public static string ArchiveTitle => S("Scan-Archiv", "Scan archive");
    public static string ArchiveFrom => S("Von", "From");
    public static string ArchiveTo => S("Bis", "To");
    public static string ArchiveApplyRange => S("Zeitraum anzeigen", "Show range");
    public static string ArchiveClearRange => S("Alle", "All");
    public static string ArchiveRuns => S("Scanläufe", "Scan runs");
    public static string ArchiveDate => S("Datum", "Date");
    public static string ArchiveDuration => S("Dauer", "Duration");
    public static string ArchiveDetails => S("Laufdetails", "Run details");
    public static string ArchiveDevices => S("Geräte im Lauf", "Devices in run");
    public static string ArchiveDeviceHistory => S("Geräteverlauf im Zeitraum", "Device history in range");
    public static string ArchiveAvailability => S("Verfügbarkeit", "Availability");
    public static string ArchiveLatencyTrend => S("Ø Latenz (ms)", "Avg latency (ms)");
    public static string ArchiveFirstSeen => S("Zuerst gesehen", "First seen");
    public static string ArchiveLastSeen => S("Zuletzt gesehen", "Last seen");
    public static string ArchiveDeleteRun => S("Lauf löschen", "Delete run");
    public static string ArchiveDeleteRange => S("Zeitraum löschen", "Delete range");
    public static string ArchiveOlderThanDays => S("Tage alt", "days old");
    public static string ArchiveCompact => S("Verdichten", "Compact");
    public static string ArchivePurge => S("Bereinigen", "Purge");
    public static string ArchiveImport => S("Archiv importieren", "Import archive");
    public static string ArchiveBackup => S("Sicherung erstellen", "Create backup");
    public static string PickDate => S("Datum wählen", "Pick a date");
    public static string ArchiveConfirmTitle => S("Archiv ändern?", "Change archive?");
    public static string ArchiveLoading => S("Archiv wird geladen …", "Loading archive …");
    public static string ArchiveWorking => S("Wird bearbeitet …", "Working …");
    public static string BusyExportingDevices => S("Geräte werden exportiert …", "Exporting devices …");
    public static string BusyImportingDevices => S("Geräte werden eingelesen …", "Importing devices …");
    public static string ArchiveKeyUnavailable => S(
        "Das Archiv wurde unter einem anderen Windows-Konto erstellt oder die Schlüsseldatei scanner.key fehlt. Öffne es mit dem ursprünglichen Konto.",
        "The archive was created under a different Windows account or the key file scanner.key is missing. Open it with the original account.");
    public static string ArchiveCompactionLabel => S("Scan-Archiv verdichten nach", "Compact scan archive after");
    public static string TipArchiveCompaction => S(
        "Läufe, die älter sind, werden nach jedem Scan zu Tageswerten zusammengefasst.",
        "Runs older than this are merged into daily summaries after each scan.");
    public static string ArchiveBackupRetentionLabel => S("Automatische Archivsicherungen aufbewahren", "Keep automatic archive backups");
    public static string TipArchiveBackupRetention => S(
        "Ältere automatische Sicherungen werden nach jedem Scan gelöscht.",
        "Older automatic backups are deleted after each scan.");
    public static string CompareButton => S("Läufe vergleichen", "Compare runs");
    public static string CompareBack => S("Zurück zur Übersicht", "Back to overview");
    public static string CompareRunA => S("Lauf A (Ausgangslage)", "Run A (baseline)");
    public static string CompareRunB => S("Lauf B (neuer)", "Run B (newer)");
    public static string CompareLatencyPercent => S("Latenz-Änderung über (%)", "Latency change over (%)");
    public static string CompareLatencyMinMs => S("und mindestens (ms)", "and at least (ms)");
    public static string CompareChange => S("Änderung", "Change");
    public static string CompareBefore => S("Vorher", "Before");
    public static string CompareAfter => S("Nachher", "After");
    public static string CompareTypeNew => S("Neu", "New");
    public static string CompareTypeGone => S("Verschwunden", "Gone");
    public static string CompareTypeIpChanged => S("IP-Adresse geändert", "IP address changed");
    public static string CompareTypeMacChanged => S("MAC geändert", "MAC changed");
    public static string CompareTypePortsOpened => S("Ports neu offen", "Ports newly open");
    public static string CompareTypePortsClosed => S("Ports neu geschlossen", "Ports newly closed");
    public static string CompareTypeLatencyChanged => S("Latenz geändert", "Latency changed");
    public static string CompareSummaryFmt => S("{0} Änderungen", "{0} changes");
    public static string CompareNeedTwoRunsFmt => S("Zum Vergleichen sind mindestens zwei Scanläufe nötig. Im Archiv sind bisher {0}.", "Comparing needs at least two scan runs. The archive holds {0} so far.");
    public static string CompareNoChanges => S("Zwischen den beiden Läufen gibt es keine Unterschiede.", "There are no differences between the two runs.");
    public static string CompareThresholdInvalid => S("Bitte für die Schwellen eine Zahl ab 0 eingeben.", "Enter a number of 0 or more for the thresholds.");
    public static string ComparePortsHint => S("Ports werden nur verglichen, wenn beide Läufe Portdaten enthalten (Einstellung: Offene Ports mit exportieren, bei eingeschaltetem TXT-, CSV- oder HTML-Bericht).", "Ports are only compared when both runs hold port data (setting: Include open ports in exports, with a TXT, CSV or HTML report switched on).");
    public static string CompareCopy => S("Kopieren", "Copy");
    public static string CompareSaveJson => S("Als JSON speichern", "Save as JSON");
    public static string CompareSaveText => S("Als Text speichern", "Save as text");
    public static string CompareSaved => S("Vergleich gespeichert.", "Comparison saved.");
    public static string ArchiveBackupSavedFmt => S("Sicherung gespeichert in {0}", "Backup saved to {0}");
    public static string ArchiveBackupNothing => S("Keine Läufe vorhanden, nichts zu sichern.", "No runs stored, nothing to back up.");
    public static string ArchiveInvalidRange => S("Das Enddatum muss nach dem Startdatum liegen.", "The end date must be after the start date.");
    public static string ArchiveSelectRun => S("Wähle einen Scanlauf aus.", "Select a scan run.");
    public static string ArchiveTabHistory => S("Verlauf", "History");
    public static string ArchiveNoDevices => S("Dieser Lauf enthält keine gespeicherten Geräte.", "This run has no stored devices.");
    public static string ArchiveTrendTitle => S("Gewähltes Gerät, Lauf für Lauf", "Selected device, run by run");
    public static string ArchiveTrendHint => S("Wähle oben ein Gerät, um sein Ergebnis in jedem Lauf zu sehen.", "Select a device above to see how it did in each run.");
    public static string ArchiveEmpty => S("Noch keine Scanläufe in diesem Zeitraum. Starte einen Scan oder wähle einen anderen Zeitraum.", "No scan runs in this range yet. Start a scan or choose another range.");
    public static string ArchiveRangeSummary => S("{0} Läufe, {1} Scan-Stichproben", "{0} runs, {1} scan samples");
    public static string ArchiveRunSummary => S("{0} von {1} Geräten online · {2} Pings erfolgreich, {3} fehlgeschlagen · Netzwerke: {4}", "{0} of {1} devices online · {2} pings succeeded, {3} failed · Networks: {4}");
    public static string ArchiveDatabaseSize => S("Datenbankgröße: {0:0.0} MiB", "Database size: {0:0.0} MiB");
    public static string ArchiveConfirmDeleteRun => S("Scanlauf vom {0} im Netzwerk {1} dauerhaft löschen?", "Permanently delete the scan run from {0} on network {1}?");
    public static string ArchiveConfirmDeleteRange => S("{0} Scanläufe vom {1:d} bis {2:d} dauerhaft löschen?", "Permanently delete {0} scan runs from {1:d} through {2:d}?");
    public static string ArchiveConfirmCompact => S("Bis zu {0} Scanläufe, älter als {1} Tage, zu Tageswerten verdichten? Einzelne Laufdetails gehen verloren.", "Compact up to {0} scan runs older than {1} days into daily summaries? Individual run details will be lost.");
    public static string ArchiveConfirmPurge => S("{0} Scanläufe, älter als {1} Tage, dauerhaft löschen?", "Permanently delete {0} scan runs older than {1} days?");
    public static string ArchiveConfirmImport => S("Archiv aus Datei hinzufügen? Vorhandene Läufe bleiben erhalten. Die Datei muss mit dem lokalen Schlüssel dieser Installation erstellt worden sein.", "Add runs from the archive file? Existing runs stay intact. The file must have been created with this installation's local key.");
    public static string ArchiveZeroDaysRefused => S("Gib 1 bis 36500 Tage ein. 0 Tage werden aus Sicherheitsgründen abgelehnt.", "Enter 1 to 36500 days. Zero days are refused for safety.");
    public static string ArchiveDeleted => S("{0} Scanläufe gelöscht.", "{0} scan runs deleted.");
    public static string ArchiveCompacted => S("Archiv um {0} Scanläufe verdichtet.", "Archive reduced by {0} scan runs.");
    public static string ArchiveImported => S("{0} Läufe eingelesen, {1} schon vorhanden.", "{0} runs imported, {1} already present.");
    public static string ArchiveStopped => S("abgebrochen", "stopped");
    public static string ArchiveDaysAboveRetention => S("Muss kleiner sein als die Aufbewahrung des Archivs ({0} Tage).", "Must be lower than the archive retention ({0} days).");
    public static string ArchiveFileFilter => S("Verschlüsseltes Archiv (*.json;*.json.bak)|*.json;*.json.bak", "Encrypted archive (*.json;*.json.bak)|*.json;*.json.bak");
    public static string ArchiveInvalidFile => S("Archivdatei oder lokaler Schlüssel ist ungültig. Prüfe die Datei und verwende die ursprüngliche Installation.", "Archive file or local key is invalid. Check the file and use the original installation.");
    public static string ArchiveAccessDenied => S("Kein Zugriff auf die Archivdatei. Wähle einen Ordner mit Schreibrechten.", "Cannot access the archive file. Choose a folder you can write to.");
    public static string ArchiveIoError => S("Archivdatei konnte nicht gelesen oder geschrieben werden. Prüfe Speicherplatz und Dateizugriff.", "Could not read or write the archive file. Check free space and file access.");
    public static string ArchiveUnexpectedError => S("Das Archiv konnte nicht verarbeitet werden. Prüfe die Datenbank und starte die Anwendung neu.", "Could not process the archive. Check the database and restart the app.");
    public static string ToggleSidebar => S("Seitenleiste ein-/ausblenden", "Toggle sidebar");
    public static string ToolWakeOnLan => S("Aufwecken", "Wake up");
    public static string ToolPortCheck => S("Ports prüfen", "Port check");
    public static string ToolDnsLookup => S("Namensauflösung", "DNS lookup");
    public static string ToolPingRoute => S("Ping und Weg", "Ping and route");
    public static string ToolCalculator => S("Rechner", "Calculator");
    public static string ToolCertificate => S("Zertifikat", "Certificate");
    public static string LangAuto => S("Automatisch", "Automatic");
    public static string TabInternet => S("Internet", "Internet");
    public static string TabOutput => S("Ausgabe", "Output");
    public static string TabDatabase => S("Datenbank", "Database");
    public static string NavScan => S("Scan", "Scan");
    public static string NavDisplay => S("Anzeige", "Display");
    public static string NavData => S("Daten", "Data");
    public static string Subnets => S("Netzwerke", "Networks");
    public static string PinnedIps => S("Angeheftete IPs", "Pinned IPs");
    public static string PingInterval => S("Ping-Intervall", "Ping interval");
    public static string OfflineAfter => S("Offline nach X Fehlversuchen", "Offline after X failures");
    public static string InitPings => S("Such-Pings je IP", "Discovery pings per IP");
    public static string StartupPings => S("Pings bei Start", "Pings at startup");
    public static string DefaultPings => S("Standard-Pings", "Default pings");
    public static string InvalidPath => S("Ungültiger Pfad", "Invalid path");
    // Shown instead of InvalidPath when the text is a syntactically
    // valid path, but points at a drive root or the user's own profile folder -
    // a folder the app must never re-ACL to itself alone.
    public static string UnsafeDataPath => S(
        "Laufwerk oder Benutzerverzeichnis wird nicht akzeptiert - bitte einen eigenen Unterordner wählen",
        "Drive or user folder not accepted - please choose a subfolder of your own");
    public static string ScanThreads => S("Threads pro Scan", "Threads per scan");
    // "0 Threads" wird zum Kontrollkästchen "Unbegrenzt" (reuses DbRetentionUnlimited's "Unlimited" key).
    public static string UnlimitedThreadsLabel => S("Unbegrenzt", "Unlimited");
    public static string EndlessLabel => S("Endlos", "Endless");
    public static string OffLabel => S("Aus", "Off");
    public static string DiscoveryOnlyLabel => S("Nur Erkennung", "Discovery only");
    public static string KeyCtrl => S("Strg", "Ctrl");
    public static string KeyDel => S("Entf", "Del");
    public static string ShortcutCtrlF => $"{KeyCtrl}+F";
    public static string ShortcutCtrlComma => $"{KeyCtrl}+,";
    public static string ShortcutCtrlT => $"{KeyCtrl}+T";
    public static string ShortcutCtrlB => $"{KeyCtrl}+B";
    public static string ShortcutCtrlE => $"{KeyCtrl}+E";
    public static string ShortcutCtrl1 => $"{KeyCtrl}+1";
    public static string ShortcutCtrl2 => $"{KeyCtrl}+2";
    public static string ShortcutCtrl3 => $"{KeyCtrl}+3";
    public static string ShortcutCtrlC => $"{KeyCtrl}+C";
    public static string DbCsvFileName => S("bekannte_geraete.csv", "known_devices.csv");
    public static string TextScale => S("Textgröße", "Text size");
    public static string ColorScheme => S("Farbschema", "Color scheme");
    public static string AccentColorLabel => S("Akzentfarbe", "Accent color");
    public static string TipAccentColor => S(
        "Frei wählbare Farbe für Titel, Fokusrahmen und ausgewählte Einträge.",
        "Freely chosen color for titles, focus borders and selected entries.");
    public static string DateNumberFormatLabel => S("Datums-/Zahlenformat", "Date/number format");
    public static string TipDateNumberFormat => S(
        "Legt fest, wie Datum, Uhrzeit und Zahlen dargestellt werden - unabhängig von der Sprache der Oberfläche.",
        "Sets how dates, times and numbers are displayed - independent of the interface language.");
    public static string CompactRowsLabel => S("Kompakte Zeilen", "Compact rows");
    public static string TipCompactRows => S(
        "Dichtere Tabellenzeilen, mehr Geräte auf einen Blick.",
        "Denser table rows, more devices visible at once.");
    public static string ColumnsLabel => S("Tabellenspalten", "Table columns");
    public static string TipColumns => S(
        "Blendet einzelne Spalten aus. Adresse, Status und Hostname bleiben immer sichtbar.",
        "Hides individual columns. Address, status and hostname always stay visible.");
    // Scan comfort settings.
    public static string ScanComfortLabel => S("Scan-Komfort", "Scan comfort");
    // First-run welcome card.
    public static string FirstRunTitle => S("Willkommen bei IP-Scanner", "Welcome to IP-Scanner");
    public static string FirstRunNetworkFmt => S("Erkanntes Netzwerk: {0}", "Detected network: {0}");
    public static string FirstRunNoNetwork => S("Kein Netzwerk erkannt - ein Netzwerk kann manuell hinzugefügt werden.", "No network detected - a network can be added manually.");
    public static string FirstRunConfigFmt => S("Einstellungen: {0}", "Settings: {0}");
    public static string FirstRunDatabaseFmt => S("Datenbank: {0}", "Database: {0}");
    public static string FirstRunReportsFmt => S("Berichte: {0}", "Reports: {0}");
    public static string FirstRunCheckUpdates => S(
        "Bei GitHub nach neuen Versionen suchen", "Check GitHub for new versions");
    public static string FirstRunMeasureInternet => S(
        "Internet-Latenz messen (pingt Google, Cloudflare und Quad9)",
        "Measure internet latency (pings Google, Cloudflare and Quad9)");
    public static string FirstRunScanNow => S("Scan starten", "Start scan");
    public static string FirstRunDismiss => S("Nicht mehr zeigen", "Don't show again");
    // Network adapter picker on the primary network card.
    public static string NetworkInterfaceAuto => S("Automatisch", "Automatic");
    public static string TipNetworkInterfacePicker => S("Netzwerkkarte für Scan und Ping wählen", "Choose the network adapter to scan and ping from");
    public static string RepeatScanLabel => S("Scan wiederholen", "Repeat scan");
    public static string TipRepeatScan => S(
        "Startet den Scan automatisch alle N Minuten erneut, solange die App läuft.",
        "Automatically restarts the scan every N minutes while the app is running.");
    public static string RepeatScanOff => S("Aus", "Off");
    public static string ArchiveCompactionOff => S("Nie", "Never");
    public static string RepeatScanMinutesFmt => S("Alle {0} Min.", "Every {0} min");
    public static string RepeatScanMinutes(int n) => string.Format(RepeatScanMinutesFmt, n);
    public static string NotifyScanDoneLabel => S("Meldung bei Scan-Ende", "Notify when scan finishes");
    public static string TipNotifyScanDone => S(
        "Zeigt eine Sprechblase im Infobereich, sobald ein Scan fertig ist.",
        "Shows a tray balloon as soon as a scan finishes.");
    public static string NotifyNewDeviceLabel => S("Meldung bei neuem Gerät", "Notify on new device");
    public static string TipNotifyNewDevice => S(
        "Zeigt eine Sprechblase, sobald ein neues, unbekanntes Gerät im Netzwerk gefunden wird.",
        "Shows a tray balloon as soon as a new, unknown device is found on the network.");
    public static string StartMinimizedLabel => S("Minimiert starten", "Start minimized");
    public static string TipStartMinimized => S(
        "Startet die App direkt im Infobereich, ohne das Fenster anzuzeigen.",
        "Starts the app straight into the tray without showing the window.");
    public static string TrayScanDoneTitle => S("Scan abgeschlossen", "Scan finished");
    public static string TrayScanDoneTextFmt => S("{0} Geräte online.", "{0} devices online.");
    public static string TrayScanDoneText(int online) => string.Format(TrayScanDoneTextFmt, online);
    public static string TrayNewDeviceTitle => S("Neues Gerät gefunden", "New device found");
    public static string TrayNewDeviceTextFmt => S("{0} ({1}) ist neu im Netzwerk.", "{0} ({1}) is new on the network.");
    public static string TrayNewDeviceText(string ip, string name) => string.Format(TrayNewDeviceTextFmt, ip, name);
    public static string OfflineRecheck => S("Offline-Nachprüfung (s)", "Offline recheck (s)");
    public static string InvalidEntryFmt => S("Ungültiger Eintrag: {0}", "Invalid entry: {0}");
    public static string TabLatencyColors => S("Latenz-Farbskala", "Latency colors");
    public static string HeatMs1 => S("Sehr gut bis", "Excellent up to");
    public static string HeatMs2 => S("Gut bis", "Good up to");
    public static string HeatMs3 => S("Okay bis", "Okay up to");
    public static string HeatMs4 => S("Schlecht bis", "Bad up to");
    public static string TipHeatMs1 => S("Bis zu diesem Wert gilt die Latenz als sehr gut (grün).", "Up to this value, latency counts as excellent (green).");
    public static string TipHeatMs2 => S("Bis zu diesem Wert gilt die Latenz als gut (hellgrün).", "Up to this value, latency counts as good (lime).");
    public static string TipHeatMs3 => S("Bis zu diesem Wert gilt die Latenz als okay (gelb).", "Up to this value, latency counts as okay (yellow).");
    public static string TipHeatMs4 => S("Bis zu diesem Wert gilt die Latenz als schlecht (orange); darüber als sehr schlecht (rot).",
        "Up to this value, latency counts as bad (peach); above it, very bad (red).");
    public static string ResetHeatDefaults => S("Standard", "Defaults");
    public static string TipResetHeatDefaults => S("Setzt die vier Schwellenwerte auf 50/100/200/400 ms zurück.", "Resets the four thresholds to 50/100/200/400 ms.");
    public static string HeatOrderCorrected => S("Werte wurden aufsteigend sortiert.", "Values were reordered to stay ascending.");
    public static string EmptyEntry => S("Bitte zuerst einen Eintrag eingeben.", "Enter an entry first.");
    public static string InvalidEntry(string entry)
        => string.IsNullOrWhiteSpace(entry) ? EmptyEntry : string.Format(InvalidEntryFmt, entry);
    public static string DuplicateEntryFmt => S(
        "{0} wird schon von einem anderen Eintrag benutzt.",
        "{0} is already used by another entry.");
    public static string DuplicateEntry(string entry) => string.Format(DuplicateEntryFmt, entry);
    public static string DuplicateName => S(
        "Name wird schon benutzt. Trotzdem speichern?",
        "Name already in use. Save anyway?");
    public static string ExportConf => S("Einstellungen exportieren …", "Export settings …");
    public static string ImportConf => S("Einstellungen importieren …", "Import settings …");
    public static string TabConfig => S("Konfigurationsdatei", "Configuration file");
    public static string TabApp => S("App", "App");
    public static string NavAbout => S("Über", "About");
    // Keyboard-shortcut help page.
    public static string NavHelp => S("Hilfe", "Help");
    public static string HelpPageTitle => S("Tastenkürzel", "Keyboard shortcuts");
    public static string HelpShortcutHeader => S("Kürzel", "Shortcut");
    public static string HelpActionHeader => S("Aktion", "Action");
    public static string HelpF5 => S("Scan starten oder stoppen", "Start or stop the scan");
    public static string HelpCtrlF => S("Suche fokussieren", "Focus search");
    public static string HelpEsc => S("Einstellungen schließen oder Suche leeren", "Close settings or clear search");
    public static string HelpCtrlComma => S("Einstellungen öffnen", "Open settings");
    public static string HelpCtrlT => S("Werkzeuge öffnen", "Open tools");
    public static string HelpCtrlB => S("Seitenleiste ein- oder ausblenden", "Show or hide the sidebar");
    public static string HelpCtrlE => S("Ordner \"Scans\" öffnen", "Open the Scans folder");
    public static string HelpCtrl1 => S("Filter: nur online", "Filter: online only");
    public static string HelpCtrl2 => S("Filter: nur angeheftet", "Filter: pinned only");
    public static string HelpCtrl3 => S("Filter: nur unbekannt", "Filter: unknown only");
    public static string HelpCtrlC => S("Fokussierte Tabellenzeile als Text kopieren", "Copy the focused table row as text");
    public static string HelpDel => S("Fokussierten Seitenleisten-Eintrag löschen (mit Bestätigung)", "Delete the focused sidebar entry (with confirmation)");
    public static string HelpF1 => S("Diese Hilfeseite öffnen", "Open this help page");
    public static string AboutVersionLabel => S("Version", "Version");
    public static string AboutManufacturerLabel => S("Hersteller", "Manufacturer");
    public static string AboutLicenseLabel => S("Lizenz", "License");
    public static string AboutIconAttribution => S(
        "Symbole: Material Design Icons von Pictogrammers, Apache-Lizenz 2.0. Bibliotheken: SharpZipLib und Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache-Lizenz 2.0). Vollständige Hinweise: THIRD-PARTY-NOTICES.md neben der App.",
        "Icons: Material Design Icons by Pictogrammers, Apache License 2.0. Libraries: SharpZipLib and Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache License 2.0). Full notices: THIRD-PARTY-NOTICES.md next to the app.");
    public static string AboutOpenWindow => S("Info-Fenster öffnen", "Open info window");
    public static string TipAboutOpenWindow => S(
        "Öffnet ein eigenes Fenster mit dem Verweis auf die Projektseite.",
        "Opens a separate window with a link to the project page.");
    public static string AboutWindowTitle => S("Über IP-Scanner", "About IP-Scanner");
    public static string AboutOpenProjectPage => S("Projektseite öffnen", "Open project page");
    public static string CheckUpdatesNow => S("Nach Updates suchen", "Check for updates");
    public static string UpToDate => S("Aktuell", "Up to date");
    public static string Checking => S("Wird geprüft …", "Checking …");
    public static string EnableInternet => S("Internet-Ping aktivieren", "Enable internet ping");
    public static string OutputDir => S("Ausgabeverzeichnis", "Output directory");
    public static string Browse => S("Durchsuchen …", "Browse …");
    public static string WriteTxt => S("TXT-Bericht schreiben", "Write TXT report");
    public static string ExportCsv => S("CSV exportieren", "Export CSV");
    public static string KnownDb => S("Known-Devices-Datenbank aktivieren", "Enable known-devices database");
    public static string ClearDb => S("Datenbank leeren", "Clear database");
    public static string MergeDb => S("Datenbanken zusammenführen …", "Merge databases …");
    // The plain, portable counterpart to the encrypted scanner.db - moves device
    // history to a different Windows account/machine, or reads it back in.
    public static string ExportDbCsv => S("Datenbank exportieren …", "Export database …");
    public static string ImportDbCsv => S("Datenbank importieren …", "Import database …");
    public static string TipExportDbCsv => S(
        "Schreibt alle gespeicherten Geräte als lesbare CSV-Datei - zum Umziehen auf einen "
        + "anderen Rechner oder ein anderes Windows-Konto.",
        "Writes every stored device as a plain, readable CSV file - for moving to a different "
        + "computer or Windows account.");
    public static string TipImportDbCsv => S(
        "Liest eine mit „Datenbank exportieren“ geschriebene Datei wieder ein.",
        "Reads back a file written by \"Export database\".");
    public static string CsvRowsFmt => S("{0} Einträge.", "{0} entries.");
    public static string CsvRowsOne => S("1 Eintrag.", "1 entry.");
    public static string CsvRows(int n) => n == 1 ? CsvRowsOne : string.Format(CsvRowsFmt, n);
    public static string CsvImportResultFmt => S(
        "{0} Einträge importiert, {1} übersprungen.", "{0} entries imported, {1} skipped.");
    public static string CsvImportResultOne => S(
        "1 Eintrag importiert, {0} übersprungen.", "1 entry imported, {0} skipped.");
    public static string CsvImportResult(int imported, int skipped) =>
        imported == 1 ? string.Format(CsvImportResultOne, skipped) : string.Format(CsvImportResultFmt, imported, skipped);
    public static string DbFile => S("Datenbankdatei", "Database file");
    public static string InternetTimeout => S("Ping-Timeout", "Ping timeout");
    public static string ConfNote => S(
        "Die Einstellungen werden automatisch als ip_scanner.conf gespeichert und beim Start geladen.",
        "Settings are saved automatically as ip_scanner.conf and loaded at startup.");
    // Privacy note under the database path: the file is encrypted, not just access-restricted.
    public static string DbPrivacyNote => S(
        "Enthält IP, MAC, Hostname und letzten Zeitpunkt jedes erkannten Geräts. Verschlüsselt, "
        + "an dein Windows-Konto gebunden, ohne dass du je ein Passwort eingibst.",
        "Contains the IP, MAC, hostname and last-seen time of every device found. Encrypted, tied "
        + "to your Windows account, without ever asking you for a password.");
    public static string MergeDoneFmt => S("{0} Einträge zusammengeführt.", "{0} entries merged.");
    public static string MergeDone(int n) => string.Format(MergeDoneFmt, n);
    public static string MergeRecordsFmt => S("{0} / {1} Einträge", "{0} / {1} entries");
    public static string MergeRecords(int done, int total) => string.Format(MergeRecordsFmt, done, total);
    public static string LargeScanTitleFmt => S("{0:N0} Adressen scannen?", "Scan {0:N0} addresses?");
    public static string LargeScanTitle(long count) => string.Format(LargeScanTitleFmt, count);
    public static string LargeScanMessageFmt => S("Dieser Scan umfasst {0:N0} Adressen und kann lange dauern.", "This scan covers {0:N0} addresses and can take a long time.");
    public static string LargeScanMessage(long count) => string.Format(LargeScanMessageFmt, count);
    public static string MergeSourceSizeFmt => S("Quelldatei: {0} MB", "Source file: {0} MB");
    public static string MergeSourceSize(double mb) => string.Format(MergeSourceSizeFmt,
        mb.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
    // The chosen database was sealed under a different Windows account's key -
    // genuinely unreadable, not a bug.
    public static string ForeignDatabaseMsg => S(
        "Diese Datenbank gehört zu einem anderen Windows-Konto und kann nicht gelesen werden. Exportiere sie dort und importiere die Exportdatei hier.",
        "This database belongs to a different Windows account and cannot be read. Export it there and import the exported file here.");
    public static string ResetDefaults => S("Auf Standard zurücksetzen", "Reset to defaults");
    public static string Threads => S("Threads", "Threads");
    public static string MaxLabel => S("max", "max");
    public static string ClearDbConfirm => S(
        "Alle gespeicherten Geräte samt Latenzverlauf und Portergebnissen löschen? Das lässt sich nicht rückgängig machen.",
        "Delete all stored devices, their latency history and port results? This cannot be undone.");
    public static string ClearDbIncludeArchive => S(
        "Auch das Scan-Archiv und seine Sicherungen löschen", "Also delete the scan archive and its backups");
    public static string ClearDbDoneFmt => S("{0} Geräte gelöscht.", "{0} devices deleted.");
    public static string ClearDbDone(int n) => string.Format(ClearDbDoneFmt, n);
    public static string ScanHistory => S("Scan-Archiv führen", "Keep a scan archive");
    public static string TipScanHistory => S(
        "Speichert jeden Scan, damit du ihn später im Scan-Archiv ansehen kannst. Benötigt die Known-Devices-Datenbank.",
        "Stores every scan so you can review it later in the scan archive. Needs the known-devices database.");
    public static string ResetConfirm => S("Alle Einstellungen zurücksetzen?", "Reset all settings?");
    public static string Confirm => S("Bestätigen", "Confirm");
    public static string Cancel => S("Abbrechen", "Cancel");
    public static string Yes => S("Ja", "Yes");
    public static string No => S("Nein", "No");

    // ── Settings tooltips ──
    public static string TipPinnedIps => S(
        "Diese IPs werden immer mitgepingt (auch außerhalb der Netzwerke) und stehen ganz oben in der Tabelle.",
        "These IPs are always pinged (even outside the networks) and stay at the top of the table.");
    public static string TipScanThreads => S(
        "Parallele Ping-Worker für Suche und Analyse, auch für den automatischen Scan nach dem Start. Standard 100; \"Unbegrenzt\" nutzt einen Thread pro Gerät (maximale Geschwindigkeit, mehr Last).",
        "Parallel ping workers for discovery and analysis, including the automatic sweep after startup. Default 100; \"Unlimited\" uses one thread per device (max speed, more load).");
    public static string TipExportConf => S(
        "Speichert alle aktuellen Einstellungen in eine .conf-Datei.",
        "Saves all current settings to a .conf file.");
    public static string TipImportConf => S(
        "Lädt Einstellungen aus einer zuvor exportierten .conf-Datei.",
        "Loads settings from a previously exported .conf file.");
    // ── Full backup and restore ──
    public static string BackupHeader => S("Sichern und Wiederherstellen", "Back up and restore");
    public static string BackupHint => S(
        "Sichert Einstellungen, Geräteliste, Scan-Archiv, Notizen und manuelle Änderungen in einer ZIP-Datei, auf Wunsch mit Passwort (AES-256). Eine Sicherung mit Passwort lässt sich auch mit 7-Zip öffnen und auf einem anderen PC oder in einem anderen Windows-Konto wiederherstellen. Ohne Passwort funktioniert das Wiederherstellen nur im selben Windows-Konto.",
        "Saves settings, device list, scan archive, notes and manual edits into one ZIP file, optionally protected with a password (AES-256). A password-protected backup also opens in 7-Zip and can be restored on another PC or Windows account. Without a password, restoring works only under the same Windows account.");
    public static string BackupButton => S("Alles sichern …", "Back up everything …");
    public static string RestoreButton => S("Wiederherstellen …", "Restore from backup …");
    public static string TipBackup => S(
        "Speichert Einstellungen, Geräteliste, Scan-Archiv und Notizen in einer ZIP-Datei.",
        "Saves the settings, device list, scan archive and notes into one ZIP file.");
    public static string TipRestore => S(
        "Stellt eine Sicherung wieder her. Der aktuelle Stand wird vorher gesichert, danach schließt sich das Programm.",
        "Restores a backup. The current state is saved first and the app closes afterwards.");
    public static string BackupFileFilter => S("Sicherung (*.zip)|*.zip", "Backup (*.zip)|*.zip");
    public static string BackupPasswordTitle => S("Passwort der Sicherung", "Backup password");
    public static string BackupPasswordSetTitle => S("Sicherung schützen", "Protect backup");
    public static string BackupPasswordSetHint => S(
        "Passwort zum Verschlüsseln der Sicherung eingeben (AES-256). Mit Passwort lässt sich die Sicherung auch auf einem anderen PC oder in einem anderen Windows-Konto wiederherstellen. Beide Felder leer lassen für eine Sicherung ohne Passwort; sie lässt sich nur in diesem Windows-Konto wiederherstellen. Ohne das Passwort lässt sich die Sicherung nicht wiederherstellen.",
        "Enter a password to encrypt the backup (AES-256). With a password the backup can also be restored on another PC or Windows account. Leave both fields empty for a backup without a password; that one can only be restored under this Windows account. Without the password the backup cannot be restored.");
    public static string BackupPasswordAskHint => S(
        "Diese Sicherung ist mit einem Passwort geschützt. Zum Fortfahren das Passwort eingeben.",
        "This backup is password protected. Enter the password to continue.");
    public static string BackupPasswordLabel => S("Passwort", "Password");
    public static string BackupPasswordRepeat => S("Passwort wiederholen", "Repeat password");
    public static string BackupPasswordMismatch => S("Die Passwörter stimmen nicht überein.", "The passwords do not match.");
    public static string BackupPasswordWrong => S("Falsches Passwort. Bitte erneut versuchen.", "Wrong password. Try again.");
    public static string BackupPasswordEmpty => S("Bitte das Passwort eingeben.", "Enter the password.");
    public static string BackupSavedFmt => S("Sicherung gespeichert: {0}", "Backup saved: {0}");
    public static string BackupSaved(string path) => string.Format(BackupSavedFmt, path);
    public static string BackupFailedTitle => S("Sicherung fehlgeschlagen", "Backup failed");
    public static string RestoreFailedTitle => S("Wiederherstellen fehlgeschlagen", "Restore failed");
    public static string RestoreConfirmTitle => S("Sicherung wiederherstellen", "Restore backup");
    public static string RestoreConfirmFmt => S(
        "Sicherung vom {0} (Version {1}) wiederherstellen? Der aktuelle Stand wird vorher als Sicherheitskopie im Ordner Backups neben der Datenbank gesichert. IP-Scanner schließt sich danach und muss neu gestartet werden.",
        "Restore the backup from {0} (version {1})? Your current data is saved first as a safety backup in the Backups folder next to the database. IP-Scanner closes afterwards and has to be started again.");
    public static string RestoreConfirm(DateTime created, string version)
        => string.Format(RestoreConfirmFmt, created.ToString("g", System.Globalization.CultureInfo.CurrentCulture), version);
    public static string RestoreDoneFmt => S(
        "Die Sicherung wurde wiederhergestellt. Der vorherige Stand liegt in: {0}. IP-Scanner schließt sich jetzt und muss neu gestartet werden.",
        "The backup was restored. The previous state was saved to: {0}. IP-Scanner closes now and has to be started again.");
    public static string RestoreDone(string safetyPath) => string.Format(RestoreDoneFmt, safetyPath);
    public static string RestoreStopScanFirst => S(
        "Vor dem Wiederherstellen den laufenden Scan beenden.",
        "Stop the running scan before restoring a backup.");
    public static string BackupNotABackup => S("Diese Datei ist keine IP-Scanner-Sicherung.", "This file is not an IP-Scanner backup.");
    public static string BackupNewerFormat => S(
        "Diese Sicherung stammt aus einer neueren IP-Scanner-Version. Zuerst das Programm aktualisieren.",
        "This backup was created by a newer version of IP-Scanner. Update the app first.");
    public static string BackupCorrupt => S(
        "Die Sicherung ist beschädigt oder unvollständig. Es wurde nichts verändert.",
        "The backup is damaged or incomplete. Nothing was changed.");
    public static string BackupKeyForeign => S(
        "Diese Sicherung wurde ohne Passwort in einem anderen Windows-Konto erstellt und lässt sich hier nicht wiederherstellen. Für Sicherungen, die auf einen anderen PC oder in ein anderes Konto wechseln sollen, ein Passwort verwenden. Es wurde nichts verändert.",
        "This backup was made without a password under a different Windows account and cannot be restored here. Use a password for backups that are meant to move to another PC or account. Nothing was changed.");
    public static string BackupKeyUnreadable => S(
        "Der Schlüssel dieser Installation lässt sich nicht lesen (anderes Windows-Konto?). Eine Sicherung mit Passwort kann deshalb nicht erstellt werden.",
        "The key of this installation cannot be read (different Windows account?), so a password-protected backup cannot be created.");
    public static string BackupIoFailed => S(
        "Eine Datei konnte nicht gelesen oder geschrieben werden. Es wurde nichts verändert.",
        "A file could not be read or written. Nothing was changed.");
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
        "Misst nebenbei die Latenz zu öffentlichen Hosts in der Seitenleiste. Zeigt, ob die Internetverbindung steht.",
        "Also measures latency to public hosts in the sidebar. Shows whether the internet connection is up.");
    public static string TipDefaultPings => S(
        "Vorbelegung des Ping-Dropdowns oben rechts beim Programmstart. Mit Endlos wird bis zum Stopp gepingt.",
        "Preset for the ping dropdown at the top right when the app starts. Tick Endless to keep pinging until stopped.");
    public static string TipStartupPings => S(
        "Ping-Anzahl je Gerät für den automatischen Scan direkt nach dem Programmstart. Mit Nur Erkennung werden nur Geräte gesucht.",
        "Ping count per device for the automatic scan right after startup. Tick Discovery only to just look for devices.");
    public static string TipOfflineRecheck => S(
        "Prüft während eines Durchlaufs alle N Sekunden, ob Offline-IPs online gekommen sind. Neue Geräte steigen sofort in den Durchlauf ein. Wirkt nur bei endlosen Pings.",
        "While a run is active, rechecks offline IPs every N seconds. Devices that come online join the run immediately. Only takes effect with endless pings.");
    public static string TipTextScale => S(
        "Skaliert die gesamte Oberfläche inklusive aller Texte. 100 = Standardgröße.",
        "Scales the whole interface including all text. 100 = default size.");
    public static string TipOutputDir => S(
        "Ordner für alle Berichte, Exporte und Archiv-Sicherungen.",
        "Folder for all reports, exports and archive backups.");
    public static string TipWriteTxt => S(
        "Schreibt nach jedem Scan einen lesbaren Textbericht mit allen Tabellenwerten.",
        "Writes a readable text report with all table values after each scan.");
    public static string TipExportCsv => S(
        "Exportiert die Ergebnisse zusätzlich als CSV, praktisch für Excel oder Skripte.",
        "Additionally exports the results as CSV, handy for Excel or scripts.");
    // TXT/CSV nur noch über die Seitenleiste, kein doppelter Schalter mehr.
    public static string TxtCsvInSidebarHint => S(
        "TXT- und CSV-Export werden über die Pillen in der Seitenleiste ein- und ausgeschaltet.",
        "TXT and CSV export are toggled via the pills in the sidebar.");
    // Data settings.
    public static string ExportPortsLabel => S("Offene Ports mit exportieren", "Include open ports in exports");
    public static string TipExportPorts => S(
        "Prüft vor dem Bericht die offenen Ports jedes Online-Geräts und schreibt sie in TXT/CSV/HTML. "
        + "Dauert länger und sieht für Firewalls wie ein Portscan aus.",
        "Probes every online device's open ports before the report and writes them into TXT/CSV/HTML. "
        + "Takes longer and looks like a port scan to firewalls.");
    public static string HtmlReportLabel => S("HTML-Bericht", "HTML report");
    public static string TipHtmlReport => S(
        "Schreibt zusätzlich einen Bericht als HTML-Datei im aktuellen Farbschema.",
        "Also writes a report as an HTML file in the current color theme.");
    public static string OpenPortsHeader => S("Offene Ports", "Open ports");
    public static string DbRetentionLabel => S("Aufbewahrungsdauer", "Retention period");
    public static string TipDbRetention => S(
        "Geräte, die länger nicht mehr gesehen wurden, werden nach jedem Scan aus der Datenbank entfernt.",
        "Devices not seen for longer than this are removed from the database after each scan.");
    public static string ArchiveRetentionLabel => S("Scan-Archiv aufbewahren", "Keep scan archive");
    public static string TipArchiveRetention => S(
        "Läufe, die älter sind, werden nach jedem Scan gelöscht.",
        "Runs older than this are deleted after each scan.");
    public static string DbRetentionUnlimited => S("Unbegrenzt", "Unlimited");
    public static string DbRetentionDaysFmt => S("{0} Tage", "{0} days");
    public static string DbRetentionDaysText(int n) => string.Format(DbRetentionDaysFmt, n);
    // Legend entry for skipped pings.
    public static string ShowSkippedLegendLabel => S("\"Übersprungen\" in der Legende zeigen", "Show \"skipped\" in the legend");
    public static string TipShowSkippedLegend => S(
        "Blendet die dritte Ping-Kategorie (übersprungen) in der Legende ein. Gezählt wird sie so oder so.",
        "Shows the third ping category (skipped) in the legend. It is counted either way.");
    // Listen aus einer Datei einlesen.
    public static string ImportListsHeader => S("Listen aus Datei importieren", "Import lists from file");
    public static string ImportListsHint => S(
        "Eine Adresse oder ein Netz pro Zeile, optional gefolgt von einem Namen (mit Leerzeichen oder Komma "
        + "getrennt). Leere Zeilen und Zeilen mit # am Anfang werden ignoriert.",
        "One address or network per line, optionally followed by a name (space- or comma-separated). "
        + "Blank lines and lines starting with # are ignored.");
    public static string ImportListsTargetLabel => S("Liste", "List");
    public static string ImportListsModeLabel => S("Modus", "Mode");
    public static string ImportModeAppend => S("Ergänzen", "Append");
    public static string ImportModeReplace => S("Ersetzen", "Replace");
    public static string ImportListsBrowse => S("Datei wählen…", "Choose file…");
    public static string ImportListsCommit => S("Importieren", "Import");
    public static string ImportListsFileFilter => S(
        "Text/CSV (*.txt;*.csv)|*.txt;*.csv|Alle Dateien (*.*)|*.*",
        "Text/CSV (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*");
    public static string ImportListsPreviewFmt => S(
        "{0} Einträge erkannt, {1} von {2} Zeilen übersprungen.",
        "{0} entries recognized, {1} of {2} lines skipped.");
    public static string ImportListsPreviewText(int accepted, int skipped, int total)
        => string.Format(ImportListsPreviewFmt, accepted, skipped, total);
    public static string ImportListsNoneFmt => S(
        "Keine gültigen Einträge gefunden ({0} von {1} Zeilen übersprungen).",
        "No valid entries found ({0} of {1} lines skipped).");
    public static string ImportListsNoneText(int skipped, int total)
        => string.Format(ImportListsNoneFmt, skipped, total);
    public static string ImportListsDoneFmt => S("{0} Einträge gespeichert.", "{0} entries saved.");
    public static string ImportListsDoneText(int count) => string.Format(ImportListsDoneFmt, count);
    // Profile für mehrere Umgebungen.
    public static string DefaultProfileName => S("Standard", "Default");
    public static string ProfileLabel => S("Profil", "Profile");
    public static string TipProfile => S(
        "Aktives Profil - jedes Profil hat eigene Netzwerke, angeheftete IPs und Internetziele.",
        "Active profile - each profile has its own networks, pinned IPs and internet hosts.");
    public static string TipProfileAdd => S("Neues Profil anlegen", "Create a new profile");
    public static string TipProfileRename => S("Profil umbenennen oder löschen", "Rename or delete profile");
    public static string ProfileAddTitle => S("Neues Profil", "New profile");
    public static string ProfileRenameTitle => S("Profil umbenennen", "Rename profile");
    public static string ProfileNameRequired => S("Bitte einen Namen eingeben.", "Please enter a name.");
    public static string ProfileNameTaken => S(
        "Ein Profil mit diesem Namen existiert bereits.", "A profile with this name already exists.");
    public static string ProfileNameInvalidChars => S("Namen dürfen kein Komma und kein Semikolon enthalten.", "Names cannot contain commas or semicolons.");
    // Erweiterte Netzwerk-Einstellungen (Portscan-Feintuning).
    public static string AdvancedNetworkLabel => S("Erweiterte Netzwerk-Einstellungen", "Advanced network settings");
    public static string PortScanConnectTimeoutLabel => S("Zeitlimit fürs Anklopfen", "Connect timeout");
    public static string TipPortScanConnectTimeout => S(
        "Wie lange auf eine Antwort beim Verbindungsversuch zu einem Port gewartet wird.",
        "How long to wait for a reply when probing whether a port is open.");
    public static string PortScanBannerTimeoutLabel => S("Zeitlimit für die Versionsabfrage", "Banner timeout");
    public static string TipPortScanBannerTimeout => S(
        "Wie lange auf die Versions-/Banner-Antwort eines offenen Ports gewartet wird.",
        "How long to wait for the version/banner reply of an open port.");
    public static string PortScanParallelismLabel => S("Gleichzeitige Portabfragen", "Concurrent port probes");
    public static string TipPortScanParallelism => S(
        "Wie viele Ports gleichzeitig pro Gerät abgefragt werden.",
        "How many ports are probed at the same time per device.");
    public static string PortScanTimingLabel => S("Scan-Tempo", "Scan timing");
    public static string TipPortScanTiming => S(
        "Vorlage von 0 (sehr vorsichtig, unauffällig) bis 5 (maximal schnell). Die Vorlage füllt die Felder darunter, jedes Feld lässt sich einzeln überschreiben. 3 entspricht dem bisherigen Verhalten.",
        "Template from 0 (very careful, quiet) to 5 (as fast as possible). The template fills the fields below, and each field can still be overridden on its own. 3 matches the behavior so far.");
    public static string PortScanRetriesLabel => S("Wiederholungen ohne Antwort", "Retries without reply");
    public static string TipPortScanRetries => S(
        "Wie oft ein Port erneut abgefragt wird, wenn gar nichts zurückkam (0 bis 5).",
        "How often a port is probed again when nothing came back at all (0 to 5).");
    public static string PortScanDelayLabel => S("Pause zwischen Abfragen", "Delay between probes");
    public static string TipPortScanDelay => S(
        "Mindestabstand zwischen zwei Portabfragen. 0 schaltet die Pause ab.",
        "Minimum gap between two port probes. 0 turns the delay off.");
    public static string PortScanMaxRateLabel => S("Höchstrate", "Maximum rate");
    public static string TipPortScanMaxRate => S(
        "Obergrenze für Portabfragen pro Sekunde. 0 bedeutet unbegrenzt.",
        "Upper limit of port probes per second. 0 means unlimited.");
    public static string PortScanIntensityLabel => S("Dienst-Erkennungsstufe", "Service detection level");
    public static string TipPortScanIntensity => S(
        "Aufwand der Diensterkennung auf offenen Ports, von 0 (nur lauschen) bis 9 (alle Sonden). Höhere Stufen senden mehr Abfragen an die Zielgeräte.",
        "Effort of service detection on open ports, from 0 (listen only) to 9 (every probe). Higher levels send more probes to the target devices.");
    public static string PortScanTimingParanoid => S("Paranoid", "Paranoid");
    public static string PortScanTimingSneaky => S("Verstohlen", "Sneaky");
    public static string PortScanTimingPolite => S("Höflich", "Polite");
    public static string PortScanTimingNormal => S("Normal", "Normal");
    public static string PortScanTimingAggressive => S("Aggressiv", "Aggressive");
    public static string PortScanTimingInsane => S("Wahnsinnig", "Insane");
    public static string PortScanTimingUnlimited => S("unbegrenzt", "unlimited");
    public static string PortScanTimingSummaryFmt => S(
        "{0} parallel, {1} ms Zeitlimit, {2} Wiederholungen, {3} ms Pause, Rate {4}",
        "{0} parallel, {1} ms timeout, {2} retries, {3} ms delay, rate {4}");
    public static string PortScanTimingName(int template) => template switch
    {
        0 => PortScanTimingParanoid,
        1 => PortScanTimingSneaky,
        2 => PortScanTimingPolite,
        3 => PortScanTimingNormal,
        4 => PortScanTimingAggressive,
        _ => PortScanTimingInsane,
    };
    public static string PortScanTimingSummary(int parallel, int rttMs, int retries, int delayMs, int maxRate)
        => string.Format(PortScanTimingSummaryFmt,
            parallel, rttMs, retries, delayMs, maxRate > 0 ? maxRate + "/s" : PortScanTimingUnlimited);
    public static string CustomPortsLabel => S("Eigene Ports", "Custom ports");
    public static string TipCustomPorts => S(
        "Zusätzliche Ports, kommagetrennt (z. B. \"8443 Verwaltung, 9000\"), die bei jedem Portscan mitgeprüft werden.",
        "Extra ports, comma-separated (e.g. \"8443 Admin, 9000\"), checked on every port scan.");
    public static string CustomPortDefaultName => S("Benutzerdefiniert", "Custom");
    public static string SnmpCommunityLabel => S("SNMP-Community", "SNMP community");
    public static string TipSnmpCommunity => S(
        "Community-String für die SNMP-Versionsabfrage (Standard: \"public\"). Wird verschlüsselt gespeichert und nur an Geräte in deinen eigenen Netzen gesendet.",
        "Community string used for the SNMP version query (default: \"public\"). Stored encrypted. Sent only to devices in your own networks.");
    public static string TipKnownDb => S(
        "Merkt sich gefundene Geräte je Netzwerk in scanner.db, damit bekannte Geräte beim nächsten Scan wiedererkannt werden.",
        "Remembers discovered devices per network in scanner.db so known devices are recognized on the next scan.");
    public static string TipInternetTimeout => S(
        "ICMP-Timeout für die Pings an Internetziele in Millisekunden. Standard 1000.",
        "ICMP timeout for the internet host pings in milliseconds. Default 1000.");
    public static string TipLanguage => S(
        "Sprache der Oberfläche. Automatisch folgt der Systemsprache (Englisch als Fallback).",
        "UI language. Automatic follows the system language (English fallback).");
    public static string ThemeDark => S("Dunkel", "Dark");
    public static string ThemeLight => S("Hell", "Light");
    public static string TipTheme => S(
        "Farbschema der Oberfläche. Automatisch folgt der Windows-Einstellung.",
        "Interface colors. Automatic follows the Windows setting.");
    public static string TipDbFile => S(
        "Pfad der Known-Devices-Datenbank. Liegt standardmäßig neben der Programmdatei.",
        "Path of the known-devices database. Lives next to the program file by default.");
    public static readonly IReadOnlyDictionary<string, (Func<string> Description, string Default)> ConfigDescriptions =
        new Dictionary<string, (Func<string>, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["pinned_ips"] = (() => TipPinnedIps, ""),
            ["scan_threads"] = (() => TipScanThreads, "100"),
            ["ping_interval_ms"] = (() => TipPingInterval, "100 ms"),
            ["offline_after_failed_pings"] = (() => TipOfflineAfter, "5"),
            ["init_ping_count"] = (() => TipInitPings, "1"),
            ["enable_internet_ping"] = (() => TipEnableInternet, "true"),
            ["ping_count"] = (() => TipDefaultPings, "10"),
            ["startup_ping_count"] = (() => TipStartupPings, "10"),
            ["offline_recheck_seconds"] = (() => TipOfflineRecheck, "5 s"),
            ["ui_scale"] = (() => TipTextScale, "100%"),
            ["output_directory"] = (() => TipOutputDir, @".\Scans"),
            ["file_output"] = (() => TipWriteTxt, "false"),
            ["export_csv"] = (() => TipExportCsv, "false"),
            ["export_ports"] = (() => TipExportPorts, "false"),
            ["html_report"] = (() => TipHtmlReport, "false"),
            ["db_retention_days"] = (() => TipDbRetention, "365 days"),
            ["show_skipped_legend"] = (() => TipShowSkippedLegend, "false"),
            ["active_profile"] = (() => TipProfile, ""),
            ["port_scan_connect_timeout_ms"] = (() => TipPortScanConnectTimeout, "500 ms"),
            ["port_scan_banner_timeout_ms"] = (() => TipPortScanBannerTimeout, "600 ms"),
            ["port_scan_parallel"] = (() => TipPortScanParallelism, "40"),
            ["port_scan_timing"] = (() => TipPortScanTiming, "3"),
            ["port_scan_retries"] = (() => TipPortScanRetries, "0"),
            ["port_scan_delay_ms"] = (() => TipPortScanDelay, "0 ms"),
            ["port_scan_max_rate"] = (() => TipPortScanMaxRate, "0"),
            ["port_scan_intensity"] = (() => TipPortScanIntensity, "7"),
            ["custom_ports"] = (() => TipCustomPorts, ""),
            ["snmp_community"] = (() => TipSnmpCommunity, "public"),
            ["known_devices_db"] = (() => TipKnownDb, "true"),
            ["internet_timeout_ms"] = (() => TipInternetTimeout, "1000 ms"),
            ["language"] = (() => TipLanguage, "auto"),
            ["theme"] = (() => TipTheme, "dark"),
            ["database_path"] = (() => TipDbFile, @".\scanner.db"),
            ["auto_graph_pinned"] = (() => TipAutoGraphPinned, "false"),
            ["network_graphs_enabled"] = (() => TipEnableNetworkGraphs, "true"),
            ["graphs_enabled"] = (() => TipEnableGraphs, "true"),
            ["graph_max_seconds"] = (() => TipGraphMaxTime, "300 s"),
            ["minimize_to_tray"] = (() => TipMinimizeToTray, "false"),
            ["check_for_updates"] = (() => TipCheckForUpdates, "true"),
            ["verbose_log"] = (() => TipVerboseLog, "false"),
            ["show_offline_db"] = (() => TipShowOfflineDb, "true"),
            ["accent_color"] = (() => TipAccentColor, "auto"),
            ["date_number_format"] = (() => TipDateNumberFormat, "auto"),
            ["compact_rows"] = (() => TipCompactRows, "false"),
            ["hidden_columns"] = (() => TipColumns, ""),
            ["repeat_scan_minutes"] = (() => TipRepeatScan, "0"),
            ["notify_scan_done"] = (() => TipNotifyScanDone, "false"),
            ["notify_new_device"] = (() => TipNotifyNewDevice, "false"),
            ["start_minimized"] = (() => TipStartMinimized, "false"),
        };
    public static string TipMergeDb => S(
        "Wählt die scanner.db einer anderen Programm-Instanz und führt deren Geräte in die eigene Datenbank zusammen (neuere Einträge gewinnen).",
        "Pick another instance's scanner.db and merge its devices into this database (newer entries win).");
    public static string TipResetDefaults => S(
        "Setzt alle Einstellungen sofort auf die Standardwerte zurück. Netzwerke, angeheftete IPs, Internetziele und Profile bleiben erhalten.",
        "Resets all settings to their defaults right away. Networks, pinned IPs, internet hosts and profiles are kept.");
    public static string TipClearDb => S(
        "Löscht alle gespeicherten Geräte samt Latenzverlauf und Portergebnissen, auf Wunsch auch das Scan-Archiv. Kann nicht rückgängig gemacht werden.",
        "Deletes all stored devices with their latency history and port results, optionally the scan archive too. Cannot be undone.");
    public static string TipStopRun => S(
        "Bricht die laufende Aufgabe sofort ab. Bereits gesammelte Ergebnisse bleiben sichtbar.",
        "Stops the running task right away. Whatever has been collected so far stays visible.");
    public static string ToolWolDesc => S(
        "Schickt ein Magic Packet, das ein schlafendes Gerät über das Netzwerk aufweckt. Nutze es, wenn ein Rechner oder NAS mit aktiviertem Wake on LAN ausgeschaltet oder im Ruhezustand ist.",
        "Sends a magic packet that wakes a sleeping device over the network. Use it when a computer or NAS with Wake on LAN enabled is switched off or asleep.");
    public static string ToolPortCheckDesc => S(
        "Prüft, welche TCP- und UDP-Ports eines Geräts Verbindungen annehmen, und zeigt den Dienst hinter jedem offenen Port. Nutze es, wenn du wissen willst, was ein Gerät im Netzwerk anbietet oder ob ein Dienst erreichbar ist.",
        "Checks which TCP and UDP ports on a device accept connections and shows the service behind each open port. Use it to see what a device offers on the network or whether a service is reachable.");
    public static string ToolDnsLookupDesc => S(
        "Sucht die IP-Adressen zu einem Namen oder den Namen zu einer IP-Adresse, über die DNS-Server dieses Rechners. Nutze es, wenn ein Name nicht wie erwartet aufgelöst wird oder du eine unbekannte Adresse zuordnen willst.",
        "Looks up the IP addresses behind a name, or the name behind an IP address, using the DNS servers of this computer. Use it when a name does not resolve as expected or you want to identify an unknown address.");
    public static string ToolPingRouteDesc => S(
        "Pingt ein Ziel dauerhaft mit Live-Diagramm oder zeigt die Router auf dem Weg von diesem Rechner zum Ziel. Nutze es, um die Stabilität einer Verbindung zu beurteilen oder die Stelle zu finden, an der Pakete verloren gehen.",
        "Pings a target continuously with a live graph, or traces the routers between this computer and the target. Use it to judge how stable a connection is or to find where packets get lost.");
    public static string ToolCalculatorDesc => S(
        "Berechnet Netz, Rundsendeadresse, Rechnerbereich und Maske für jede Adresse, sucht den Hersteller zu einer MAC-Adresse und die MAC zu einer IP in der Adresstabelle. Nutze es beim Planen von Netzen oder um ein Gerät zu identifizieren.",
        "Works out network, broadcast, host range and mask for any address, looks up the vendor of a MAC address and finds the MAC for an IP in the address table. Use it when planning networks or identifying a device.");
    public static string ToolCertificateDesc => S(
        "Verbindet sich per TLS mit einem Server und zeigt dessen Zertifikat mit Inhaber, Aussteller, Gültigkeit, Fingerabdruck und Kette. Nutze es, um das Ablaufdatum zu prüfen oder ob ein Dienst das richtige Zertifikat vorlegt.",
        "Connects to a server over TLS and shows its certificate: owner, issuer, validity, fingerprint and chain. Use it to check when a certificate expires or whether a service presents the right one.");
    public static string TipToolsNav => S(
        "Wähle ein Werkzeug; seine Beschreibung steht oben auf der Seite.",
        "Pick a tool; its description appears at the top of the page.");
    public static string TipWakeKnown => S(
        "Wählt ein Gerät aus der Liste der bekannten Geräte und trägt dessen MAC-Adresse ein.",
        "Picks a device from the known-devices list and fills in its MAC address.");
    public static string TipWakeCard => S(
        "Netzwerkkarte, über die das Magic Packet gesendet wird. Wähle die, die im selben Netzwerk wie das Gerät hängt.",
        "Network card the magic packet is sent from. Pick the one on the same network as the device.");
    public static string TipWakeSend => S(
        "Sendet das Magic Packet an die MAC-Adresse. Ist die IP des Geräts bekannt, wird bis zu 2 Minuten auf seine Antwort gewartet.",
        "Sends the magic packet to the MAC address. If the device's IP is known, waits up to 2 minutes for it to answer.");
    public static string TipWakeStop => S(
        "Hört auf, auf die Antwort des Geräts zu warten. Das Magic Packet ist bereits gesendet.",
        "Stops waiting for the device to answer. The magic packet has already been sent.");
    public static string TipWakeRecent => S(
        "Übernimmt die MAC-Adresse dieses früheren Weckvorgangs.",
        "Fills in the MAC address of this earlier wake-up.");
    public static string TipPortCheckStart => S(
        "Prüft die gewählten Ports des Ziels. Viele Ports dauern länger, UDP ist langsamer als TCP.",
        "Checks the chosen ports on the target. Many ports take longer, and UDP is slower than TCP.");
    public static string TipScopeCommon => S(
        "Prüft eine feste Liste bekannter Dienstports wie Web, SSH, Dateifreigabe und Datenbanken. Die schnellste Variante; TCP und UDP gelten hier nicht.",
        "Checks a built-in list of well-known service ports such as web, SSH, file sharing and databases. The quickest choice; the TCP and UDP boxes do not apply here.");
    public static string TipScopeTop1000 => S(
        "Prüft die Ports 1 bis 1000, auf denen die meisten Standarddienste laufen. Dauert länger als die üblichen Dienste.",
        "Checks ports 1 to 1000, where most standard services live. Takes longer than the common services.");
    public static string TipScopeRange => S(
        "Prüft jeden Port zwischen zwei Nummern, die du darunter angibst.",
        "Checks every port between two numbers you enter below.");
    public static string TipScopeList => S(
        "Prüft nur die Ports, die du darunter einträgst, durch Kommas getrennt.",
        "Checks only the ports you type below, separated by commas.");
    public static string TipPortTcp => S(
        "Versucht, zu jedem Port eine TCP-Verbindung aufzubauen. Eine Antwort heißt, dass dort ein Dienst lauscht. Gilt für Top 1000, Bereich und Liste.",
        "Tries a TCP connection to each port. An answer means a service is listening. Applies to Top 1000, Range and List.");
    public static string TipPortUdp => S(
        "Schickt an jeden Port eine UDP-Anfrage. Viele Dienste schweigen bei UDP, daher ist das Ergebnis unsicherer und der Lauf langsamer.",
        "Sends a UDP probe to each port. Many services stay silent on UDP, so results are less certain and the run is slower.");
    public static string TipPortRangeFrom => S(
        "Erster Port des Bereichs, von 1 bis 65535.",
        "First port of the range, from 1 to 65535.");
    public static string TipPortRangeTo => S(
        "Letzter Port des Bereichs, von 1 bis 65535 und nicht kleiner als der erste.",
        "Last port of the range, from 1 to 65535 and not lower than the first.");
    public static string TipPortCopy => S(
        "Kopiert die gefundenen offenen Ports samt Dienstnamen als Text in die Zwischenablage.",
        "Copies the open ports found, with their service names, to the clipboard as text.");
    public static string TipDnsAuto => S(
        "Entscheidet anhand der Eingabe: eine IP-Adresse wird rückwärts aufgelöst, alles andere als Name.",
        "Decides from what you typed: an IP address is looked up in reverse, anything else as a name.");
    public static string TipDnsForward => S(
        "Sucht die IP-Adressen, die zu einem Namen gehören.",
        "Finds the IP addresses that belong to a name.");
    public static string TipDnsReverse => S(
        "Sucht den Namen, der zu einer IP-Adresse gehört, samt weiteren Namen.",
        "Finds the name that belongs to an IP address, plus any aliases.");
    public static string TipDnsStart => S(
        "Schickt die Anfrage an die DNS-Server dieses Rechners und zeigt die Antwort.",
        "Sends the query to this computer's DNS servers and shows the answer.");
    public static string TipDnsCopy => S(
        "Kopiert die angezeigten Adressen oder Namen zeilenweise in die Zwischenablage.",
        "Copies the addresses or names shown above to the clipboard, one per line.");
    public static string TipToolPingTarget => S(
        "IP-Adresse oder Name des Ziels. Es wird gepingt, bis du die Aufgabe stoppst.",
        "IP address or hostname of the target. It is pinged until you stop it.");
    public static string TipToolPingInterval => S(
        "Pause zwischen zwei Pings in Sekunden.",
        "Pause between two pings, in seconds.");
    public static string TipToolPingPayload => S(
        "Größe der Nutzdaten je Ping in Byte. Große Pakete decken Probleme mit großen Frames auf, brauchen aber mehr Bandbreite.",
        "Size of the data in each ping, in bytes. Large packets expose problems with big frames but use more bandwidth.");
    public static string TipToolPingTimeout => S(
        "Wie lange auf eine Antwort gewartet wird, in Millisekunden. Danach zählt der Ping als verloren.",
        "How long to wait for a reply, in milliseconds. After that the ping counts as lost.");
    public static string TipToolPingStart => S(
        "Startet den Dauerping zum Ziel. Diagramm und Statistik aktualisieren sich live.",
        "Starts pinging the target continuously. The graph and the statistics update live.");
    public static string TipToolRouteTarget => S(
        "IP-Adresse oder Name des Ziels, zu dem der Weg verfolgt wird.",
        "IP address or hostname of the target whose route is traced.");
    public static string TipToolRouteStart => S(
        "Verfolgt die Router zwischen diesem Rechner und dem Ziel, bis zu 30 Stationen.",
        "Traces the routers between this computer and the target, up to 30 hops.");
    public static string TipRouteCopy => S(
        "Kopiert die Stationen als Text in die Zwischenablage, eine pro Zeile.",
        "Copies the hops to the clipboard as text, one per line.");
    public static string TipCertTarget => S(
        "Servername oder IP-Adresse, zu der verbunden wird. Der Name wird auch gegen das Zertifikat geprüft.",
        "Server name or IP address to connect to. The name is also checked against the certificate.");
    public static string TipCertPort => S(
        "Port des TLS-Dienstes, 443 für HTTPS.",
        "Port of the TLS service, 443 for HTTPS.");
    public static string TipCertStart => S(
        "Verbindet sich mit dem Server und liest dessen Zertifikatskette. Außer dem TLS-Handshake wird nichts gesendet.",
        "Connects to the server and reads its certificate chain. Nothing is sent besides the TLS handshake.");
    public static string TipCalcAddress => S(
        "Eine beliebige Adresse im Netz, zum Beispiel 192.168.1.20.",
        "Any address inside the network, for example 192.168.1.20.");
    public static string TipCalcArp => S(
        "IP-Adresse eines Geräts im eigenen Netz. In der Tabelle stehen nur Geräte, mit denen dieser Rechner kürzlich Kontakt hatte.",
        "IP address of a device in your own network. The table only holds devices this computer has talked to recently.");
    public static string TipCalcLookup => S(
        "Sucht die Adresse in der Adresstabelle dieses Rechners und zeigt die zugehörige MAC-Adresse.",
        "Looks the address up in this computer's address table and shows the MAC address it maps to.");
    public static string TipDialogCancel => S(
        "Schließt den Dialog, ohne etwas zu ändern.",
        "Closes the dialog without changing anything.");
    public static string TipDialogOk => S(
        "Schließt dieses Fenster.",
        "Closes this window.");
    public static string TipMessageYes => S(
        "Bestätigt die Frage und macht weiter.",
        "Confirms the question and carries on.");
    public static string TipMessageNo => S(
        "Verneint die Frage und schließt das Fenster, ohne etwas zu ändern.",
        "Declines the question and closes the window without changing anything.");
    public static string TipAboutProject => S(
        "Öffnet die Projektseite auf GitHub im Standardbrowser.",
        "Opens the project page on GitHub in your default browser.");
    public static string TipErrorOpenLog => S(
        "Öffnet die Fehlerprotokoll-Datei im Standard-Editor. Ausgegraut, solange noch kein Protokoll existiert.",
        "Opens the error log file in your default text editor. Greyed out while no log exists yet.");
    public static string TipErrorCopy => S(
        "Kopiert die technischen Details in die Zwischenablage, damit du sie in eine Nachricht oder einen Fehlerbericht einfügen kannst.",
        "Copies the technical details to the clipboard so you can paste them into a message or a bug report.");
    public static string TipUpdateLater => S(
        "Behält vorerst die installierte Version. Es wird nichts heruntergeladen.",
        "Keeps the installed version for now. Nothing is downloaded.");
    public static string TipUpdateNow => S(
        "Lädt die neue Version herunter, prüft ihre Signatur und startet das Programm zum Installieren neu.",
        "Downloads the new version, checks its signature and restarts the app to install it.");
    public static string TipFullScanConfirm => S(
        "Startet die Prüfung aller Ports jetzt. Das dauert lange und kann bei Überwachungssystemen Alarm auslösen.",
        "Starts the check of all ports now. It takes a long time and can set off alarms in monitoring systems.");
    public static string TipConfirmOption => S(
        "Zusatzoption, die nur für diese eine Aktion gilt.",
        "Extra option that applies to this one action only.");
    public static string TipConfirmDo => S(
        "Führt die in der Meldung beschriebene Aktion aus.",
        "Carries out the action described in the message.");
    public static string TipExportScope => S(
        "Legt fest, welche Zeilen in die Datei kommen: alle Geräte, die gerade angezeigten Zeilen oder nur die markierten.",
        "Chooses which rows go into the file: all devices, the rows currently shown, or only the selected ones.");
    public static string TipExportFormat => S(
        "Dateityp des Exports. TXT und CSV sind einfacher Text, HTML öffnet sich im Browser, JSON eignet sich für andere Programme.",
        "File type of the export. TXT and CSV are plain text, HTML opens in a browser, JSON suits other programs.");
    public static string TipExportProbePorts => S(
        "Prüft vor dem Export die Ports der Geräte, für die es noch kein Ergebnis gibt. Du kannst abbrechen und bekommst den Export mit dem bisher Gefundenen.",
        "Checks the ports of devices without a result yet before exporting. You can stop it and still get the export with what was found.");
    public static string TipExportAction => S(
        "Speichert den Export als neue Datei im Ausgabeordner und zeigt dir, wo sie liegt.",
        "Saves the export as a new file in the output folder and shows you where it went.");
    public static string TipConfigOverwrite => S(
        "Ersetzt die Einstellungsdatei durch die aktuellen Einstellungen. Deine Kommentare darin gehen verloren.",
        "Replaces the settings file with the current settings. Any comments you wrote in it are lost.");
    public static string TipBackupPwSet => S(
        "Verschlüsselt die Sicherung mit diesem Passwort. Leer lassen, dann lässt sie sich nur in diesem Windows-Konto wiederherstellen.",
        "Encrypts the backup with this password. Leave it empty and the backup can only be restored under this Windows account.");
    public static string TipBackupPwAsk => S(
        "Das Passwort, das beim Erstellen dieser Sicherung festgelegt wurde.",
        "The password that was set when this backup was created.");
    public static string TipBackupPwRepeat => S(
        "Gib dasselbe Passwort noch einmal ein, damit sich kein Tippfehler einschleicht.",
        "Type the same password again to rule out a typo.");
    public static string TipBackupConfirmSet => S(
        "Prüft beide Eingaben und erstellt die Sicherung mit diesem Passwort.",
        "Checks both entries and creates the backup with this password.");
    public static string TipBackupConfirmAsk => S(
        "Öffnet die Sicherung mit diesem Passwort. Ist es falsch, kannst du es erneut versuchen.",
        "Opens the backup with this password. If it is wrong you can try again.");
    public static string TipMergeCancel => S(
        "Bricht das Zusammenführen ab. Aus der anderen Datenbank wird nichts übernommen.",
        "Stops the merge. Nothing from the other database is added.");
    public static string TipPingCount => S(
        "Pings, die pro Host und Scan gesendet werden. Mehr Pings glätten den Durchschnitt, verlängern aber jeden Scan.",
        "Pings sent to each host per scan. More pings smooth out the average but make every scan take longer.");
    public static string TipScanButton => S(
        "Startet einen Scan Ihrer Netzwerke und angehefteten IPs. Während ein Scan läuft, bricht dieselbe Taste ihn ab.",
        "Starts a scan of your networks and pinned IPs. While a scan is running, the same button stops it.");
    public static string TipArchiveButton => S(
        "Öffnet das Archiv gespeicherter Scanläufe. Dort lassen sich Läufe vergleichen, sichern und alte aufräumen.",
        "Opens the archive of saved scan runs. There you can compare runs, back them up and clean up old ones.");
    public static string TipLogButton => S(
        "Öffnet das Programmprotokoll. Einträge lassen sich nach Bereich und Stufe filtern, das hilft bei der Fehlersuche.",
        "Opens the application log. Entries can be filtered by area and level, which helps when tracking down a problem.");
    public static string TipFirstRunUpdates => S(
        "Fragt bei jedem Programmstart bei GitHub nach einer neueren Version. Lässt sich später in den Einstellungen ändern.",
        "Asks GitHub for a newer version whenever the app starts. You can change this later in the settings.");
    public static string TipFirstRunInternet => S(
        "Misst die Latenz zu den Internetzielen in der Seitenleiste. Lässt sich später in den Einstellungen abschalten.",
        "Measures the latency to the internet hosts listed in the sidebar. You can turn it off later in the settings.");
    public static string TipFirstRunScan => S(
        "Übernimmt die beiden Auswahlen oben und startet den ersten Scan des erkannten Netzwerks.",
        "Applies the two choices above and starts the first scan of the detected network.");
    public static string TipFirstRunSettings => S(
        "Öffnet die Einstellungen, damit Sie Netzwerke und Scan-Optionen vor dem ersten Scan anpassen können.",
        "Opens the settings so you can adjust networks and scan options before the first scan.");
    public static string TipFirstRunDismiss => S(
        "Blendet diese Willkommenskarte aus und zeigt sie beim nächsten Start nicht mehr.",
        "Hides this welcome card and does not show it again at the next start.");
    public static string TipFilterBox => S(
        "Filtert die Tabelle beim Tippen. Mehrere Wörter müssen alle passen. Mit ip:, mac:, name:, vendor: oder port: wird nur eine Spalte durchsucht.",
        "Filters the table as you type. Several words must all match. Prefixes such as ip:, mac:, name:, vendor: or port: search a single column only.");
    public static string TipPillOnline => S(
        "Zeigt nur Geräte, die gerade online sind. Wirkt zusammen mit den anderen Filtern.",
        "Shows only devices that are online right now. Works together with the other filters.");
    public static string TipPillPinned => S(
        "Zeigt nur angeheftete IP-Adressen. Wirkt zusammen mit den anderen Filtern.",
        "Shows only pinned IP addresses. Works together with the other filters.");
    public static string TipPillUnknown => S(
        "Zeigt nur Geräte, die nicht als bekannt erkannt wurden. Wirkt zusammen mit den anderen Filtern.",
        "Shows only devices that were not recognized as known. Works together with the other filters.");
    public static string TipManualExport => S(
        "Lässt Sie wählen, was exportiert wird (alle oder gefilterte Zeilen) und in welchem Format, und speichert dann eine Datei.",
        "Lets you choose what to export (all or filtered rows) and in which format, then saves a file.");
    public static string TipCopyView => S(
        "Öffnet ein Menü, um die aktuell sichtbaren Tabellenzeilen in die Zwischenablage zu kopieren.",
        "Opens a menu to copy the table rows currently shown to the clipboard.");
    public static string TipExportsHeader => S(
        "Blendet die Export-Einstellungen in der Seitenleiste ein oder aus.",
        "Shows or hides the export settings in the sidebar.");
    public static string TipNetworksHeader => S(
        "Blendet die Liste der zu scannenden Netzwerke ein oder aus. Dort lassen sich Netzwerke hinzufügen, bearbeiten und einzeln scannen.",
        "Shows or hides the list of networks to scan. There you can add, edit and scan single networks.");
    public static string TipInternetHeader => S(
        "Blendet die Ziele für die Internet-Latenzmessung ein oder aus.",
        "Shows or hides the hosts used to measure your internet latency.");
    public static string TipScanSubnetNow => S(
        "Scannt nur dieses Netzwerk, die übrigen bleiben unberührt.",
        "Scans only this network and leaves the others untouched.");
    public static string TipCopyValue => S(
        "Nur lesbarer Text. Ins Feld klicken, Strg+A und dann Strg+C kopiert den Wert.",
        "Read-only text. Click into it, press Ctrl+A and then Ctrl+C to copy the value.");
    public static string TipImportTarget => S(
        "Wohin die importierten Einträge kommen: in die Netzwerkliste, die angehefteten IPs oder die Internetziele.",
        "Where the imported entries go: the list of networks, the pinned IPs or the internet hosts.");
    public static string TipImportMode => S(
        "Anhängen behält die vorhandenen Einträge und fügt die neuen hinzu. Ersetzen verwirft zuerst die Einträge der gewählten Liste.",
        "Append keeps the current entries and adds the new ones. Replace first discards the entries of the chosen list.");
    public static string TipImportBrowse => S(
        "Wählt eine Datei und zeigt vorab, was sie enthält. Es ändert sich nichts, bis Sie importieren.",
        "Picks a file and shows what it contains. Nothing changes until you import.");
    public static string TipImportCommit => S(
        "Schreibt die angezeigten Einträge in die gewählte Liste. Verfügbar, sobald eine Datei eingelesen wurde.",
        "Writes the previewed entries into the chosen list. Available once a file has been read.");
    public static string TipNoticeAction => S(
        "Führt die zur Meldung gehörende Aktion aus: den Ergebnisordner öffnen oder die letzte Änderung rückgängig machen.",
        "Runs the action that belongs to this message: open the result folder or undo the last change.");
    public static string TipColorApply => S(
        "Übernimmt die im Feld eingegebene Hexfarbe (Format #RRGGBB).",
        "Applies the hex color typed in the field (format #RRGGBB).");
    public static string TipHexBox => S(
        "Farbe als Hexwert im Format #RRGGBB, zum Beispiel #3A7BD5.",
        "Color as a hex value in the form #RRGGBB, for example #3A7BD5.");
    public static string TipCellEditBox => S(
        "Der Anzeigename für dieses Gerät. Enter speichert, Esc verwirft die Eingabe.",
        "The name shown for this device. Enter saves it, Escape discards the change.");
    public static string TipCellEditOk => S(
        "Speichert den eingegebenen Namen für dieses Gerät.",
        "Saves the typed name for this device.");
    public static string TipEntryTarget => S(
        "Die Adresse dieses Eintrags, zum Beispiel ein Netzwerk in CIDR-Schreibweise, eine IP-Adresse oder ein Hostname.",
        "The address of this entry, for example a network in CIDR notation, an IP address or a host name.");
    public static string TipEntryName => S(
        "Anzeigename statt der Adresse. Jeder Name kann nur einmal vergeben werden.",
        "Display name shown instead of the address. Each name can be used only once.");
    public static string TipEntryDelete => S(
        "Entfernt diesen Eintrag. Ein zweiter Klick bestätigt, danach lässt sich das Entfernen über einen Hinweis rückgängig machen.",
        "Removes this entry. A second click confirms it, and a banner afterwards lets you undo the removal.");
    public static string TipEditClose => S(
        "Schließt diesen Editor und verwirft die Änderungen.",
        "Closes this editor and discards your changes.");
    public static string TipEditSave => S(
        "Speichert die Änderungen und schließt den Editor.",
        "Saves your changes and closes the editor.");
    public static string TipProfileName => S(
        "Name dieses Profils. Er darf weder Komma noch Semikolon enthalten.",
        "Name of this profile. It cannot contain commas or semicolons.");
    public static string TipProfileDelete => S(
        "Löscht dieses Profil sofort mit seinen Netzwerken, angehefteten IPs und Internetzielen, ohne Nachfrage.",
        "Deletes this profile at once, together with its networks, pinned IPs and internet hosts, without asking.");
    public static string TipBackToTable => S(
        "Schließt diese Ansicht und kehrt zur Gerätetabelle zurück.",
        "Closes this view and returns to the device table.");
    public static string TipRangeMinute => S(
        "Zeigt die Live-Latenz der letzten Minute aus dem Arbeitsspeicher.",
        "Shows the live latency of the last minute, taken from memory.");
    public static string TipRangeDay => S(
        "Zeigt den Latenzverlauf der letzten 24 Stunden aus der Gerätedatenbank. Braucht aktivierte Datenbank und eine bekannte MAC-Adresse.",
        "Shows the latency history of the last 24 hours from the known devices database. Needs the database enabled and a known MAC address.");
    public static string TipRangeWeek => S(
        "Zeigt den Latenzverlauf der letzten 7 Tage aus der Gerätedatenbank. Braucht aktivierte Datenbank und eine bekannte MAC-Adresse.",
        "Shows the latency history of the last 7 days from the known devices database. Needs the database enabled and a known MAC address.");
    public static string TipServiceFilter => S(
        "Beschränkt die Dienstliste auf Einträge, die diesen Text enthalten.",
        "Narrows the service list to entries that contain this text.");
    public static string TipArchiveFrom => S(
        "Erster Tag des angezeigten Zeitraums. Leer lassen, um beim ältesten Lauf zu beginnen.",
        "First day of the range to show. Leave empty to start at the oldest run.");
    public static string TipArchiveTo => S(
        "Letzter Tag des angezeigten Zeitraums. Leer lassen, um bis zum neuesten Lauf anzuzeigen.",
        "Last day of the range to show. Leave empty to include the newest run.");
    public static string TipArchiveApply => S(
        "Listet nur Läufe innerhalb der gewählten Tage. Zeitraum löschen betrifft genau diese Läufe.",
        "Lists only the runs inside the chosen days. Delete range acts on exactly these runs.");
    public static string TipArchiveClear => S(
        "Leert beide Datumsfelder und listet wieder alle gespeicherten Läufe.",
        "Clears both dates and lists every stored run again.");
    public static string TipCompareToggle => S(
        "Blendet den Vergleich zweier Läufe ein oder aus: welche Geräte neu, verschwunden oder in der Latenz verändert sind.",
        "Shows or hides the comparison of two runs: which devices appeared, disappeared or changed latency.");
    public static string TipArchiveStartScan => S(
        "Das Archiv ist leer. Startet einen Scan, dessen Ergebnis danach im Archiv erscheinen kann.",
        "The archive is empty. Starts a scan whose result can then appear in the archive.");
    public static string TipCompareRunA => S(
        "Der frühere Lauf, gegen den verglichen wird.",
        "The earlier run to compare against.");
    public static string TipCompareRunB => S(
        "Der spätere Lauf, der mit Lauf A verglichen wird.",
        "The later run, compared with run A.");
    public static string TipComparePercent => S(
        "Ein Gerät gilt als verändert, wenn seine Latenz um mehr als diesen Prozentwert abweicht. Standard: 50.",
        "A device counts as changed when its latency differs by more than this percentage. Default: 50.");
    public static string TipCompareMinMs => S(
        "Kleinere Latenzunterschiede als diese Millisekunden werden ignoriert, egal wie groß der Prozentwert ist. Standard: 10.",
        "Latency differences smaller than this many milliseconds are ignored, however large the percentage. Default: 10.");
    public static string TipCompareCopy => S(
        "Kopiert den Vergleich als Text in die Zwischenablage.",
        "Copies the comparison to the clipboard as text.");
    public static string TipCompareJson => S(
        "Speichert den Vergleich als JSON-Datei zur Weiterverarbeitung.",
        "Saves the comparison as a JSON file for further processing.");
    public static string TipCompareText => S(
        "Speichert den Vergleich als gut lesbare Textdatei.",
        "Saves the comparison as an easy to read text file.");
    public static string TipArchiveDeleteRun => S(
        "Löscht den gewählten Lauf nach einer Bestätigung endgültig.",
        "Permanently deletes the selected run after a confirmation.");
    public static string TipArchiveDeleteRange => S(
        "Löscht nach einer Bestätigung alle Läufe im angezeigten Zeitraum endgültig.",
        "Permanently deletes all runs in the shown date range after a confirmation.");
    public static string TipArchiveDays => S(
        "Alter in Tagen (1 bis 36500), ab dem Verdichten und Bereinigen greifen.",
        "Age in days, from 1 to 36500, used by Compact and Purge.");
    public static string TipArchiveCompact => S(
        "Fasst ältere Läufe desselben Netzwerks und Tags zu einem zusammen. Die Gerätehistorie bleibt, Einzelheiten der Läufe nicht. Eine Rückfrage nennt vorher die Zahl.",
        "Merges older runs of the same network and day into one. Device history stays, per-run detail does not. A confirmation first shows how many runs are affected.");
    public static string TipArchivePurge => S(
        "Löscht nach einer Bestätigung alle Läufe, die älter als die angegebenen Tage sind, endgültig.",
        "Permanently deletes all runs older than the given number of days after a confirmation.");
    public static string TipArchiveImport => S(
        "Liest Läufe aus einer Sicherungsdatei ins Archiv ein. Bereits vorhandene Läufe werden übersprungen.",
        "Reads runs from a backup file into the archive. Runs that already exist are skipped.");
    public static string TipArchiveBackup => S(
        "Schreibt eine Sicherung des gesamten Archivs in den Ordner ArchiveBackups im Ausgabeordner.",
        "Writes a backup of the whole archive into the ArchiveBackups folder inside the output folder.");
    public static string TipLogOpenFile => S(
        "Öffnet die Protokolldatei im Standardprogramm für Textdateien.",
        "Opens the raw log file in your default app for text files.");
    public static string TipLogArea => S(
        "Zeigt nur Einträge eines einzelnen Bereichs.",
        "Shows only the entries of a single area.");
    public static string TipLogLevel => S(
        "Zeigt nur Einträge der gewählten Stufe.",
        "Shows only the entries of the chosen level.");
    public static string TipLogSearch => S(
        "Beschränkt die Liste auf Einträge, die diesen Text enthalten.",
        "Narrows the list to entries that contain this text.");
    public static string TipLogRow => S(
        "Ein Klick auf den Titel klappt den Eintrag auf. Die Schaltfläche darin kopiert den vollständigen Text.",
        "Click the title to expand the entry. The button inside copies its full text.");
    public static string TipSettingsClose => S(
        "Schließt die Einstellungen. Änderungen gelten sofort, es muss nichts gespeichert werden.",
        "Closes the settings. Changes apply immediately, so there is nothing to save.");
    public static string TipNavScan => S(
        "Threads, Scan-Tempo, Wiederholung, Benachrichtigungen und erweiterte Netzwerkoptionen.",
        "Threads, scan speed, repeat scans, notifications and advanced network options.");
    public static string TipNavDisplay => S(
        "Akzentfarbe, Zahlen- und Datumsformat, kompakte Zeilen und welche Spalten die Gerätetabelle zeigt.",
        "Accent color, number and date format, compact rows and which columns the device table shows.");
    public static string TipNavInternet => S(
        "Die Internet-Latenzmessung ein- oder ausschalten und ihr Zeitlimit festlegen.",
        "Turn the internet latency measurement on or off and set its timeout.");
    public static string TipNavGraphs => S(
        "Latenzgrafiken für Geräte und Netzwerk sowie das automatische Anheften von Grafiken.",
        "Latency graphs for devices and the network, and automatic graph pinning.");
    public static string TipNavData => S(
        "Ordner, Gerätedatenbank, Aufbewahrung, Archivsicherungen, Konfiguration exportieren und importieren sowie Listenimport.",
        "Folders, the known devices database, retention, archive backups, config export and import, and list import.");
    public static string TipNavApp => S(
        "Verhalten im Infobereich, Update-Prüfung und ausführliche Protokollierung.",
        "Tray behavior, update checks and detailed logging.");
    public static string TipNavAbout => S(
        "Version, Hersteller und Lizenzangaben.",
        "Version, manufacturer and license information.");
    public static string TipNavHelp => S(
        "Hinweise zur Bedienung der wichtigsten Funktionen.",
        "Usage hints for the main features.");
    public static string TipAdvancedNetwork => S(
        "Blendet Port-Scan-Zeitlimits, Parallelität, SNMP-Community und eigene Ports ein oder aus. Die Standardwerte passen meistens.",
        "Shows or hides port scan timeouts, parallelism, the SNMP community and custom ports. The defaults suit most networks.");
    public static string TipColGroup => S(
        "Zeigt die Spalte mit der erkannten Gerätegruppe.",
        "Shows the column with the detected device group.");
    public static string TipColAvg => S(
        "Zeigt die durchschnittliche Antwortzeit der Pings.",
        "Shows the average response time of the pings.");
    public static string TipColMin => S(
        "Zeigt die kürzeste gemessene Antwortzeit.",
        "Shows the shortest measured response time.");
    public static string TipColMax => S(
        "Zeigt die längste gemessene Antwortzeit.",
        "Shows the longest measured response time.");
    public static string TipColLast => S(
        "Zeigt die zuletzt gemessene Antwortzeit.",
        "Shows the most recently measured response time.");
    public static string TipColLoss => S(
        "Zeigt den Anteil der Pings, auf die keine Antwort kam.",
        "Shows the share of pings that got no answer.");
    public static string TipColJitter => S(
        "Zeigt, wie stark die Antwortzeit von Ping zu Ping schwankt.",
        "Shows how much the response time varies from ping to ping.");
    public static string TipColProgress => S(
        "Zeigt den Fortschritt der Pings je Host, solange ein Scan läuft.",
        "Shows the ping progress of each host while a scan is running.");
    public static string TipColMac => S(
        "Zeigt die Hardware-Adresse jedes Geräts. Sie ist nur für Geräte im lokalen Netzwerk verfügbar.",
        "Shows the hardware address of each device. It is only available for devices on your local network.");
    public static string TipColVendor => S(
        "Zeigt den Hersteller, der sich aus der MAC-Adresse ergibt.",
        "Shows the manufacturer derived from the MAC address.");
    public static string TipBrowseOutput => S(
        "Wählt den Ordner, in dem Scan-Berichte gespeichert werden.",
        "Choose the folder where scan reports are saved.");
    public static string TipBrowseDb => S(
        "Wählt die Datei der Gerätedatenbank.",
        "Choose the file of the known devices database.");
    public static string TipBrowseConf => S(
        "Wählt den Ordner, in dem die Konfigurationsdatei liegt.",
        "Choose the folder that holds the settings file.");
    public static string TipCheckUpdatesNow => S(
        "Fragt jetzt bei GitHub nach einer neueren Version, unabhängig von der Prüfung beim Start.",
        "Asks GitHub for a newer version right now, independent of the check at startup.");
    // ── Updates ──
    public static string UpdateTitle => S("Update verfügbar", "Update available");
    public static string UpdateBodyFmt => S(
        "Version {0} ist verfügbar (installiert: {1}).\n\nDie App startet nach dem Update neu.",
        "Version {0} is available (installed: {1}).\n\nThe app will restart after updating.");
    public static string UpdateBody(string newVersion, string currentVersion)
        => string.Format(UpdateBodyFmt, newVersion, currentVersion);
    public static string UpdateNow => S("Jetzt aktualisieren", "Update now");
    public static string UpdateLater => S("Später", "Later");
    public static string UpdateCheckFailed => S(
        "Update-Prüfung fehlgeschlagen (keine Verbindung oder GitHub nicht erreichbar).",
        "Update check failed (no connection, or GitHub could not be reached).");
    public static string UpdateUnsignedFmt => S(
        "Version {0} ist verfügbar, aber nicht signiert. Sie wird nicht automatisch installiert.",
        "Version {0} is available but not signed, so it is not installed automatically.");
    public static string UpdateUnsigned(string newVersion) => string.Format(UpdateUnsignedFmt, newVersion);
    public static string OpenDownloadPage => S("Download-Seite öffnen", "Open download page");
    public static string UpdateFailed => S(
        "Das Update konnte nicht installiert werden. Bitte später erneut versuchen.",
        "The update could not be installed. Please try again later.");

    public static string UpdateDownloadFailed => S(
        "Die neue Version konnte nicht heruntergeladen werden. Prüfe die Internetverbindung.",
        "The new version could not be downloaded. Check the internet connection.");
    public static string UpdateFolderReadOnly => S(
        "Der Programmordner ist schreibgeschützt.",
        "The program folder is read-only.");
    public static string UpdateSignatureFailed => S(
        "Die heruntergeladene Datei ist nicht vom Herausgeber signiert und wurde verworfen.",
        "The downloaded file is not signed by the publisher and was discarded.");

    public static string ScanError => S("Scan-Fehler", "Scan error");
    public static string UnexpectedError => S("Unerwarteter Fehler", "Unexpected error");
    public static string DbExportFailedTitle => S("Datenbank-Export fehlgeschlagen", "Database export failed");
    public static string DbImportFailedTitle => S("Datenbank-Import fehlgeschlagen", "Database import failed");
    public static string MergeFailedTitle => S("Zusammenführen fehlgeschlagen", "Merging databases failed");
    public static string ConfImportFailedTitle => S("Einstellungen konnten nicht eingelesen werden", "Settings could not be imported");
    public static string ConfExportFailedTitle => S("Einstellungen konnten nicht gespeichert werden", "Settings could not be saved");
    public static string ListImportFailedTitle => S("Liste konnte nicht eingelesen werden", "List could not be imported");
    public static string OpenFolderFailedTitle => S("Ordner konnte nicht geöffnet werden", "Folder could not be opened");
    public static string ClearDbFailedTitle => S("Datenbank konnte nicht geleert werden", "Database could not be cleared");
    public static string SavedFmt => S("Gespeichert: {0}", "Saved: {0}");
    public static string OpenFolder => S("Ordner öffnen", "Open folder");
    public static string RowsCopiedFmt => S("{0} Zeilen kopiert", "{0} rows copied");
    public static string RowCopiedOne => S("1 Zeile kopiert", "1 row copied");
    public static string RowsCopied(int n) => n == 1 ? RowCopiedOne : string.Format(RowsCopiedFmt, n);
    public static string NothingToCopy => S("Nichts zu kopieren", "Nothing to copy");
    public static string CopiedOne => S("Kopiert", "Copied");
    public static string CopyFailedMsg => S(
        "Das Kopieren ist fehlgeschlagen, weil ein anderes Programm die Zwischenablage belegt. Warte kurz und kopiere erneut.",
        "Copying failed because another program is using the clipboard. Wait a moment and copy again.");
    public static string SavePortsFailedTitle => S("Ports konnten nicht gespeichert werden", "Ports could not be saved");
    public static string LargeRange => S("Großer Bereich", "Large range");
    public static string LargeRangeFmt => S(
        "/{0} umfasst {1} Netzwerke (~{2:N0} Hosts). Das kann sehr lange dauern. Fortfahren?",
        "/{0} spans {1} networks (~{2:N0} hosts). This can take very long. Continue?");
    public static string LargeRangeMsg(int cidr, int subnets, long hosts)
        => string.Format(LargeRangeFmt, cidr, subnets, hosts);

    public static string RepeatedErrorsFmt => S(
        "{0}\n\nDieser Fehler tritt wiederholt auf. Weitere Fehlermeldungen werden unterdrückt (weiterhin protokolliert). Programm jetzt beenden?",
        "{0}\n\nThis error keeps recurring. Further error popups will be suppressed (still logged). Exit the program now?");
    public static string RepeatedErrorsMsg(string message) => string.Format(RepeatedErrorsFmt, message);

    public static string InvalidSubnet => S(
        "Ungültige Basis-IP. Bitte eine gültige IPv4-Adresse eingeben, z.B. 192.168.1.0.",
        "Invalid base IP. Please enter a valid IPv4 address, e.g. 192.168.1.0.");
    public static string RangeTooLargeFmt => S(
        "/{0} ist zu groß (mehr als {1:N0} Hosts). Kleinstmögliche Maske ist /16.",
        "/{0} is too large (more than {1:N0} hosts). The smallest allowed mask is /16.");
    public static string RangeTooLargeMsg(int cidr)
        => string.Format(RangeTooLargeFmt, cidr, (long)IpScanner.Core.Net.Ipv4.MaxPrefixes * IpScanner.Core.Net.Ipv4.HostsPerSubnet);

    // ── Log ──
    public static string LogTitle => S("Protokoll", "Log");
    public static string VerboseLogLabel => S("Ausführliches Protokoll", "Verbose logging");
    public static string TipVerboseLog => S(
        "Schreibt zusätzlich Info- und Debug-Zeilen ins Protokoll. Hilft bei der Fehlersuche, lässt die Datei aber schneller wachsen.",
        "Also writes information and debug rows to the log. Helps with troubleshooting but makes the file grow faster.");
    public static string LogFilterArea => S("Bereich", "Area");
    public static string LogFilterLevel => S("Stufe", "Level");
    public static string LogAllAreas => S("Alle Bereiche", "All areas");
    public static string LogAllLevels => S("Alle Stufen", "All levels");
    public static string LogSearchLabel => S("Protokoll durchsuchen", "Search the log");
    public static string LogRowsFmt => S("{0} von {1} Zeilen", "{0} of {1} rows");
    public static string LogRowsText(int shown, int total) => string.Format(LogRowsFmt, shown, total);
    public static string LogNoMatch => S("Keine passenden Protokollzeilen.", "No log rows match.");
    public static string LogEmpty => S(
        "Noch keine Protokolleinträge. Warnungen und Fehler werden hier festgehalten; schalte in den Einstellungen das ausführliche Protokoll ein, um auch gewöhnliche Ereignisse zu sehen.",
        "No log entries yet. Warnings and errors are recorded here; turn on verbose logging in the settings to also see routine events.");
    public static string LogOpenFile => S("Protokolldatei öffnen", "Open log file");
    public static string LogCopyRow => S("Zeile kopieren", "Copy row");
    public static string LogLevelError => S("Fehler", "Error");
    public static string LogLevelWarn => S("Warnung", "Warning");
    public static string LogLevelInfo => S("Info", "Info");
    public static string LogLevelDebug => S("Debug", "Debug");
    public static string LogAreaApp => S("Allgemein", "General");
    public static string LogAreaPorts => S("Ports", "Ports");
    public static string LogAreaUpdate => S("Updates", "Updates");
    public static string LogAreaUi => S("Oberfläche", "User interface");
    public static string LogAreaCli => S("Kommandozeile", "Command line");
    public static string LogTitleDbLocked => S(
        "Datenbank ist von einem anderen Prozess gesperrt", "Database is locked by another process");
    public static string LogTitleNoRoute => S("Keine Route zum Gerät", "No route to the host");
    public static string LogTitleAccessDenied => S(
        "Zugriff verweigert - evtl. fehlen Administratorrechte", "Access denied - administrator rights may be missing");
    public static string LogTitleTimedOut => S("Zeitüberschreitung", "The operation timed out");
    public static string LogTitleRefused => S("Das Gerät hat die Verbindung abgelehnt", "The host refused the connection");
    public static string LogTitleNameNotFound => S("Der Hostname ließ sich nicht auflösen", "Host name could not be resolved");
    public static string LogTitleFileInUse => S("Datei wird von einem anderen Programm benutzt", "File is in use by another program");
    public static string LogTitleDiskFull => S("Datenträger ist voll", "Disk is full");
    public static string LogTitleDbReadOnly => S("Datenbank ist schreibgeschützt", "Database is read-only");
    public static string LogTitleClipboardBusy => S("Zwischenablage ist belegt", "Clipboard is busy");
    public static string LogTitleDiskIo => S("Lese- oder Schreibfehler auf dem Datenträger", "Disk read or write error");
    public static string LogTitlePortCollection => S("Die offenen Ports ließen sich nicht sammeln", "Open ports could not be collected");
    public static string LogTitleUpdateSignature => S("Die Signaturprüfung des Updates ist fehlgeschlagen", "The update signature check failed");
    public static string LogTitleStartupScan => S("Der Scan beim Start ist fehlgeschlagen", "The startup scan failed");

    // Packet reader (driver) notice, help section and capability matrix.
    public static string DriverBarText => S(
        "Für halboffene Prüfung, Sonderformen und den echten Fingerabdruck wird ein Paketleser gebraucht, den Windows nicht mitbringt.",
        "Half-open scanning, special scan forms and the real fingerprint need a packet reader that Windows does not ship.");
    public static string DriverBarAdmin => S(
        "Der Paketleser ist installiert, funktioniert aber nur mit Administratorrechten. Starte den Scanner als Administrator, um diese Funktionen zu nutzen.",
        "The packet reader is installed but only works with administrator rights. Start the scanner as administrator to enable those functions.");
    public static string DriverBarStopped => S(
        "Der Paketleser ist installiert, läuft aber nicht. Rohe Scan-Arten sind nicht verfügbar, bis sein Dienst gestartet ist.",
        "The packet reader is installed but not running. Raw scan types are unavailable until its service is started.");
    public static string DriverBarFor(string? reason) => reason switch
    {
        PacketDriver.ReasonAdmin => DriverBarAdmin,
        PacketDriver.ReasonStopped => DriverBarStopped,
        _ => DriverBarText,
    };
    public static string DriverDetails => S("Details", "Details");
    public static string DriverGet => S("Npcap holen", "Get Npcap");
    public static string TipDriverGet => S(
        "Öffnet die offizielle Npcap-Seite im Browser. Die App lädt und installiert nichts.",
        "Opens the official Npcap page in your browser. The app downloads and installs nothing.");
    public static string TipDriverDetails => S(
        "Zeigt, welche Scan-Arten verfügbar sind und warum manche es nicht sind.",
        "Shows which scan types are available and why some are not.");
    public static string TipDriverClose => S("Blendet diesen Hinweis bis zum nächsten Start aus.", "Hides this notice until the next start.");
    public static string DriverHelpTitle => S("Paketleser (Npcap)", "Packet reader (Npcap)");
    public static string DriverLabel => S("Paketleser", "Packet reader");
    public static string DriverInstalledVersionFmt => S("Installiert, Version {0}", "Installed, version {0}");
    public static string DriverInstalledVersion(string version) => string.Format(DriverInstalledVersionFmt, version);
    public static string DriverInstalled => S("Installiert", "Installed");
    public static string DriverNotInstalled => S("Nicht installiert", "Not installed");
    public static string DriverNeedsAdmin => S("Installiert, braucht Administratorrechte", "Installed, needs administrator rights");
    public static string DriverStopped => S("Installiert, läuft nicht", "Installed, not running");
    public static string CapMatrixTitle => S("Scan-Arten auf diesem Rechner", "Scan types on this computer");
    public static string CapAvailable => S("Verfügbar", "Available");
    public static string CapUnavailable => S("Nicht verfügbar", "Not available");
    public static string CapColType => S("Scan-Art", "Scan type");
    public static string CapColStatus => S("Status", "Status");
    public static string CapColReason => S("Grund", "Reason");
    public static string CapReasonDriver => S("Paketleser fehlt", "Packet reader missing");
    public static string CapReasonStopped => S("Paketleser läuft nicht", "Packet reader not running");
    public static string CapReasonAdmin => S("Administratorrechte fehlen", "Administrator rights missing");
    public static string CapReasonIpv6 => S("IPv6 wird von diesem Modus nicht unterstützt", "IPv6 is not supported by this mode");
    public static string CapGroupPortScan => S("Portscan", "Port scan");
    public static string CapGroupDiscovery => S("Hosterkennung", "Host discovery");
    public static string CapGroupOs => S("Betriebssystem", "Operating system");
    public static string CapUnavailableTipFmt => S("Nicht verfügbar: {0}", "Not available: {0}");
    public static string CapUnavailableTip(string reason) => string.Format(CapUnavailableTipFmt, reason);

    public static string CapNameTcpConnectScan => S("TCP-Verbindungsscan", "TCP connect scan");
    public static string CapNameSynScan => S("Halboffener SYN-Scan", "Half-open SYN scan");
    public static string CapNameFinScan => S("FIN-Scan", "FIN scan");
    public static string CapNameNullScan => S("NULL-Scan", "NULL scan");
    public static string CapNameXmasScan => S("Xmas-Scan", "Xmas scan");
    public static string CapNameAckScan => S("ACK-Scan", "ACK scan");
    public static string CapNameWindowScan => S("Window-Scan", "Window scan");
    public static string CapNameMaimonScan => S("Maimon-Scan", "Maimon scan");
    public static string CapNameUdpScan => S("UDP-Scan", "UDP scan");
    public static string CapNameArpSweep => S("ARP-Suche", "ARP sweep");
    public static string CapNameIcmpEcho => S("ICMP-Echo", "ICMP echo");
    public static string CapNameIcmpTimestamp => S("ICMP-Zeitstempel", "ICMP timestamp");
    public static string CapNameIcmpAddressMask => S("ICMP-Adressmaske", "ICMP address mask");
    public static string CapNameTcpConnectPing => S("TCP-Verbindungs-Ping", "TCP connect ping");
    public static string CapNameTcpSynPing => S("TCP-SYN-Ping", "TCP SYN ping");
    public static string CapNameTcpAckPing => S("TCP-ACK-Ping", "TCP ACK ping");
    public static string CapNameUdpPing => S("UDP-Ping", "UDP ping");
    public static string CapNameListScan => S("Listenscan", "List scan");
    public static string CapNameSkipDiscovery => S("Erkennung überspringen", "Skip discovery");
    public static string CapNameOsEstimate => S("Betriebssystem-Schätzung", "OS estimate");
    public static string CapNameOsFingerprint => S("Betriebssystem-Fingerabdruck", "OS fingerprint");

    public static string CapName(Capability id) => id switch
    {
        Capability.TcpConnectScan => CapNameTcpConnectScan,
        Capability.SynScan => CapNameSynScan,
        Capability.FinScan => CapNameFinScan,
        Capability.NullScan => CapNameNullScan,
        Capability.XmasScan => CapNameXmasScan,
        Capability.AckScan => CapNameAckScan,
        Capability.WindowScan => CapNameWindowScan,
        Capability.MaimonScan => CapNameMaimonScan,
        Capability.UdpScan => CapNameUdpScan,
        Capability.ArpSweep => CapNameArpSweep,
        Capability.IcmpEcho => CapNameIcmpEcho,
        Capability.IcmpTimestamp => CapNameIcmpTimestamp,
        Capability.IcmpAddressMask => CapNameIcmpAddressMask,
        Capability.TcpConnectPing => CapNameTcpConnectPing,
        Capability.TcpSynPing => CapNameTcpSynPing,
        Capability.TcpAckPing => CapNameTcpAckPing,
        Capability.UdpPing => CapNameUdpPing,
        Capability.ListScan => CapNameListScan,
        Capability.SkipDiscovery => CapNameSkipDiscovery,
        Capability.OsEstimate => CapNameOsEstimate,
        Capability.OsFingerprint => CapNameOsFingerprint,
        _ => id.ToString(),
    };

    public static string CapGroupName(CapabilityGroup g) => g switch
    {
        CapabilityGroup.PortScan => CapGroupPortScan,
        CapabilityGroup.Discovery => CapGroupDiscovery,
        _ => CapGroupOs,
    };

    public static string CapReasonText(string? reason) => reason switch
    {
        PacketDriver.ReasonMissing => CapReasonDriver,
        PacketDriver.ReasonAdmin => CapReasonAdmin,
        PacketDriver.ReasonStopped => CapReasonStopped,
        PacketDriver.ReasonIpv6 => CapReasonIpv6,
        "transport-missing" => PortTransportMissing,
        _ => reason ?? "",
    };

    public static string StatusListed => S("Aufgelistet", "Listed");
    public static string FoundBy => S("Gefunden per", "Found by");
    public static string TipColMethod => S(
        "Zeigt, mit welchem Erkennungsverfahren der Host gefunden wurde.",
        "Shows which discovery method found the host.");

    public static string DiscoveryHeader => S("Host-Erkennung", "Host discovery");
    public static string TcpPingPortsLabel => S("Ports für TCP-Ping", "TCP ping ports");
    public static string UdpPingPortsLabel => S("Ports für UDP-Ping", "UDP ping ports");
    public static string TipTcpPingPorts => S(
        "Kommagetrennte Ports, auf denen der TCP-Ping einen Host anfragt (1 bis 65535, höchstens 16).",
        "Comma-separated ports the TCP ping tries on each host (1 to 65535, at most 16).");
    public static string TipUdpPingPorts => S(
        "Kommagetrennte Ports, an die der UDP-Ping seine Anfrage schickt (1 bis 65535, höchstens 16).",
        "Comma-separated ports the UDP ping sends its probe to (1 to 65535, at most 16).");
    public static string TipDiscArp => S(
        "Fragt per ARP im eigenen Netz, wer antwortet. Findet auch Hosts, die ICMP blockieren.",
        "Asks the local network by ARP who answers. Also finds hosts that block ICMP.");
    public static string TipDiscIcmp => S(
        "Klassischer Ping mit ICMP-Echo. Standardverfahren.",
        "Classic ping using ICMP echo. The default method.");
    public static string TipDiscIcmpTs => S(
        "Sendet eine ICMP-Zeitstempel-Anfrage. Braucht Administratorrechte.",
        "Sends an ICMP timestamp request. Needs administrator rights.");
    public static string TipDiscIcmpMask => S(
        "Sendet eine ICMP-Adressmasken-Anfrage. Braucht Administratorrechte.",
        "Sends an ICMP address mask request. Needs administrator rights.");
    public static string TipDiscTcp => S(
        "Baut zu den eingestellten Ports eine TCP-Verbindung auf. Findet Hosts, die ICMP blockieren, aber einen Dienst anbieten.",
        "Opens a TCP connection to the configured ports. Finds hosts that block ICMP but offer a service.");
    public static string TipDiscTcpSyn => S(
        "Sendet ein einzelnes SYN-Paket und wertet die Antwort aus. Braucht den Paketleser.",
        "Sends a single SYN packet and reads the answer. Needs the packet reader.");
    public static string TipDiscTcpAck => S(
        "Sendet ein einzelnes ACK-Paket und wertet die Antwort aus. Braucht den Paketleser.",
        "Sends a single ACK packet and reads the answer. Needs the packet reader.");
    public static string TipDiscUdp => S(
        "Schickt ein UDP-Paket an die eingestellten Ports. Eine Antwort oder ein ICMP-Fehler zeigt, dass der Host da ist.",
        "Sends a UDP packet to the configured ports. An answer or an ICMP error shows the host is there.");
    public static string TipDiscList => S(
        "Löst die Ziele nur auf und listet sie auf. Es wird kein Paket gesendet. Kann nicht mit anderen Verfahren kombiniert werden.",
        "Only resolves and lists the targets. No packet is sent. Cannot be combined with other methods.");
    public static string TipDiscSkip => S(
        "Behandelt jedes Ziel als erreichbar und analysiert alle direkt.",
        "Treats every target as up and analyzes all of them right away.");

    public static string TipDiscovery(Core.Scanner.Discovery.DiscoveryMode mode) => mode switch
    {
        Core.Scanner.Discovery.DiscoveryMode.Arp => TipDiscArp,
        Core.Scanner.Discovery.DiscoveryMode.IcmpEcho => TipDiscIcmp,
        Core.Scanner.Discovery.DiscoveryMode.IcmpTimestamp => TipDiscIcmpTs,
        Core.Scanner.Discovery.DiscoveryMode.IcmpMask => TipDiscIcmpMask,
        Core.Scanner.Discovery.DiscoveryMode.TcpConnect => TipDiscTcp,
        Core.Scanner.Discovery.DiscoveryMode.TcpSyn => TipDiscTcpSyn,
        Core.Scanner.Discovery.DiscoveryMode.TcpAck => TipDiscTcpAck,
        Core.Scanner.Discovery.DiscoveryMode.Udp => TipDiscUdp,
        Core.Scanner.Discovery.DiscoveryMode.ListScan => TipDiscList,
        _ => TipDiscSkip,
    };

    public static string OsFingerprintLabel => S("Aktiver Betriebssystem-Fingerabdruck", "Active OS fingerprint");
    public static string TipOsFingerprint => S(
        "Schickt beim Öffnen eines Geräts einzelne Testpakete an einen offenen und einen geschlossenen Port und vergleicht die Antwort mit einer kleinen eingebauten Tabelle. Braucht den Paketleser.",
        "When a device is opened, sends a few probe packets to an open and a closed port and compares the reply with a small built-in table. Needs the packet reader.");
    public static string OsFingerprintDegradeNote => S(
        "Ohne Paketleser schätzt die App das Betriebssystem nur aus TTL und offenen Ports.",
        "Without the packet reader the operating system is estimated from the TTL and open ports only.");
    public static string OsColumnHeader => S("Betriebssystem (Schätzung)", "OS (estimate)");
    public static string TipColOs => S(
        "Zeigt die geschätzte Betriebssystemfamilie mit Vertrauen in Prozent.",
        "Shows the estimated operating system family with a confidence percentage.");
    public static string OsFamilyWindows => S("Windows", "Windows");
    public static string OsFamilyUnix => S("Linux/Unix", "Linux/Unix");
    public static string OsFamilyNetwork => S("Netzwerkgerät", "Network device");
    public static string OsFamilyPrinter => S("Drucker", "Printer");
    public static string OsFamilyMedia => S("Mediengerät", "Media device");
    public static string OsEstimateFmt => S("{0} ~{1} % (Schätzung)", "{0} ~{1} % (estimate)");
    public static string OsFingerprintFmt => S("{0}, Fingerabdruck, Übereinstimmung {1} %, Sicherheit ~{2} %", "{0}, fingerprint, {1} % match, ~{2} % confidence");
    public static string OsHopsFmt => S("Hops: {0}", "hops: {0}");
    public static string OsTipNone => S(
        "Für eine Schätzung fehlen die Antwort-TTL und offene Ports.",
        "Not enough data for an estimate: the reply TTL and open ports are missing.");
    public static string OsTipHeuristic => S(
        "Nur eine Schätzung aus der Antwort-TTL und den offenen Ports.",
        "Only an estimate from the reply TTL and the open ports.");
    public static string OsTipFingerprint => S(
        "Nächster Treffer einer kleinen eingebauten Tabelle gängiger Systeme, mit der Übereinstimmung der beobachteten Werte. Nur eine Schätzung.",
        "Closest match in a small built-in table of common systems, with how well the observed values fit it. Only an estimate.");
    public static string ReportPartialNote => S("Unvollständig: der Scan wurde abgebrochen.", "Incomplete: the scan was cancelled.");
    public static string ReportNotShownFmt => S("Nicht angezeigt: {0} {1}", "Not shown: {0} {1}");
    public static string ReportChecksHeader => S("Prüfungen", "Checks");
    public static string ReportConflictsHeader => S("Auffälligkeiten", "Findings");
    public static string PresetQuick => S("Schnell", "Quick");
    public static string PresetThorough => S("Gründlich", "Thorough");
    public static string PresetPortsOnly => S("Nur Anschlüsse", "Ports only");
    public static string PresetQuiet => S("Leise", "Quiet");
    public static string PresetNameRequired => S("Bitte einen Namen eingeben.", "Enter a name for the preset.");
    public static string PresetNameTooLong => S("Der Name darf höchstens 40 Zeichen haben.", "The name can have at most 40 characters.");
    public static string PresetNameInvalidChars => S("Der Name darf weder | noch # noch ; enthalten.", "The name cannot contain | # or ;");
    public static string PresetNameTaken => S("Eine Vorlage mit diesem Namen gibt es schon.", "A preset with this name already exists.");
    public static string ScanPresetsHeader => S("Scan-Vorlagen", "Scan presets");
    public static string ScanPresetLabel => S("Vorlage", "Preset");
    public static string PresetCustom => S("Eigene Einstellungen", "Custom settings");
    public static string TipScanPreset => S("Setzt Zeitprofil, Erkennung, Ports und Diensterkennung in einem Schritt. Änderst du danach einen Wert, steht hier Eigene Einstellungen.", "Sets timing, discovery, ports and service detection in one step. If you change a value afterwards, this switches to Custom settings.");
    public static string PresetSaveButton => S("Als Vorlage speichern", "Save as preset");
    public static string TipPresetSave => S("Speichert die aktuellen Scan-Einstellungen unter einem Namen als eigene Vorlage (höchstens 50).", "Saves the current scan settings under a name as a preset of your own (at most 50).");
    public static string PresetDeleteButton => S("Vorlage löschen", "Delete preset");
    public static string TipPresetDelete => S("Löscht die gewählte eigene Vorlage. Die vier eingebauten lassen sich nicht löschen.", "Deletes the selected preset of your own. The four built-in ones cannot be deleted.");
    public static string PresetNameLabel => S("Name der Vorlage", "Preset name");
    public static string TipPresetName => S("Name für die neue Vorlage, höchstens 40 Zeichen, ohne | # ;", "Name for the new preset, at most 40 characters, without | # ;");
    public static string ScanPortsLabel => S("Zu prüfende Ports", "Ports to scan");
    public static string PortsStandardOption => S("Standard (1000 häufigste)", "Standard (top 1000)");
    public static string PortsFastOption => S("Schnell (100 häufigste)", "Fast (top 100)");
    public static string PortsAllOption => S("Alle (1 bis 65535)", "All (1 to 65535)");
    public static string PortsListOption => S("Eigene Liste", "Custom list");
    public static string TipScanPorts => S("Welche Ports ein Scan prüft. Der ganze Bereich läuft nur über die Befehlszeile, die App sammelt dann die Standard-Ports.", "Which ports a scan checks. The full range runs from the command line only; the app collects the standard ports instead.");
    public static string TipScanPortsList => S("Ports wie 22,80,8000-9000 oder top:200.", "Ports such as 22,80,8000-9000 or top:200.");
    public static string PortsListInvalid => S("Ungültige Portliste, die bisherige gilt weiter.", "Invalid port list, the previous one stays in effect.");
    public static string ServiceDetectionLabel => S("Dienste und Versionen erkennen", "Identify services and versions");
    public static string TipServiceDetection => S("Fragt an offenen Ports den Dienst und seine Version ab (wie -sV). Aus: Offene Ports behalten nur den Namen aus der Tabelle.", "Asks each open port for its service and version (like -sV). Off: open ports keep just the name from the table.");
    public static string PresetCommandLabel => S("Gleicher Befehl", "Equivalent command");
    public static string TipPresetCommand => S("Diese Zeile startet denselben Scan aus der Befehlszeile. Sie folgt jeder Einstellung, die du änderst.", "This line runs the same scan from a command line. It follows every setting you change.");
    public static string CopyCommandButton => S("Befehl kopieren", "Copy command");
    public static string TipCopyCommand => S("Kopiert den Befehl in die Zwischenablage.", "Copies the command to the clipboard.");
    public static string CommandCopied => S("Befehl kopiert", "Command copied");
    public static string HelpAboutTitle => S("Was dieses Werkzeug ist", "What this tool is");
    public static string HelpAboutText => S(
        "IP-Scanner dient der Diagnose im eigenen Netzwerk. Er zeigt, welche Geräte antworten, welche Ports offen sind und welche Dienste dort laufen. Er ist keine Sammlung für Penetrationstests und greift keine Systeme an.",
        "IP-Scanner is for diagnosing your own network. It shows which devices answer, which ports are open and which services run there. It is not a penetration testing suite and does not attack systems.");
    public static string HelpLegalNote => S(
        "Scanne nur Netzwerke, für die du verantwortlich bist. Der Scan fremder Netzwerke kann verboten sein.",
        "Only scan networks you are responsible for. Scanning other networks can be against the law.");
    public static string HelpCommandLineTitle => S("Kommandozeile", "Command line");
    public static string HelpCommandLineIntro => S(
        "Diese Optionen gelten für IP-Scanner.exe in der Kommandozeile.",
        "These options work with IP-Scanner.exe on the command line. The descriptions match the command line help.");
    public static string HelpToolDiscoveryName => S("Geräte finden", "Host discovery");
    public static string HelpToolDiscoveryDesc => S(
        "Findet die Geräte im Netzwerk mit Ping und weiteren Verfahren und zeigt Name, MAC-Adresse, Hersteller und Antwortzeit. Nutze es für einen schnellen Überblick, wer im Netzwerk ist.",
        "Finds the devices on the network with ping and other methods and shows name, MAC address, vendor and response time. Use it for a quick overview of who is on the network.");
    public static string HelpToolServicesName => S("Dienst und Version", "Service and version");
    public static string HelpToolServicesDesc => S(
        "Erkennt, welcher Dienst hinter einem offenen Port läuft, und liest wenn möglich Programm und Version aus. Nutze es, um veraltete oder unerwartete Dienste zu finden.",
        "Identifies the service behind an open port and reads the program and version where it can. Use it to spot outdated or unexpected services.");
    public static string HelpToolOsName => S("Betriebssystem schätzen", "OS estimate");
    public static string HelpToolOsDesc => S(
        "Schätzt aus Antworten, Ports und Diensten, welches Betriebssystem ein Gerät nutzt. Das Ergebnis ist eine Schätzung und kann falsch sein.",
        "Estimates the operating system of a device from its replies, ports and services. The result is an estimate and can be wrong.");
    public static string HelpToolChecksName => S("Prüfungen", "Checks");
    public static string HelpToolChecksDesc => S(
        "Führt Prüfungen gegen die Dienste an offenen Ports aus. Sichere und erkundende Prüfungen lesen nur. Aufdringliche Prüfungen können einen Dienst stören und laufen nur nach deiner Freigabe.",
        "Runs checks against the services on open ports. Safe and discovery checks only read. Intrusive checks can disturb a service and run only after you allow them.");
    public static string HelpToolExportName => S("Scan und Export", "Scan and export");
    public static string HelpToolExportDesc => S(
        "Speichert Ergebnisse als Text, XML, JSON oder grep-bar und startet denselben Scan aus der Kommandozeile. Nutze es für Berichte, Vergleiche und Skripte.",
        "Saves results as text, XML, JSON or grepable output and runs the same scan from the command line. Use it for reports, comparisons and scripts.");
    public static string HelpExamplesTitle => S("Beispiele", "Examples");
    public static string HelpExitCodesTitle => S("Rückgabecodes", "Exit codes");

    // ── Help page reference (the descriptions of the CliFlags, CliHelp and CliExit tables) ──
    public static string HelpOpt_iL => S("Ziele aus einer Datei lesen, eine Angabe pro Zeile.", "Read targets from a file, one spec per line.");
    public static string HelpOpt_exclude => S("Auszulassende Ziele, durch Komma getrennt.", "Comma separated targets to leave out.");
    public static string HelpOpt_exclude_file => S("Die auszulassenden Ziele aus einer Datei lesen.", "Read the targets to leave out from a file.");
    public static string HelpOpt_6 => S("IPv6-Ziele scannen. Für sie Pflicht, IPv4-Ziele werden dann abgelehnt.", "Scan IPv6 targets. Required for them, and IPv4 targets are then refused.");
    public static string HelpOpt_sn => S("Nur Geräte finden: aktive Geräte suchen, keine Ports scannen.", "Discovery only: find the live hosts, scan no ports.");
    public static string HelpOpt_Pn => S("Gerätesuche überspringen und jedes Ziel als erreichbar behandeln.", "Skip discovery and treat every target as up.");
    public static string HelpOpt_sL => S("Ziele mit ihren Namen auflisten, nichts an sie senden.", "List the targets with their reverse names, send nothing to them.");
    public static string HelpOpt_sT => S("TCP-Verbindungsscan (Standard, braucht keinen Paketleser).", "TCP connect scan (default, needs no driver).");
    public static string HelpOpt_sS => S("TCP-SYN-Scan (braucht den Paketleser).", "TCP SYN scan (needs the packet driver).");
    public static string HelpOpt_sU => S("UDP-Scan; allein ersetzt er den TCP-Scan, mit einer TCP-Art kommt er dazu.", "UDP scan; alone it replaces the TCP scan, with a TCP type it adds to it.");
    public static string HelpOpt_sF => S("TCP-FIN-Scan (braucht den Paketleser und --allow-aggressive).", "TCP FIN scan (needs the packet driver and --allow-aggressive).");
    public static string HelpOpt_sX => S("TCP-Xmas-Scan (braucht den Paketleser und --allow-aggressive).", "TCP Xmas scan (needs the packet driver and --allow-aggressive).");
    public static string HelpOpt_sN => S("TCP-Null-Scan (braucht den Paketleser und --allow-aggressive).", "TCP Null scan (needs the packet driver and --allow-aggressive).");
    public static string HelpOpt_sA => S("TCP-ACK-Scan: zeigt Firewall-Regeln (braucht den Paketleser).", "TCP ACK scan: maps firewall rules (needs the packet driver).");
    public static string HelpOpt_sW => S("TCP-Window-Scan (braucht den Paketleser).", "TCP window scan (needs the packet driver).");
    public static string HelpOpt_sM => S("TCP-Maimon-Scan (braucht den Paketleser und --allow-aggressive).", "TCP Maimon scan (needs the packet driver and --allow-aggressive).");
    public static string HelpOpt_p => S("Zu scannende Ports: 22,80,8000-9000, T:80,U:53; mit -p- alle 65535 Ports.", "Ports to scan: 22,80,8000-9000, T:80,U:53; use -p- for all 65535 ports.");
    public static string HelpOpt_F => S("Schnell: die 100 häufigsten Ports.", "Fast: the 100 most common ports.");
    public static string HelpOpt_top_ports => S("Die n häufigsten Ports scannen.", "Scan the n most common ports.");
    public static string HelpOpt_sV => S("Dienst und Version hinter jedem offenen Port erkennen.", "Identify the service and version behind each open port.");
    public static string HelpOpt_version_intensity => S("Wie gründlich -sV prüft (0 hört nur zu, 9 probiert jede Abfrage).", "How hard -sV probes (0 listens only, 9 tries every probe).");
    public static string HelpOpt_O => S("Betriebssystem schätzen (braucht den Paketleser).", "Estimate the operating system (needs the packet driver).");
    public static string HelpOpt_checks => S("Dienstprüfungen ausführen: safe, discovery, intrusive (durch Komma getrennt).", "Run service checks: safe, discovery, intrusive (comma separated).");
    public static string HelpOpt_T => S("Zeitvorlage, 0 sehr vorsichtig bis 5 sehr aggressiv (Standard 3).", "Timing template, 0 paranoid to 5 insane (default 3).");
    public static string HelpOpt_min_rate => S("Mindestens n Anfragen pro Sekunde senden.", "Send at least n probes per second.");
    public static string HelpOpt_max_rate => S("Höchstens n Anfragen pro Sekunde senden.", "Send at most n probes per second.");
    public static string HelpOpt_max_retries => S("Wiederholungen pro Anfrage.", "Retransmissions per probe.");
    public static string HelpOpt_host_timeout => S("Ein Gerät nach dieser Zeit aufgeben (Endung ms, s, m oder h).", "Give up on a host after this time (ms, s, m or h suffix).");
    public static string HelpOpt_oN => S("Den Textbericht in eine Datei schreiben (- für die Standardausgabe).", "Write the text report to a file (- for stdout).");
    public static string HelpOpt_oX => S("Den XML-Bericht in eine Datei schreiben (- für die Standardausgabe).", "Write the XML report to a file (- for stdout).");
    public static string HelpOpt_oJ => S("Den JSON-Bericht in eine Datei schreiben (- für die Standardausgabe).", "Write the JSON report to a file (- for stdout).");
    public static string HelpOpt_oG => S("Den grep-baren Bericht in eine Datei schreiben (- für die Standardausgabe).", "Write the grepable report to a file (- for stdout).");
    public static string HelpOpt_reason => S("Zeigen, warum jeder Port seinen Zustand hat.", "Show why each port got its state.");
    public static string HelpOpt_open => S("Nur offene Ports melden (und open|filtered).", "Report only open (and open|filtered) ports.");
    public static string HelpOpt_allow_full_scan => S("Einen gewählten Scan von mehr als 100 Ports erlauben (-p, --top-ports), bis zu allen 65535.", "Allow a chosen scan of more than 100 ports (-p, --top-ports), up to all 65535.");
    public static string HelpOpt_allow_large => S("Mehr als 65536 Ziele erlauben.", "Allow more than 65536 targets.");
    public static string HelpOpt_allow_intrusive => S("Die Prüfkategorie intrusive erlauben.", "Allow the intrusive check category.");
    public static string HelpOpt_allow_aggressive => S("Die Scans FIN, Null, Xmas und Maimon erlauben.", "Allow the FIN, Null, Xmas and Maimon scans.");
    public static string HelpOpt_i_know_what_i_am_doing => S("Den Hinweis bei aggressiven Scans ausblenden (die Sperren oben gelten weiter).", "Hide the notice printed for aggressive scans (the gates above still apply).");
    public static string HelpOpt_interface => S("Von diesem Netzwerkadapter senden (IPv4-Adresse oder Adaptername). Gilt für IPv4-Ziele; IPv6-Ziele gehen über den Adapter, den das System dafür wählt.", "Send from this network adapter (IPv4 address or adapter name). Applies to IPv4 targets; IPv6 targets leave through the adapter the system routes them over.");
    public static string HelpOpt_v => S("Mehr Fortschritt auf stderr (-vv für Einzelheiten je Gerät).", "More progress on stderr (-vv for per-host detail).");
    public static string HelpOpt_d => S("Debug-Protokoll auf stderr.", "Debug log on stderr.");
    public static string HelpOpt_help => S("Diese Hilfe anzeigen.", "Show this help.");
    public static string HelpOpt_version => S("Die Version anzeigen.", "Show the version.");
    public static string HelpGroupTargets => S("Ziele", "Targets");
    public static string HelpGroupScanType => S("Scan-Art", "Scan type");
    public static string HelpGroupPorts => S("Anschlüsse (Ports)", "Ports");
    public static string HelpGroupDetection => S("Erkennung", "Detection");
    public static string HelpGroupTiming => S("Zeitverhalten", "Timing");
    public static string HelpGroupOutput => S("Ausgabe", "Output");
    public static string HelpGroupSafetyGates => S("Sicherheitssperren", "Safety gates");
    public static string HelpGroupMiscellaneous => S("Weiteres", "Miscellaneous");
    public static string HelpExample1 => S("Aktive Geräte eines Subnetzes finden.", "Find the live hosts of a subnet.");
    public static string HelpExample2 => S("Drei Ports scannen und einen JSON-Bericht schreiben.", "Scan three ports and write a JSON report.");
    public static string HelpExample3 => S("Die 100 häufigsten Ports scannen und die Dienste erkennen.", "Scan the 100 most common ports and identify the services.");
    public static string HelpExample4 => S("Die 50 häufigsten UDP-Ports scannen.", "Scan the 50 most common UDP ports.");
    public static string HelpExample5 => S("SYN-Scan aller 65535 Ports (braucht den Paketleser).", "SYN scan of all 65535 ports (packet driver needed).");
    public static string HelpExample6 => S("Ziele aus einer Datei lesen, eines auslassen, XML schreiben.", "Read targets from a file, leave one out, write XML.");
    public static string HelpExample7 => S("Adressen und ihre Namen auflisten, ohne etwas zu senden.", "List the addresses and their names without sending anything.");
    public static string HelpExample8 => S("Gerätesuche überspringen, schneller scannen, nur offene Ports und den Grund zeigen.", "Skip discovery, scan faster, show only open ports and why.");
    public static string HelpExample9 => S("Scannen, dann die Prüfungen safe und discovery ausführen.", "Scan, then run the safe and discovery service checks.");
    public static string HelpExample10 => S("Betriebssystem schätzen und Dienste erkennen (braucht den Paketleser).", "Estimate the operating system and identify the services (packet driver needed).");
    public static string HelpExample11 => S("Textbericht auf der Standardausgabe, grep-barer Bericht in einer Datei.", "Text report on stdout, grepable report in a file.");
    public static string HelpExample12 => S("Einen IPv6-Rechner scannen.", "Scan an IPv6 host.");
    public static string HelpExit0 => S("Der Scan ist fertig.", "The scan finished.");
    public static string HelpExit1 => S("Der Scan konnte nicht laufen oder ist fehlgeschlagen (kein Netzwerk, Paketleser fehlt, Datei nicht beschreibbar).", "The scan could not run or failed (no network, driver missing, file not writable).");
    public static string HelpExit2 => S("Falsche Eingabe: unbekannte Option, ungültiger Wert, kein Ziel oder eine Sicherheitssperre wurde nicht geöffnet.", "Wrong usage: unknown option, bad value, no target, or a safety gate was not opened.");
    public static string HelpExit3 => S("Die Standardausgabe wurde vor dem Bericht geschlossen (zum Beispiel durch | head); die Berichtsdateien wurden trotzdem geschrieben.", "Stdout was closed before the report was printed (for example by | head); report files were still written.");
    public static string HelpExit130 => S("Mit Strg+C gestoppt; das Teilergebnis wurde trotzdem geschrieben.", "Stopped with Ctrl+C; the partial result was still written.");

    /// <summary>The description of the command line flag with this id, in the UI language.</summary>
    public static string HelpOption(string id) => id switch
    {
        "iL" => HelpOpt_iL,
        "exclude" => HelpOpt_exclude,
        "exclude-file" => HelpOpt_exclude_file,
        "6" => HelpOpt_6,
        "sn" => HelpOpt_sn,
        "Pn" => HelpOpt_Pn,
        "sL" => HelpOpt_sL,
        "sT" => HelpOpt_sT,
        "sS" => HelpOpt_sS,
        "sU" => HelpOpt_sU,
        "sF" => HelpOpt_sF,
        "sX" => HelpOpt_sX,
        "sN" => HelpOpt_sN,
        "sA" => HelpOpt_sA,
        "sW" => HelpOpt_sW,
        "sM" => HelpOpt_sM,
        "p" => HelpOpt_p,
        "F" => HelpOpt_F,
        "top-ports" => HelpOpt_top_ports,
        "sV" => HelpOpt_sV,
        "version-intensity" => HelpOpt_version_intensity,
        "O" => HelpOpt_O,
        "checks" => HelpOpt_checks,
        "T" => HelpOpt_T,
        "min-rate" => HelpOpt_min_rate,
        "max-rate" => HelpOpt_max_rate,
        "max-retries" => HelpOpt_max_retries,
        "host-timeout" => HelpOpt_host_timeout,
        "oN" => HelpOpt_oN,
        "oX" => HelpOpt_oX,
        "oJ" => HelpOpt_oJ,
        "oG" => HelpOpt_oG,
        "reason" => HelpOpt_reason,
        "open" => HelpOpt_open,
        "allow-full-scan" => HelpOpt_allow_full_scan,
        "allow-large" => HelpOpt_allow_large,
        "allow-intrusive" => HelpOpt_allow_intrusive,
        "allow-aggressive" => HelpOpt_allow_aggressive,
        "i-know-what-i-am-doing" => HelpOpt_i_know_what_i_am_doing,
        "interface" => HelpOpt_interface,
        "v" => HelpOpt_v,
        "d" => HelpOpt_d,
        "help" => HelpOpt_help,
        "version" => HelpOpt_version,
        _ => ""
    };

    /// <summary>The heading of a command line flag group (the English group name), in the UI language.</summary>
    public static string HelpGroup(string group) => group switch
    {
        "Targets" => HelpGroupTargets,
        "Scan type" => HelpGroupScanType,
        "Ports" => HelpGroupPorts,
        "Detection" => HelpGroupDetection,
        "Timing" => HelpGroupTiming,
        "Output" => HelpGroupOutput,
        "Safety gates" => HelpGroupSafetyGates,
        "Miscellaneous" => HelpGroupMiscellaneous,
        _ => ""
    };

    /// <summary>The explanation of the command line example with this index, in the UI language.</summary>
    public static string HelpExample(int index) => index switch
    {
        0 => HelpExample1,
        1 => HelpExample2,
        2 => HelpExample3,
        3 => HelpExample4,
        4 => HelpExample5,
        5 => HelpExample6,
        6 => HelpExample7,
        7 => HelpExample8,
        8 => HelpExample9,
        9 => HelpExample10,
        10 => HelpExample11,
        11 => HelpExample12,
        _ => ""
    };

    /// <summary>The meaning of this exit code, in the UI language.</summary>
    public static string HelpExit(int code) => code switch
    {
        0 => HelpExit0,
        1 => HelpExit1,
        2 => HelpExit2,
        3 => HelpExit3,
        130 => HelpExit130,
        _ => ""
    };
    public static string ToolHostDiscovery => S("Geräte finden", "Host discovery");
    public static string ToolHostDiscoveryDesc => S("Findet erreichbare Geräte in einem Adressbereich per Ping.", "Finds reachable devices in an address range by ping.");
    public static string ToolServiceDetection => S("Dienste erkennen", "Service detection");
    public static string ToolServiceDetectionDesc => S("Prüft offene Ports mehrerer Geräte und erkennt Dienst und Version.", "Checks the open ports of several devices and detects service and version.");
    public static string ToolOsEstimate => S("System schätzen", "OS estimate");
    public static string ToolOsEstimateDesc => S("Schätzt das Betriebssystem aus Antwort-TTL, offenen Ports und Fingerabdruck.", "Estimates the operating system from the reply TTL, open ports and fingerprint.");
    public static string ToolChecks => S("Dienst-Prüfungen", "Service checks");
    public static string ToolChecksDesc => S("Prüft die offenen Dienste mehrerer Geräte auf bekannte Schwachstellen und Fehlkonfigurationen.", "Checks the open services of several devices for known weaknesses and misconfigurations.");
    public static string ToolScanExport => S("Scan und Export", "Scan and export");
    public static string ToolScanExportDesc => S("Führt eine Vorlage aus und speichert den Bericht als Text, XML, JSON oder grepbar.", "Runs a preset and saves the report as text, XML, JSON or greppable.");
    public static string ScanToolTargetTip => S("Adressen, Bereiche, CIDR-Netze oder Hostnamen, durch Komma getrennt. Beispiel: 192.168.1.0/24", "Addresses, ranges, CIDR networks or host names, separated by commas. Example: 192.168.1.0/24");
    public static string ScanToolTargetEmpty => S("Gib ein Ziel ein.", "Enter a target.");
    public static string ScanToolTargetCountFmt => S("{0} Adressen", "{0} addresses");
    public static string ScanToolTargetInvalidFmt => S("Ungültiges Ziel: {0}", "Invalid target: {0}");
    public static string ScanToolStartButton => S("Starten", "Start");
    public static string ScanToolStartTip => S("Startet den Scan mit den gewählten Optionen.", "Starts the scan with the chosen options.");
    public static string ScanToolTimingLabel => S("Tempo", "Timing");
    public static string ScanToolTimingTip => S("Wie schnell gescannt wird, von T0 (sehr langsam) bis T5 (sehr schnell). T4 und T5 sind im Netz sehr laut.", "How fast the scan runs, from T0 (very slow) to T5 (very fast). T4 and T5 are very noisy on the network.");
    public static string ScanToolSkipDiscovery => S("Ping-Test überspringen", "Skip the ping test");
    public static string ScanToolSkipDiscoveryTip => S("Behandelt alle Ziele als erreichbar und prüft sie direkt, auch wenn sie nicht auf Ping antworten.", "Treats every target as up and scans it directly, even if it does not answer ping.");
    public static string WarnAggressiveScanType => S("Dieser Scan-Typ sendet ungewöhnlich gebaute Pakete. Firewalls und Einbruchserkennung schlagen oft Alarm, manche Geräte reagieren unvorhersehbar.", "This scan type sends unusually crafted packets. Firewalls and intrusion detection often raise an alarm, and some devices react unpredictably.");
    public static string WarnIntrusiveChecks => S("Diese Prüfungen schicken aktiv Anfragen ins Netz, auch an Geräte außerhalb des Ziels (zum Beispiel per Broadcast). Nur im eigenen Netz nutzen.", "These checks actively send requests into the network, also to devices beyond the target (for example by broadcast). Use them on your own network only.");
    public static string WarnFastTiming => S("T4 und T5 senden sehr viele Pakete gleichzeitig. Das kann schwache Router und Geräte überlasten und löst eher Alarme aus.", "T4 and T5 send very many packets at once. This can overload weak routers and devices and is more likely to trigger alarms.");
    public static string WarnFullRange => S("Prüft sehr viele Ports pro Gerät. Das dauert lange, erzeugt viel Verkehr und fällt in Protokollen auf.", "Checks a very large number of ports per device. It takes long, creates a lot of traffic and stands out in logs.");
    public static string ScanToolFormatLabel => S("Format", "Format");
    public static string ScanToolFormatTip => S("Dateiformat zum Speichern.", "File format for saving.");
    public static string ScanToolSaveButton => S("Speichern", "Save");
    public static string ScanToolSaveTip => S("Speichert das Ergebnis im gewählten Format in einer Datei.", "Saves the result to a file in the chosen format.");
    public static string ScanToolCopyButton => S("Kopieren", "Copy");
    public static string ScanToolCopyTip => S("Kopiert das Ergebnis als Text in die Zwischenablage.", "Copies the result to the clipboard as text.");
    public static string ScanToolSaveTitle => S("Bericht speichern", "Save report");
    public static string ScanToolSavedFmt => S("Gespeichert: {0}", "Saved: {0}");
    public static string ScanToolSaveFailedFmt => S("Speichern fehlgeschlagen: {0}", "Saving failed: {0}");
    public static string ScanToolStageDiscovery => S("Suche erreichbare Geräte", "Finding reachable devices");
    public static string ScanToolStagePorts => S("Prüfe Ports", "Checking ports");
    public static string ScanToolStageChecks => S("Führe Prüfungen aus", "Running checks");
    public static string ScanToolStageOs => S("Schätze Betriebssysteme", "Estimating operating systems");
    public static string ScanToolDoneFmt => S("Fertig: {0} Geräte, {1} offene Ports", "Done: {0} hosts, {1} open ports");
    public static string ScanToolStoppedFmt => S("Abgebrochen: {0} Geräte, {1} offene Ports", "Stopped: {0} hosts, {1} open ports");
    public static string ScanToolBusy => S("Ein anderer Scan läuft schon. Warte, bis er fertig ist.", "Another scan is already running. Wait until it has finished.");
    public static string ScanToolResultsTip => S("Live-Ergebnisse. Neue Zeilen erscheinen, sobald ein Ergebnis eintrifft.", "Live results. New rows appear as soon as a result arrives.");
    public static string ScanToolColLatency => S("Antwortzeit", "Reply time");
    public static string ScanToolColMatch => S("Treffer", "Match");
    public static string ScanToolLargeFmt => S("Du willst {0} Adressen scannen. Das kann lange dauern und viel Verkehr erzeugen. Fortfahren?", "You are about to scan {0} addresses. This can take a long time and create a lot of traffic. Continue?");
    public static string ScanToolAggressiveFmt => S("Der Scan-Typ {0} sendet ungewöhnliche Pakete. Firewalls und Einbruchserkennung schlagen dabei oft Alarm. Fortfahren?", "The {0} scan type sends unusual packets. Firewalls and intrusion detection often raise an alarm. Continue?");
    public static string ScanToolExportRun => S("Scannen und speichern", "Scan and save");
    public static string ScanToolExportRunTip => S("Führt die Vorlage aus und speichert den Bericht danach in der gewählten Datei.", "Runs the preset and then saves the report to the chosen file.");
    public static string ScanToolPortsLabel => S("Ports", "Ports");
    public static string ScanToolPortsTip => S("Welche Ports pro Gerät geprüft werden. Je mehr Ports, desto länger dauert der Scan.", "Which ports are checked per device. The more ports, the longer the scan takes.");
    public static string ScanToolPortsAll => S("Alle Ports (1 bis 65535)", "All ports (1 to 65535)");
    public static string ScanToolPresetTip => S("Vorlage mit Tempo, Ports und Optionen. Eigene Vorlagen legst du in den Einstellungen an.", "Preset with timing, ports and options. You create your own presets in the settings.");
    public static string ScanToolTargetTooMany => S("Mehr als 65536 Adressen. Wähle einen kleineren Bereich.", "More than 65536 addresses. Choose a smaller range.");

    /// <summary>Why an IPv6 prefix with more than 65,536 addresses is refused instead of swept.</summary>
    public static string Ipv6PrefixTooLarge => S(
        "Ein IPv6-Netz mit mehr als 65.536 Adressen (zum Beispiel /64) wird nicht Adresse für Adresse durchsucht. Gib einen kleineren Bereich an (/112 oder länger).",
        "An IPv6 network with more than 65,536 addresses (for example a /64) is not scanned address by address. Enter a smaller range (/112 or longer).");

    // Tool pages (round 2026-10-24)
    public static string PortCheckSplitterTip => S("Ziehen, um die Höhe der beiden Tabellen zu ändern.", "Drag to change the height of the two tables.");
}
