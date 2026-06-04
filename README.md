# IP-Scanner

Windows desktop network scanner. Pings every host in one or more IPv4 subnets,
resolves hostnames and MAC addresses, and shows live latency statistics in a
dark, table-focused UI. Built with C# / WPF on .NET 8, ships as a single
self-contained exe.

![IP-Scanner](docs/screenshot.png)

*The screenshot shows synthetic sample data (TEST-NET addresses), not a real network.*

## Features

- Two-phase scan: fast discovery sweep, then a configurable number of analysis
  pings per device — devices join the run the moment they answer
- Offline IPs are rechecked during the run and picked up when they come online
- Hostname/MAC resolution over several techniques in parallel (ARP, reverse
  DNS, mDNS, NetBIOS); the first result shows immediately, better ones replace it
- Latency table with min/avg/max/last, heatmap colors, best/worst markers and
  a totals row
- Device grouping by MAC vendor and hostname prefix, gateway highlighted
- Multiple subnets per run plus pinned IPs that are always scanned
- Internet latency panel (configurable public hosts)
- Known-devices database (SQLite), mergeable between instances
- TXT/CSV reports, German/English UI (auto-detected), adjustable text size
- Settings persist next to the database and can be exported/imported as .conf

## Build

Requires the .NET 8 SDK on Windows.

```
build.bat
```

produces `dist\IP-Scanner.exe` (self-contained, no .NET install needed).

For development:

```
dotnet build IP-Scanner.slnx
dotnet test IP-Scanner.slnx
```

## Notes

Scan reports, the device database and the local configuration contain
information about your network. They are written to the scans folder and are
excluded from version control.
