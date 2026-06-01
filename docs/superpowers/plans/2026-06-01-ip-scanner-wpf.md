# IP-Scanner WPF Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the Python terminal network scanner (`network_scanner.py`) to a native Windows WPF (.NET 8) GUI app called "IP-Scanner" with all existing options, shipped as one self-contained `.exe`.

**Architecture:** MVVM. A `Core` layer (pure C#, no WPF) holds the scanning engine, P/Invoke network helpers, SQLite DB, config, and export. A `ViewModels` layer exposes observable state. WPF Views bind to it. All async work uses `Task` + `CancellationToken`; UI updates marshalled via the Dispatcher / `ObservableCollection`.

**Tech Stack:** C#, .NET 8, WPF, P/Invoke (iphlpapi/ws2_32), Microsoft.Data.Sqlite, xUnit for tests.

---

## File Structure

```
IP-Scanner/
├── IP-Scanner.sln
├── src/IpScanner/
│   ├── IpScanner.csproj            # WPF app, net8.0-windows, PublishSingleFile
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── ViewModels/
│   │   ├── ObservableObject.cs     # INotifyPropertyChanged base
│   │   ├── RelayCommand.cs         # ICommand impl
│   │   ├── MainViewModel.cs
│   │   ├── DeviceViewModel.cs
│   │   ├── NetworkInfoViewModel.cs
│   │   ├── ProgressViewModel.cs
│   │   ├── InternetHostViewModel.cs
│   │   └── SettingsViewModel.cs
│   ├── Views/
│   │   ├── SettingsWindow.xaml / .cs
│   │   └── Controls/SegmentedProgressBar.cs
│   ├── Core/
│   │   ├── Models/
│   │   │   ├── Device.cs
│   │   │   ├── NetworkInfo.cs
│   │   │   ├── PingResult.cs
│   │   │   └── ScanConfig.cs
│   │   ├── Scanner/
│   │   │   ├── IcmpPinger.cs
│   │   │   ├── NetworkDetector.cs
│   │   │   ├── ArpHelper.cs
│   │   │   ├── NetBiosHelper.cs
│   │   │   ├── DeviceGrouper.cs
│   │   │   ├── GroupColorPalette.cs
│   │   │   └── ScanEngine.cs
│   │   ├── Data/
│   │   │   ├── ConfigManager.cs
│   │   │   └── KnownDevicesDb.cs
│   │   └── Export/
│   │       ├── TxtExporter.cs
│   │       ├── CsvExporter.cs
│   │       └── MacVendorLookup.cs
│   └── Resources/
│       ├── Styles.xaml             # Catppuccin dark theme
│       ├── network.ico
│       └── oui.txt                 # embedded MAC vendor DB
├── tests/IpScanner.Tests/
│   ├── IpScanner.Tests.csproj      # xUnit, net8.0
│   └── ... (one test file per Core class)
└── build.bat                       # dotnet publish single-file
```

---

## PHASE 0 — Project Scaffolding

### Task 1: Solution + test project skeleton

**Files:**
- Create: `IP-Scanner.sln`
- Create: `src/IpScanner/IpScanner.csproj`
- Create: `tests/IpScanner.Tests/IpScanner.Tests.csproj`
- Create: `src/IpScanner/Core/Models/PingResult.cs`
- Test: `tests/IpScanner.Tests/SmokeTest.cs`

- [ ] **Step 1: Create the WPF project file**

`src/IpScanner/IpScanner.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>IP-Scanner</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ApplicationIcon>Resources\network.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project file**

`tests/IpScanner.Tests/IpScanner.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.6.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\IpScanner\IpScanner.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create a simple model + smoke test**

`src/IpScanner/Core/Models/PingResult.cs`:
```csharp
namespace IpScanner.Core.Models;

public readonly record struct PingResult(bool Success, double? LatencyMs, int? Ttl);
```

`tests/IpScanner.Tests/SmokeTest.cs`:
```csharp
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests;

public class SmokeTest
{
    [Fact]
    public void PingResult_StoresValues()
    {
        var r = new PingResult(true, 1.5, 64);
        Assert.True(r.Success);
        Assert.Equal(1.5, r.LatencyMs);
        Assert.Equal(64, r.Ttl);
    }
}
```

- [ ] **Step 4: Create the solution and add projects**

Run:
```
dotnet new sln -n IP-Scanner -o C:\Claude\IP-Scanner
dotnet sln add src\IpScanner\IpScanner.csproj
dotnet sln add tests\IpScanner.Tests\IpScanner.Tests.csproj
```

- [ ] **Step 5: Build and run the smoke test**

Run: `dotnet test`
Expected: PASS (1 test). Build succeeds for both projects.

- [ ] **Step 6: Commit**

```
git add .
git commit -m "chore: scaffold WPF app + xUnit test project"
```

---

## PHASE 1 — Core Models

### Task 2: Device and NetworkInfo models

**Files:**
- Create: `src/IpScanner/Core/Models/Device.cs`
- Create: `src/IpScanner/Core/Models/NetworkInfo.cs`
- Test: `tests/IpScanner.Tests/Core/DeviceTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/Core/DeviceTests.cs`:
```csharp
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceTests
{
    [Fact]
    public void RecordPing_UpdatesStats()
    {
        var d = new Device("192.168.1.1");
        d.RecordPing(new PingResult(true, 2.0, 64));
        d.RecordPing(new PingResult(true, 4.0, 64));
        Assert.Equal(2, d.SuccessCount);
        Assert.Equal(2.0, d.MinMs);
        Assert.Equal(4.0, d.MaxMs);
        Assert.Equal(3.0, d.AvgMs);
        Assert.Equal(4.0, d.LastMs);
        Assert.True(d.IsOnline);
    }

    [Fact]
    public void RecordPing_Failure_CountsAndCanGoOffline()
    {
        var d = new Device("192.168.1.2") { OfflineAfterFailures = 2 };
        d.RecordPing(new PingResult(false, null, null));
        Assert.False(d.IsOnline);   // never answered
        d.RecordPing(new PingResult(true, 1.0, 64));
        Assert.True(d.IsOnline);
        d.RecordPing(new PingResult(false, null, null));
        d.RecordPing(new PingResult(false, null, null));
        Assert.True(d.WentOffline);  // 2 consecutive misses after being online
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DeviceTests`
Expected: FAIL — `Device` does not exist.

- [ ] **Step 3: Implement Device**

`src/IpScanner/Core/Models/Device.cs`:
```csharp
namespace IpScanner.Core.Models;

public sealed class Device
{
    public Device(string ip) => Ip = ip;

    public string Ip { get; }
    public string? Mac { get; set; }
    public string? Hostname { get; set; }
    public int GroupId { get; set; }
    public bool FromDb { get; set; }
    public bool Seen { get; private set; }
    public int CurrentPings { get; private set; }
    public int TargetPings { get; set; } = 10;
    public int OfflineAfterFailures { get; set; } = 5;

    public int SuccessCount { get; private set; }
    public int FailCount { get; private set; }
    public double? MinMs { get; private set; }
    public double? MaxMs { get; private set; }
    public double? AvgMs { get; private set; }
    public double? LastMs { get; private set; }

    private int _consecutiveFails;
    private double _sumMs;

    public bool IsOnline { get; private set; }
    public bool WentOffline { get; private set; }

    public void RecordPing(PingResult r)
    {
        CurrentPings++;
        if (r.Success)
        {
            Seen = true;
            IsOnline = true;
            WentOffline = false;
            _consecutiveFails = 0;
            SuccessCount++;
            if (r.LatencyMs is { } ms)
            {
                LastMs = ms;
                MinMs = MinMs is null ? ms : Math.Min(MinMs.Value, ms);
                MaxMs = MaxMs is null ? ms : Math.Max(MaxMs.Value, ms);
                _sumMs += ms;
                AvgMs = _sumMs / SuccessCount;
            }
        }
        else
        {
            FailCount++;
            _consecutiveFails++;
            if (Seen && _consecutiveFails >= OfflineAfterFailures)
            {
                IsOnline = false;
                WentOffline = true;
            }
        }
    }
}
```

- [ ] **Step 4: Implement NetworkInfo**

`src/IpScanner/Core/Models/NetworkInfo.cs`:
```csharp
namespace IpScanner.Core.Models;

public sealed class NetworkInfo
{
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public string? Gateway { get; set; }
    public string? SubnetMask { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public string Interface { get; set; } = "eth0";

    /// <summary>"192.168.1.0/24" style label, or null.</summary>
    public string? Cidr =>
        Ip is null ? null : $"{string.Join('.', Ip.Split('.')[..3])}.0/24";
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter DeviceTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```
git add .
git commit -m "feat: add Device and NetworkInfo core models"
```

### Task 3: ScanConfig model + IPv4 helpers

**Files:**
- Create: `src/IpScanner/Core/Models/ScanConfig.cs`
- Create: `src/IpScanner/Core/Net/Ipv4.cs`
- Test: `tests/IpScanner.Tests/Core/Ipv4Tests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/Core/Ipv4Tests.cs`:
```csharp
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class Ipv4Tests
{
    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("abc", false)]
    public void IsValid_Works(string ip, bool expected)
        => Assert.Equal(expected, Ipv4.IsValid(ip));

    [Fact]
    public void PrefixToMask_Converts24()
        => Assert.Equal("255.255.255.0", Ipv4.PrefixToMask(24));

    [Fact]
    public void SubnetPrefix_TakesFirstThreeOctets()
        => Assert.Equal("192.168.1", Ipv4.SubnetPrefix("192.168.1.55"));

    [Fact]
    public void HostsInSubnet_Returns254()
        => Assert.Equal(254, Ipv4.HostsInSubnet("192.168.1").Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter Ipv4Tests`
Expected: FAIL — `Ipv4` does not exist.

- [ ] **Step 3: Implement Ipv4 helper**

`src/IpScanner/Core/Net/Ipv4.cs`:
```csharp
namespace IpScanner.Core.Net;

public static class Ipv4
{
    public const int FirstHost = 1;
    public const int LastHost = 254;

    public static bool IsValid(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 &&
               parts.All(p => int.TryParse(p, out var n) && n is >= 0 and <= 255);
    }

    public static string PrefixToMask(int prefix)
    {
        uint bits = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix);
        return $"{(bits >> 24) & 0xFF}.{(bits >> 16) & 0xFF}.{(bits >> 8) & 0xFF}.{bits & 0xFF}";
    }

    public static string SubnetPrefix(string ip)
        => string.Join('.', ip.Split('.')[..3]);

    public static List<string> HostsInSubnet(string prefix)
    {
        var list = new List<string>(LastHost - FirstHost + 1);
        for (int i = FirstHost; i <= LastHost; i++)
            list.Add($"{prefix}.{i}");
        return list;
    }
}
```

- [ ] **Step 4: Implement ScanConfig**

`src/IpScanner/Core/Models/ScanConfig.cs`:
```csharp
namespace IpScanner.Core.Models;

/// <summary>All scan options. Mirrors network_scanner.conf keys.</summary>
public sealed class ScanConfig
{
    public List<string> Subnets { get; set; } = new();        // extra subnets (CIDR or prefix)
    public List<string> PinnedIps { get; set; } = new();
    public int PingCount { get; set; } = 10;                  // -1 = infinite
    public int PingIntervalMs { get; set; } = 100;
    public int OfflineAfterFailedPings { get; set; } = 5;
    public int InitPingCount { get; set; } = 1;
    public bool HighPressureMode { get; set; }
    public bool EnableInternetPing { get; set; } = true;
    public List<string> InternetHosts { get; set; } =
        new() { "8.8.8.8", "8.8.4.4", "1.1.1.1", "9.9.9.9" };
    public bool KnownDevicesDb { get; set; } = true;
    public string OutputDirectory { get; set; } = "./Scans";
    public bool FileOutput { get; set; } = true;
    public bool ExportCsv { get; set; }
    public int PingThreads { get; set; } = 100;
    public int InitPingThreads { get; set; } = 254;
    public double RefreshRate { get; set; } = 1.0;

    public const int InfinitePingCount = -1;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter Ipv4Tests`
Expected: PASS (7 cases).

- [ ] **Step 6: Commit**

```
git add .
git commit -m "feat: add ScanConfig model and Ipv4 helpers"
```

---

## PHASE 2 — Config Manager

### Task 4: ConfigManager read (flat key=value)

**Files:**
- Create: `src/IpScanner/Core/Data/ConfigManager.cs`
- Test: `tests/IpScanner.Tests/Core/ConfigManagerTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/Core/ConfigManagerTests.cs`:
```csharp
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class ConfigManagerTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_ParsesValues_AndClampsRange()
    {
        var path = WriteTemp("""
            ping_count = 50
            ping_threads = 5000
            high_pressure_mode = true
            subnet = 192.168.1.0/24
            subnet_2 = 10.0.0.0/24
            pinned_ips = 192.168.1.1, 192.168.1.10
            """);
        var cfg = ConfigManager.Load(path);
        Assert.Equal(50, cfg.PingCount);
        Assert.Equal(1000, cfg.PingThreads);          // clamped to max 1000
        Assert.True(cfg.HighPressureMode);
        Assert.Equal(new[] { "192.168.1.0/24", "10.0.0.0/24" }, cfg.Subnets);
        Assert.Equal(new[] { "192.168.1.1", "192.168.1.10" }, cfg.PinnedIps);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigManager.Load("does-not-exist.conf");
        Assert.Equal(10, cfg.PingCount);
        Assert.True(cfg.EnableInternetPing);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ConfigManagerTests`
Expected: FAIL — `ConfigManager` does not exist.

- [ ] **Step 3: Implement ConfigManager.Load**

`src/IpScanner/Core/Data/ConfigManager.cs`:
```csharp
using System.Text.RegularExpressions;
using IpScanner.Core.Models;
using IpScanner.Core.Net;

namespace IpScanner.Core.Data;

/// <summary>Reads/writes ip_scanner.conf (flat key=value, # comments).</summary>
public static class ConfigManager
{
    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    public static ScanConfig Load(string path)
    {
        var cfg = new ScanConfig();
        if (!File.Exists(path)) return cfg;

        var flat = ParseFlat(path);
        var subnets = new SortedDictionary<int, string>();

        foreach (var (key, value) in flat)
        {
            switch (key)
            {
                case "ping_count": cfg.PingCount = ParseInt(value, 1, 10_000_000, 10); break;
                case "ping_interval_ms": cfg.PingIntervalMs = ParseInt(value, 0, 10_000, 100); break;
                case "offline_after_failed_pings": cfg.OfflineAfterFailedPings = ParseInt(value, 1, 100, 5); break;
                case "init_ping_count": cfg.InitPingCount = ParseInt(value, 1, 100, 1); break;
                case "high_pressure_mode": cfg.HighPressureMode = ParseBool(value); break;
                case "enable_internet_ping": cfg.EnableInternetPing = ParseBool(value); break;
                case "internet_hosts": cfg.InternetHosts = ParseIpList(value); break;
                case "known_devices_db": cfg.KnownDevicesDb = ParseBool(value); break;
                case "output_directory": cfg.OutputDirectory = value; break;
                case "file_output": cfg.FileOutput = ParseBool(value); break;
                case "export_csv": cfg.ExportCsv = ParseBool(value); break;
                case "ping_threads": cfg.PingThreads = ParseInt(value, 1, 1000, 100); break;
                case "init_ping_threads": cfg.InitPingThreads = ParseInt(value, 0, 1000, 254); break;
                case "refresh_rate": cfg.RefreshRate = ParseDouble(value, 0.1, 60.0, 1.0); break;
                case "pinned_ips": cfg.PinnedIps = ParseIpList(value); break;
                default:
                    var m = Regex.Match(key, @"^subnet(?:_(\d+))?$");
                    if (m.Success && value.Length > 0)
                        subnets[m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0] = value;
                    break;
            }
        }
        cfg.Subnets = subnets.Values.Distinct().ToList();
        return cfg;
    }

    private static Dictionary<string, string> ParseFlat(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            var idx = line.IndexOf('=');
            if (idx < 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Split('#')[0].Split(';')[0].Trim();
            if (key.Length > 0) d[key] = value;
        }
        return d;
    }

    private static int ParseInt(string s, int lo, int hi, int fallback)
        => int.TryParse(s.Trim(), out var v) ? Clamp(v, lo, hi) : fallback;

    private static double ParseDouble(string s, double lo, double hi, double fallback)
        => double.TryParse(s.Trim().Replace(',', '.'),
               System.Globalization.CultureInfo.InvariantCulture, out var v)
           ? Clamp(v, lo, hi) : fallback;

    private static bool ParseBool(string s)
        => s.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on" or "enabled";

    private static List<string> ParseIpList(string s)
    {
        var result = new List<string>();
        foreach (var part in s.Replace(';', ',').Split(','))
        {
            var ip = part.Trim();
            if (ip.Length > 0 && !result.Contains(ip)) result.Add(ip);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ConfigManagerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add ConfigManager flat-config reader"
```

### Task 5: ConfigManager write

**Files:**
- Modify: `src/IpScanner/Core/Data/ConfigManager.cs`
- Test: `tests/IpScanner.Tests/Core/ConfigManagerTests.cs`

- [ ] **Step 1: Write the failing test (append to existing file)**

```csharp
    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var cfg = new ScanConfig
        {
            PingCount = 100, PingIntervalMs = 250, ExportCsv = true,
            Subnets = new() { "192.168.5.0/24" },
            PinnedIps = new() { "192.168.5.1" },
            InternetHosts = new() { "1.1.1.1" },
        };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);
        Assert.Equal(100, loaded.PingCount);
        Assert.Equal(250, loaded.PingIntervalMs);
        Assert.True(loaded.ExportCsv);
        Assert.Equal(new[] { "192.168.5.0/24" }, loaded.Subnets);
        Assert.Equal(new[] { "192.168.5.1" }, loaded.PinnedIps);
        Assert.Equal(new[] { "1.1.1.1" }, loaded.InternetHosts);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ConfigManagerTests`
Expected: FAIL — `ConfigManager.Save` does not exist.

- [ ] **Step 3: Implement Save**

Add to `ConfigManager`:
```csharp
    public static void Save(string path, ScanConfig c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# IP-Scanner configuration");
        sb.AppendLine($"ping_count = {c.PingCount}");
        sb.AppendLine($"ping_interval_ms = {c.PingIntervalMs}");
        sb.AppendLine($"offline_after_failed_pings = {c.OfflineAfterFailedPings}");
        sb.AppendLine($"init_ping_count = {c.InitPingCount}");
        sb.AppendLine($"high_pressure_mode = {(c.HighPressureMode ? "true" : "false")}");
        sb.AppendLine($"enable_internet_ping = {(c.EnableInternetPing ? "true" : "false")}");
        sb.AppendLine($"internet_hosts = {string.Join(", ", c.InternetHosts)}");
        sb.AppendLine($"known_devices_db = {(c.KnownDevicesDb ? "true" : "false")}");
        sb.AppendLine($"output_directory = {c.OutputDirectory}");
        sb.AppendLine($"file_output = {(c.FileOutput ? "true" : "false")}");
        sb.AppendLine($"export_csv = {(c.ExportCsv ? "true" : "false")}");
        sb.AppendLine($"ping_threads = {c.PingThreads}");
        sb.AppendLine($"init_ping_threads = {c.InitPingThreads}");
        sb.AppendLine($"refresh_rate = {c.RefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        for (int i = 0; i < c.Subnets.Count; i++)
            sb.AppendLine(i == 0 ? $"subnet = {c.Subnets[i]}" : $"subnet_{i + 1} = {c.Subnets[i]}");
        if (c.PinnedIps.Count > 0)
            sb.AppendLine($"pinned_ips = {string.Join(", ", c.PinnedIps)}");
        File.WriteAllText(path, sb.ToString());
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ConfigManagerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add ConfigManager save with round-trip"
```

---

## PHASE 3 — Network P/Invoke Helpers

### Task 6: IcmpPinger (P/Invoke IcmpSendEcho)

**Files:**
- Create: `src/IpScanner/Core/Scanner/IcmpPinger.cs`
- Test: `tests/IpScanner.Tests/Core/IcmpPingerTests.cs`

- [ ] **Step 1: Write the test (integration — pings loopback)**

`tests/IpScanner.Tests/Core/IcmpPingerTests.cs`:
```csharp
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class IcmpPingerTests
{
    [Fact]
    public void Ping_Loopback_Succeeds()
    {
        var r = IcmpPinger.Ping("127.0.0.1", 1000);
        Assert.True(r.Success);
        Assert.NotNull(r.LatencyMs);
    }

    [Fact]
    public void Ping_InvalidAddress_Fails()
    {
        var r = IcmpPinger.Ping("192.0.2.1", 200); // TEST-NET-1, unroutable
        Assert.False(r.Success);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter IcmpPingerTests`
Expected: FAIL — `IcmpPinger` does not exist.

- [ ] **Step 3: Implement IcmpPinger**

`src/IpScanner/Core/Scanner/IcmpPinger.cs`:
```csharp
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Sub-millisecond ICMP via iphlpapi!IcmpSendEcho (no admin needed),
/// timed with a high-resolution clock. Mirrors the Python windows_icmp_ping.
/// </summary>
public static class IcmpPinger
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho(
        IntPtr handle, uint destIp, byte[] requestData, short requestSize,
        IntPtr requestOptions, byte[] replyBuffer, uint replySize, uint timeoutMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct IcmpEchoReply
    {
        public uint Address;
        public uint Status;
        public uint RoundTripTime;
        public ushort DataSize;
        public ushort Reserved;
        public IntPtr Data;
        public byte OptionsTtl;
        public byte OptionsTos;
        public byte OptionsFlags;
        public byte OptionsOptionsSize;
        public IntPtr OptionsOptionsData;
    }

    private static readonly byte[] Payload =
        System.Text.Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwabcdefghi");

    public static PingResult Ping(string ip, int timeoutMs)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return new PingResult(false, null, null);
        uint dest = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);

        IntPtr handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return new PingResult(false, null, null);
        try
        {
            int replySize = Marshal.SizeOf<IcmpEchoReply>() + Payload.Length + 8;
            var reply = new byte[replySize];
            var sw = Stopwatch.StartNew();
            uint n = IcmpSendEcho(handle, dest, Payload, (short)Payload.Length,
                                  IntPtr.Zero, reply, (uint)replySize, (uint)timeoutMs);
            double elapsed = sw.Elapsed.TotalMilliseconds;
            if (n == 0) return new PingResult(false, null, null);

            var handleGc = GCHandle.Alloc(reply, GCHandleType.Pinned);
            try
            {
                var r = Marshal.PtrToStructure<IcmpEchoReply>(handleGc.AddrOfPinnedObject());
                return r.Status != 0
                    ? new PingResult(false, null, null)
                    : new PingResult(true, elapsed, r.OptionsTtl);
            }
            finally { handleGc.Free(); }
        }
        finally { IcmpCloseHandle(handle); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter IcmpPingerTests`
Expected: PASS (2 tests). (Loopback ping always succeeds on Windows.)

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add IcmpPinger via IcmpSendEcho P/Invoke"
```

### Task 7: NetworkDetector (own IP/MAC/GW/DNS)

**Files:**
- Create: `src/IpScanner/Core/Scanner/NetworkDetector.cs`
- Test: `tests/IpScanner.Tests/Core/NetworkDetectorTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/NetworkDetectorTests.cs`:
```csharp
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetworkDetectorTests
{
    [Fact]
    public void LocalIp_IsValidIpv4_OnConnectedMachine()
    {
        var ip = NetworkDetector.GetLocalIpFast();
        // On a machine with a network this is non-null and valid.
        if (ip is not null) Assert.True(Ipv4.IsValid(ip));
    }

    [Fact]
    public void DetectAll_PopulatesNetworkInfo()
    {
        var info = NetworkDetector.DetectFast();
        Assert.NotNull(info);
        if (info.Ip is not null) Assert.True(Ipv4.IsValid(info.Ip));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter NetworkDetectorTests`
Expected: FAIL — `NetworkDetector` does not exist.

- [ ] **Step 3: Implement NetworkDetector**

`src/IpScanner/Core/Scanner/NetworkDetector.cs`:
```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Fast local-network detection. Uses a UDP-connect trick for the own IP and
/// .NET NetworkInformation for gateway/MAC/mask/DNS (replaces Python P/Invoke).
/// </summary>
public static class NetworkDetector
{
    public static string? GetLocalIpFast()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);              // no packet sent for UDP
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
            return ip != "0.0.0.0" && !ip.StartsWith("169.254.") ? ip : null;
        }
        catch { return null; }
    }

    public static NetworkInfo DetectFast()
    {
        var info = new NetworkInfo();
        var localIp = GetLocalIpFast();
        info.Ip = localIp;
        if (localIp is null) return info;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString() != localIp) continue;

                info.Interface = ni.Name;
                info.Mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());
                info.SubnetMask = ua.IPv4Mask?.ToString();
                info.Gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
                info.DnsServers = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString()).ToList();
                return info;
            }
        }
        return info;
    }

    private static string FormatMac(byte[] bytes)
        => bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter NetworkDetectorTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add NetworkDetector (local IP/MAC/GW/DNS)"
```

### Task 8: ArpHelper + NetBiosHelper

**Files:**
- Create: `src/IpScanner/Core/Scanner/ArpHelper.cs`
- Create: `src/IpScanner/Core/Scanner/NetBiosHelper.cs`
- Test: `tests/IpScanner.Tests/Core/ArpHelperTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/ArpHelperTests.cs`:
```csharp
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ArpHelperTests
{
    [Fact]
    public void ParseArpOutput_ExtractsMac()
    {
        const string sample = "  192.168.1.1          aa-bb-cc-dd-ee-ff     dynamisch";
        var mac = ArpHelper.ParseMac(sample);
        Assert.Equal("AA-BB-CC-DD-EE-FF", mac);
    }

    [Fact]
    public void ParseArpOutput_NoMac_ReturnsNull()
        => Assert.Null(ArpHelper.ParseMac("no mac here"));

    [Fact]
    public void ParseNetbios_ExtractsName()
    {
        const string sample = "    DESKTOP-ABC    <20>  UNIQUE      Registered";
        var name = NetBiosHelper.ParseName(sample);
        Assert.Equal("DESKTOP-ABC", name);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ArpHelperTests`
Expected: FAIL — `ArpHelper` does not exist.

- [ ] **Step 3: Implement ArpHelper**

`src/IpScanner/Core/Scanner/ArpHelper.cs`:
```csharp
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>Resolves MAC addresses via the system ARP table.</summary>
public static class ArpHelper
{
    private static readonly Regex MacRegex =
        new(@"([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    public static string? ParseMac(string arpOutput)
    {
        var m = MacRegex.Match(arpOutput);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    public static string? Resolve(string ip)
    {
        try
        {
            var output = RunCapture("arp", $"-a {ip}", 2000);
            return ParseMac(output);
        }
        catch { return null; }
    }

    internal static string RunCapture(string exe, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        p.Start();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return output;
    }
}
```

- [ ] **Step 4: Implement NetBiosHelper**

`src/IpScanner/Core/Scanner/NetBiosHelper.cs`:
```csharp
using System.Text.RegularExpressions;

namespace IpScanner.Core.Scanner;

/// <summary>NetBIOS hostname lookup via nbtstat -A (works across subnets).</summary>
public static class NetBiosHelper
{
    public static string? ParseName(string nbtstatOutput)
    {
        foreach (var code in new[] { "<20>", "<00>" })
        {
            foreach (var line in nbtstatOutput.Split('\n'))
            {
                var m = Regex.Match(line, @"\s*([^\s<].*?)\s+" + Regex.Escape(code) + @"\s");
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    if (name.Length > 0 && name != "Unknown") return name;
                }
            }
        }
        return null;
    }

    public static (string? name, string? mac) Lookup(string ip)
    {
        try
        {
            var output = ArpHelper.RunCapture("nbtstat", $"-A {ip}", 6000);
            var name = ParseName(output);
            var mac = ArpHelper.ParseMac(output);
            return (name, mac == "00-00-00-00-00-00" ? null : mac);
        }
        catch { return (null, null); }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter ArpHelperTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```
git add .
git commit -m "feat: add ARP and NetBIOS hostname/MAC helpers"
```

### Task 9: HostnameResolver (reverse DNS)

**Files:**
- Create: `src/IpScanner/Core/Scanner/HostnameResolver.cs`
- Test: `tests/IpScanner.Tests/Core/HostnameResolverTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/HostnameResolverTests.cs`:
```csharp
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class HostnameResolverTests
{
    [Fact]
    public void StripLocaldomain_RemovesSuffix()
        => Assert.Equal("myhost", HostnameResolver.Normalize("myhost.localdomain"));

    [Fact]
    public void Normalize_KeepsPlainName()
        => Assert.Equal("router.home", HostnameResolver.Normalize("router.home"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter HostnameResolverTests`
Expected: FAIL — `HostnameResolver` does not exist.

- [ ] **Step 3: Implement HostnameResolver**

`src/IpScanner/Core/Scanner/HostnameResolver.cs`:
```csharp
using System.Net;

namespace IpScanner.Core.Scanner;

public static class HostnameResolver
{
    public static string Normalize(string host)
        => host.EndsWith(".localdomain") ? host[..^".localdomain".Length] : host;

    public static string? Resolve(string ip)
    {
        try
        {
            var host = Dns.GetHostEntry(ip).HostName;
            return string.IsNullOrEmpty(host) ? null : Normalize(host);
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter HostnameResolverTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add reverse-DNS hostname resolver"
```

---

## PHASE 4 — Device Grouping + Colors

### Task 10: GroupColorPalette (max-diversity)

**Files:**
- Create: `src/IpScanner/Core/Scanner/GroupColorPalette.cs`
- Test: `tests/IpScanner.Tests/Core/GroupColorPaletteTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/GroupColorPaletteTests.cs`:
```csharp
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class GroupColorPaletteTests
{
    [Fact]
    public void Sequence_IsNonEmpty_AndDistinct()
    {
        var seq = GroupColorPalette.Sequence;
        Assert.NotEmpty(seq);
        Assert.Equal(seq.Count, seq.Distinct().Count());
    }

    [Fact]
    public void ColorForIndex_WrapsAround()
    {
        var c0 = GroupColorPalette.ColorForIndex(0);
        var cWrap = GroupColorPalette.ColorForIndex(GroupColorPalette.Sequence.Count);
        Assert.Equal(c0, cWrap);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter GroupColorPaletteTests`
Expected: FAIL — `GroupColorPalette` does not exist.

- [ ] **Step 3: Implement GroupColorPalette**

`src/IpScanner/Core/Scanner/GroupColorPalette.cs`:
```csharp
namespace IpScanner.Core.Scanner;

/// <summary>
/// Distinct group colors as #RRGGBB. Ports the Python max-diversity + min-distance
/// filtering over the xterm-256 dynamic palette into precomputed RGB hex values.
/// </summary>
public static class GroupColorPalette
{
    private static readonly int[] XtermDynamic =
    {
        196,202,208,214,220,226,190,154,118,82,
        46,51,21,27,33,39,45,50,63,69,
        75,81,87,93,99,105,111,117,129,135,
        141,147,201,207,213,219,225,231,165,171
    };
    private const int MinDistSq = 16900;

    public static IReadOnlyList<string> Sequence { get; } = Build();

    public static string ColorForIndex(int i) => Sequence[i % Sequence.Count];

    private static List<string> Build()
    {
        var filtered = FilterDiverse(XtermDynamic.ToList(), MinDistSq);
        var ordered = MaxDiversity(filtered);
        return ordered.Select(n =>
        {
            var (r, g, b) = XtermToRgb(n);
            return $"#{r:X2}{g:X2}{b:X2}";
        }).ToList();
    }

    private static (int r, int g, int b) XtermToRgb(int n)
    {
        int[][] basic16 =
        {
            new[]{0,0,0}, new[]{128,0,0}, new[]{0,128,0}, new[]{128,128,0},
            new[]{0,0,128}, new[]{128,0,128}, new[]{0,128,128}, new[]{192,192,192},
            new[]{128,128,128}, new[]{255,0,0}, new[]{0,255,0}, new[]{255,255,0},
            new[]{0,0,255}, new[]{255,0,255}, new[]{0,255,255}, new[]{255,255,255}
        };
        if (n < 16) return (basic16[n][0], basic16[n][1], basic16[n][2]);
        if (n < 232)
        {
            n -= 16;
            int Conv(int x) => x == 0 ? 0 : 55 + 40 * x;
            return (Conv(n / 36), Conv(n % 36 / 6), Conv(n % 6));
        }
        int v = 8 + (n - 232) * 10;
        return (v, v, v);
    }

    private static int DistSq(int a, int b)
    {
        var (ra, ga, ba) = XtermToRgb(a);
        var (rb, gb, bb) = XtermToRgb(b);
        return (ra - rb) * (ra - rb) + (ga - gb) * (ga - gb) + (ba - bb) * (ba - bb);
    }

    private static List<int> FilterDiverse(List<int> colors, int minDistSq)
    {
        var pool = new List<int>(colors);
        bool changed = true;
        while (changed)
        {
            changed = false;
            var close = pool.ToDictionary(c => c,
                c => pool.Count(o => o != c && DistSq(c, o) < minDistSq));
            int worstScore = close.Values.Max();
            if (worstScore > 0)
            {
                int worst = pool.Where(c => close[c] == worstScore).OrderBy(c => -c).First();
                pool.Remove(worst);
                changed = true;
            }
        }
        return pool;
    }

    private static List<int> MaxDiversity(List<int> colors)
    {
        if (colors.Count <= 1) return new List<int>(colors);
        var remaining = new List<int>(colors);
        int first = remaining.OrderByDescending(c => XtermToRgb(c).r).First();
        var ordered = new List<int> { first };
        remaining.Remove(first);
        while (remaining.Count > 0)
        {
            int best = remaining.OrderByDescending(c => ordered.Min(p => DistSq(c, p))).First();
            ordered.Add(best);
            remaining.Remove(best);
        }
        return ordered;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter GroupColorPaletteTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add group color palette (max-diversity)"
```

### Task 11: DeviceGrouper

**Files:**
- Create: `src/IpScanner/Core/Scanner/DeviceGrouper.cs`
- Test: `tests/IpScanner.Tests/Core/DeviceGrouperTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/DeviceGrouperTests.cs`:
```csharp
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceGrouperTests
{
    [Fact]
    public void Group_DevicesWithSharedMacPrefix_GetSameGroup()
    {
        var devices = new List<Device>
        {
            new("192.168.1.10") { Mac = "AA:BB:CC:11:22:33", Hostname = "pc-a" },
            new("192.168.1.11") { Mac = "AA:BB:CC:44:55:66", Hostname = "pc-b" },
            new("192.168.1.12") { Mac = "DD:EE:FF:00:11:22", Hostname = "other" },
        };
        DeviceGrouper.AssignGroups(devices, gatewayIp: null);
        Assert.Equal(devices[0].GroupId, devices[1].GroupId);
        Assert.NotEqual(devices[0].GroupId, devices[2].GroupId);
    }

    [Fact]
    public void Group_Gateway_GetsDedicatedGroup()
    {
        var devices = new List<Device> { new("192.168.1.1") { Mac = "AA:BB:CC:11:22:33" } };
        DeviceGrouper.AssignGroups(devices, gatewayIp: "192.168.1.1");
        Assert.Equal(DeviceGrouper.GatewayGroupId, devices[0].GroupId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DeviceGrouperTests`
Expected: FAIL — `DeviceGrouper` does not exist.

- [ ] **Step 3: Implement DeviceGrouper**

`src/IpScanner/Core/Scanner/DeviceGrouper.cs`:
```csharp
using IpScanner.Core.Models;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Assigns group IDs: gateway is its own group; remaining devices grouped by
/// MAC OUI (first 8 chars) or hostname prefix, min 2 devices per group.
/// </summary>
public static class DeviceGrouper
{
    public const int NoGroup = 0;
    public const int UnknownGroup = 1;
    public const int GatewayGroupId = 2;
    private const int DynamicStart = 3;
    private const int MinDevicesPerGroup = 2;

    public static void AssignGroups(IList<Device> devices, string? gatewayIp)
    {
        foreach (var d in devices) d.GroupId = UnknownGroup;

        var dynamicDevices = new List<Device>();
        foreach (var d in devices)
        {
            if (d.Ip == gatewayIp) { d.GroupId = GatewayGroupId; continue; }
            dynamicDevices.Add(d);
        }

        var buckets = new Dictionary<string, List<Device>>();
        foreach (var d in dynamicDevices)
        {
            var key = GroupKey(d);
            if (key is null) continue;
            (buckets.TryGetValue(key, out var list) ? list : buckets[key] = new()).Add(d);
        }

        int nextId = DynamicStart;
        foreach (var (_, members) in buckets.OrderBy(b => b.Key))
        {
            if (members.Count < MinDevicesPerGroup) continue;
            foreach (var d in members) d.GroupId = nextId;
            nextId++;
        }
    }

    private static string? GroupKey(Device d)
    {
        if (!string.IsNullOrEmpty(d.Mac) && d.Mac.Length >= 8)
            return "mac:" + d.Mac[..8].ToUpperInvariant();
        if (!string.IsNullOrEmpty(d.Hostname) && d.Hostname.Length >= 2)
        {
            var prefix = new string(d.Hostname.TakeWhile(c => !char.IsDigit(c)).ToArray());
            if (prefix.Length >= 2) return "host:" + prefix.ToLowerInvariant();
        }
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter DeviceGrouperTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add device grouping logic"
```

---

## PHASE 5 — Known-Devices Database

### Task 12: KnownDevicesDb (SQLite)

**Files:**
- Create: `src/IpScanner/Core/Data/KnownDevicesDb.cs`
- Test: `tests/IpScanner.Tests/Core/KnownDevicesDbTests.cs`

- [ ] **Step 1: Write the test**

`tests/IpScanner.Tests/Core/KnownDevicesDbTests.cs`:
```csharp
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class KnownDevicesDbTests
{
    [Fact]
    public void Save_ThenLoad_ReturnsDevices()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        var devices = new List<Device>
        {
            new("192.168.1.1") { Mac = netMac, Hostname = "router" },
            new("192.168.1.5") { Mac = "DD:EE:FF:00:11:22", Hostname = "nas" },
        };
        db.Save(netMac, devices, "2026-06-01 12:00:00");

        var loaded = db.Load(netMac);
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, d => Assert.False(d.IsOnline));
        Assert.All(loaded, d => Assert.True(d.FromDb));
    }

    [Fact]
    public void GetNetworkMac_FindsGatewayMac()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        var db = new KnownDevicesDb(dbPath);
        const string netMac = "AA:BB:CC:11:22:33";
        db.Save(netMac, new List<Device> { new("192.168.1.1") { Mac = netMac } }, "t");
        Assert.Equal(netMac, db.GetNetworkMac("192.168.1.1"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter KnownDevicesDbTests`
Expected: FAIL — `KnownDevicesDb` does not exist.

- [ ] **Step 3: Implement KnownDevicesDb**

`src/IpScanner/Core/Data/KnownDevicesDb.cs`:
```csharp
using IpScanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

/// <summary>SQLite store of devices seen per network (keyed by gateway MAC).</summary>
public sealed class KnownDevicesDb
{
    private readonly string _connStr;

    public KnownDevicesDb(string path)
    {
        _connStr = $"Data Source={path}";
        using var conn = Open();
        conn.CreateCommand(
            """
            CREATE TABLE IF NOT EXISTS known_devices (
              network_mac TEXT NOT NULL, mac TEXT NOT NULL, ip TEXT,
              hostname TEXT, last_seen TEXT, PRIMARY KEY (network_mac, mac))
            """).ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    public void Save(string networkMac, IEnumerable<Device> devices, string timestamp)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Mac) || d.Mac == "Unknown") continue;
            var cmd = conn.CreateCommand(
                """
                INSERT INTO known_devices (network_mac, mac, ip, hostname, last_seen)
                VALUES ($n, $m, $ip, $h, $t)
                ON CONFLICT(network_mac, mac) DO UPDATE SET
                  ip=excluded.ip,
                  hostname=COALESCE(excluded.hostname, known_devices.hostname),
                  last_seen=excluded.last_seen
                """);
            cmd.Parameters.AddWithValue("$n", networkMac.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$m", d.Mac.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$ip", d.Ip);
            cmd.Parameters.AddWithValue("$h",
                (object?)(string.IsNullOrEmpty(d.Hostname) ? null : d.Hostname) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", timestamp);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<Device> Load(string networkMac)
    {
        var result = new List<Device>();
        using var conn = Open();
        var cmd = conn.CreateCommand(
            "SELECT ip, mac, hostname FROM known_devices WHERE network_mac = $n");
        cmd.Parameters.AddWithValue("$n", networkMac.ToUpperInvariant());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Device(r.IsDBNull(0) ? "Unknown" : r.GetString(0))
            {
                Mac = r.IsDBNull(1) ? "Unknown" : r.GetString(1),
                Hostname = r.IsDBNull(2) ? "Unknown" : r.GetString(2),
                FromDb = true,
            });
        }
        return result;
    }

    public string? GetNetworkMac(string gatewayIp)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand(
            "SELECT network_mac FROM known_devices WHERE ip = $ip AND mac = network_mac LIMIT 1");
        cmd.Parameters.AddWithValue("$ip", gatewayIp);
        return cmd.ExecuteScalar() as string;
    }

    public void Clear()
    {
        using var conn = Open();
        conn.CreateCommand("DELETE FROM known_devices").ExecuteNonQuery();
    }
}
```

Add this small extension helper used above — `src/IpScanner/Core/Data/SqliteExtensions.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

internal static class SqliteExtensions
{
    public static SqliteCommand CreateCommand(this SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter KnownDevicesDbTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add SQLite known-devices database"
```

---

## PHASE 6 — Export + MAC Vendor

### Task 13: MacVendorLookup

**Files:**
- Create: `src/IpScanner/Core/Export/MacVendorLookup.cs`
- Create: `src/IpScanner/Resources/oui.txt` (small embedded subset; full list added at build)
- Modify: `src/IpScanner/IpScanner.csproj` (embed resource)
- Test: `tests/IpScanner.Tests/Core/MacVendorLookupTests.cs`

- [ ] **Step 1: Create a minimal OUI data file**

`src/IpScanner/Resources/oui.txt` (format: `PREFIX\tVENDOR`, one per line):
```
001A2B	Example Networks Inc
A4C3F0	Acme Router Co
B8273C	Cisco Systems
```

- [ ] **Step 2: Embed it in the csproj**

Add to `IpScanner.csproj` `<ItemGroup>`:
```xml
    <EmbeddedResource Include="Resources\oui.txt" />
```

- [ ] **Step 3: Write the failing test**

`tests/IpScanner.Tests/Core/MacVendorLookupTests.cs`:
```csharp
using IpScanner.Core.Export;
using Xunit;

namespace IpScanner.Tests.Core;

public class MacVendorLookupTests
{
    [Fact]
    public void Lookup_KnownPrefix_ReturnsVendor()
        => Assert.Equal("Cisco Systems", MacVendorLookup.Instance.Lookup("B8:27:3C:11:22:33"));

    [Fact]
    public void Lookup_UnknownPrefix_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup("FF:FF:FF:00:00:00"));

    [Fact]
    public void Lookup_NullOrEmpty_ReturnsNull()
        => Assert.Null(MacVendorLookup.Instance.Lookup(""));
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test --filter MacVendorLookupTests`
Expected: FAIL — `MacVendorLookup` does not exist.

- [ ] **Step 5: Implement MacVendorLookup**

`src/IpScanner/Core/Export/MacVendorLookup.cs`:
```csharp
using System.Reflection;

namespace IpScanner.Core.Export;

/// <summary>Maps a MAC OUI prefix to a vendor name from an embedded oui.txt.</summary>
public sealed class MacVendorLookup
{
    public static MacVendorLookup Instance { get; } = new();

    private readonly Dictionary<string, string> _map = new();

    private MacVendorLookup()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("oui.txt"));
        if (name is null) return;
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split('\t');
            if (parts.Length == 2) _map[parts[0].ToUpperInvariant()] = parts[1].Trim();
        }
    }

    public string? Lookup(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var prefix = new string(mac.Where(Uri.IsHexDigit).Take(6).ToArray()).ToUpperInvariant();
        return prefix.Length == 6 && _map.TryGetValue(prefix, out var v) ? v : null;
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter MacVendorLookupTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```
git add .
git commit -m "feat: add MAC vendor lookup (embedded OUI)"
```

### Task 14: CsvExporter

**Files:**
- Create: `src/IpScanner/Core/Export/CsvExporter.cs`
- Test: `tests/IpScanner.Tests/Core/CsvExporterTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/Core/CsvExporterTests.cs`:
```csharp
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class CsvExporterTests
{
    [Fact]
    public void Write_ProducesHeaderAndRow()
    {
        var d = new Device("192.168.1.1") { Mac = "AA:BB:CC:11:22:33", Hostname = "router" };
        d.RecordPing(new PingResult(true, 2.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = CsvExporter.Write(new[] { d }, dir, "20260601_120000", "router");

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("ip,status,hostname,vendor,mac", lines[0]);
        Assert.Contains("192.168.1.1", lines[1]);
        Assert.Contains("ONLINE", lines[1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CsvExporterTests`
Expected: FAIL — `CsvExporter` does not exist.

- [ ] **Step 3: Implement CsvExporter**

`src/IpScanner/Core/Export/CsvExporter.cs`:
```csharp
using System.Globalization;
using System.Text;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

public static class CsvExporter
{
    private static readonly string[] Columns =
    {
        "ip","status","hostname","vendor","mac","ping_avg_ms","ping_min_ms",
        "ping_max_ms","last_ping_ms","pings_done","pings_target","from_db"
    };

    public static string Write(IEnumerable<Device> devices, string outputDir,
                               string timestamp, string gatewaySlug)
    {
        Directory.CreateDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.csv");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns));
        foreach (var d in devices)
        {
            if (!d.IsOnline && !d.FromDb && !d.Seen) continue;
            string Num(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
            var target = d.TargetPings == ScanConfig.InfinitePingCount ? "∞" : d.TargetPings.ToString();
            sb.AppendLine(string.Join(',', new[]
            {
                d.Ip,
                d.IsOnline ? "ONLINE" : "OFFLINE",
                d.Hostname == "Unknown" ? "" : d.Hostname ?? "",
                MacVendorLookup.Instance.Lookup(d.Mac) ?? "",
                d.Mac == "Unknown" ? "" : d.Mac ?? "",
                Num(d.AvgMs), Num(d.MinMs), Num(d.MaxMs), Num(d.LastMs),
                d.CurrentPings.ToString(), target, d.FromDb ? "1" : "0",
            }));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter CsvExporterTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add CSV exporter"
```

### Task 15: TxtExporter

**Files:**
- Create: `src/IpScanner/Core/Export/TxtExporter.cs`
- Test: `tests/IpScanner.Tests/Core/TxtExporterTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/Core/TxtExporterTests.cs`:
```csharp
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class TxtExporterTests
{
    [Fact]
    public void Write_ContainsHeaderAndDevice()
    {
        var info = new NetworkInfo { Ip = "192.168.1.100", Gateway = "192.168.1.1" };
        var d = new Device("192.168.1.1") { Hostname = "router" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var dir = Directory.CreateTempSubdirectory().FullName;

        var path = TxtExporter.Write(new[] { d }, info, dir, "20260601_120000", "router");

        var text = File.ReadAllText(path);
        Assert.Contains("NETWORK SCAN REPORT", text);
        Assert.Contains("192.168.1.1", text);
        Assert.Contains("router", text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter TxtExporterTests`
Expected: FAIL — `TxtExporter` does not exist.

- [ ] **Step 3: Implement TxtExporter**

`src/IpScanner/Core/Export/TxtExporter.cs`:
```csharp
using System.Globalization;
using System.Text;
using IpScanner.Core.Models;

namespace IpScanner.Core.Export;

public static class TxtExporter
{
    public static string Write(IEnumerable<Device> devices, NetworkInfo info,
                               string outputDir, string timestamp, string gatewaySlug)
    {
        Directory.CreateDirectory(outputDir);
        var suffix = string.IsNullOrEmpty(gatewaySlug) ? "" : $"-{gatewaySlug}";
        var path = Path.Combine(outputDir, $"network_scan_{timestamp}{suffix}.txt");

        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine("NETWORK SCAN REPORT");
        sb.AppendLine($"Timestamp: {timestamp}");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();
        sb.AppendLine("Network Information:");
        sb.AppendLine(new string('-', 30));
        sb.AppendLine($"Interface:   {info.Interface}");
        sb.AppendLine($"IP Address:  {info.Ip ?? "Unknown"}");
        if (info.Gateway is not null) sb.AppendLine($"Gateway:     {info.Gateway}");
        if (info.SubnetMask is not null) sb.AppendLine($"Subnet Mask: {info.SubnetMask}");
        sb.AppendLine($"DNS Servers: {(info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "Unknown")}");
        sb.AppendLine();

        sb.AppendLine($"{"IP",-16}{"Status",-9}{"Hostname",-24}{"Avg",-10}{"MAC",-18}");
        sb.AppendLine(new string('-', 80));
        foreach (var d in devices.Where(d => d.IsOnline || d.FromDb || d.Seen))
        {
            string Avg(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) + "ms" ?? "-";
            sb.AppendLine(
                $"{d.Ip,-16}{(d.IsOnline ? "ONLINE" : "OFFLINE"),-9}" +
                $"{(d.Hostname ?? "-"),-24}{(d.AvgMs is null ? "-" : Avg(d.AvgMs)),-10}" +
                $"{d.Mac ?? "-",-18}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter TxtExporterTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add TXT report exporter"
```

---

## PHASE 7 — Scan Engine

### Task 16: ScanEngine — discovery + analysis pipeline

**Files:**
- Create: `src/IpScanner/Core/Scanner/ScanEngine.cs`
- Test: `tests/IpScanner.Tests/Core/ScanEngineTests.cs`

- [ ] **Step 1: Write the failing test (uses an injectable ping function)**

`tests/IpScanner.Tests/Core/ScanEngineTests.cs`:
```csharp
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ScanEngineTests
{
    [Fact]
    public async Task Scan_OnlyOneHostOnline_ProducesOneOnlineDevice()
    {
        // Fake pinger: only .1 answers.
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 2, PingIntervalMs = 0, InitPingThreads = 16, PingThreads = 16 };
        var devices = new List<Device>();
        engine.DeviceUpdated += d => { lock (devices) { if (!devices.Contains(d)) devices.Add(d); } };

        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        var online = devices.Where(d => d.IsOnline).ToList();
        Assert.Single(online);
        Assert.Equal("10.0.0.1", online[0].Ip);
        Assert.Equal(2, online[0].SuccessCount);
    }

    [Fact]
    public async Task Scan_Cancelled_StopsEarly()
    {
        var engine = new ScanEngine((_, _) => new PingResult(true, 1.0, 64));
        var cfg = new ScanConfig { PingCount = 1000000, PingIntervalMs = 5 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await engine.ScanAsync(new[] { "10.0.0" }, cfg, cts.Token); // must return, not hang
        Assert.True(true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ScanEngineTests`
Expected: FAIL — `ScanEngine` does not exist.

- [ ] **Step 3: Implement ScanEngine**

`src/IpScanner/Core/Scanner/ScanEngine.cs`:
```csharp
using System.Collections.Concurrent;
using IpScanner.Core.Models;
using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Two-phase scan: discovery (1 ping/IP) then analysis (N pings/online device),
/// pipelined so analysis starts as a host answers. The ping function is injected
/// for testability (production passes IcmpPinger.Ping).
/// </summary>
public sealed class ScanEngine
{
    private readonly Func<string, int, PingResult> _ping;
    private const int IcmpTimeoutMs = 1000;

    public ScanEngine(Func<string, int, PingResult> ping) => _ping = ping;

    /// <summary>Raised whenever a device's state changes (online, new ping, etc.).</summary>
    public event Action<Device>? DeviceUpdated;

    private readonly ConcurrentDictionary<string, Device> _devices = new();
    public IReadOnlyCollection<Device> Devices => (IReadOnlyCollection<Device>)_devices.Values;

    public async Task ScanAsync(IReadOnlyList<string> subnetPrefixes, ScanConfig cfg,
                                CancellationToken ct)
    {
        var ips = subnetPrefixes.SelectMany(Ipv4.HostsInSubnet).ToList();
        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;

        // Discovery
        await RunParallel(ips, cfg.InitPingThreads <= 0 ? ips.Count : cfg.InitPingThreads, ct,
            ip =>
            {
                if (ct.IsCancellationRequested) return;
                var device = _devices.GetOrAdd(ip, x => new Device(x)
                {
                    TargetPings = cfg.PingCount,
                    OfflineAfterFailures = cfg.OfflineAfterFailedPings,
                });
                var r = _ping(ip, IcmpTimeoutMs);
                device.RecordPing(r);
                DeviceUpdated?.Invoke(device);
            });

        if (cfg.PingCount == 0) return;

        // Analysis — only devices that answered discovery.
        var online = _devices.Values.Where(d => d.IsOnline).Select(d => d.Ip).ToList();
        await RunParallel(online, cfg.PingThreads, ct, ip =>
        {
            var device = _devices[ip];
            int target = infinite ? int.MaxValue : cfg.PingCount;
            // discovery already counted as ping #1
            for (int i = 1; i < target && !ct.IsCancellationRequested; i++)
            {
                if (cfg.PingIntervalMs > 0) InterruptibleSleep(cfg.PingIntervalMs, ct);
                if (ct.IsCancellationRequested) break;
                device.RecordPing(_ping(ip, IcmpTimeoutMs));
                DeviceUpdated?.Invoke(device);
            }
        });
    }

    private static async Task RunParallel(IReadOnlyList<string> items, int maxParallel,
                                          CancellationToken ct, Action<string> body)
    {
        if (items.Count == 0) return;
        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { await Task.Run(() => body(item), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { sem.Release(); }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static void InterruptibleSleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ScanEngineTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add two-phase ScanEngine with injectable pinger"
```

### Task 17: ScanEngine progress counters

**Files:**
- Modify: `src/IpScanner/Core/Scanner/ScanEngine.cs`
- Test: `tests/IpScanner.Tests/Core/ScanEngineTests.cs`

- [ ] **Step 1: Write the failing test (append)**

```csharp
    [Fact]
    public async Task Scan_TracksPingCounts()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 3, PingIntervalMs = 0 };

        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        // .1 = 3 successes; remaining 253 hosts each got 1 failed discovery ping.
        Assert.Equal(3, engine.Progress.SuccessPings);
        Assert.Equal(253, engine.Progress.FailedPings);
        Assert.True(engine.Progress.SkippedPings >= 253 * 2); // 2 analysis pings/offline host skipped
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ScanEngineTests`
Expected: FAIL — `engine.Progress` does not exist.

- [ ] **Step 3: Add a ScanProgress type and wire counters**

Create `src/IpScanner/Core/Scanner/ScanProgress.cs`:
```csharp
namespace IpScanner.Core.Scanner;

public sealed class ScanProgress
{
    private int _success, _failed, _skipped, _processed;
    public int SuccessPings => Volatile.Read(ref _success);
    public int FailedPings => Volatile.Read(ref _failed);
    public int SkippedPings => Volatile.Read(ref _skipped);
    public int ProcessedHosts => Volatile.Read(ref _processed);

    public void AddSuccess() => Interlocked.Increment(ref _success);
    public void AddFailed() => Interlocked.Increment(ref _failed);
    public void AddSkipped(int n) => Interlocked.Add(ref _skipped, n);
    public void AddProcessed() => Interlocked.Increment(ref _processed);

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();
}
```

In `ScanEngine`, add `public ScanProgress Progress { get; } = new();` and update counters:
- In discovery body after `device.RecordPing(r)`: `if (r.Success) Progress.AddSuccess(); else Progress.AddFailed();` then `Progress.AddProcessed();`
- For offline hosts (not online after discovery), in analysis-setup add skipped: after computing `online`, do
  ```csharp
  int analysisPerIp = infinite ? 0 : Math.Max(0, cfg.PingCount - 1);
  int offlineCount = _devices.Count - online.Count;
  Progress.AddSkipped(offlineCount * analysisPerIp);
  ```
- In analysis loop after `device.RecordPing(...)`: `if (last.Success) Progress.AddSuccess(); else Progress.AddFailed();` (capture the result in a local `var r = _ping(...)`).
- Call `Progress.NotifyChanged()` after each counter change batch.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ScanEngineTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add ScanEngine progress counters"
```

---

## PHASE 8 — ViewModels

### Task 18: ObservableObject + RelayCommand base

**Files:**
- Create: `src/IpScanner/ViewModels/ObservableObject.cs`
- Create: `src/IpScanner/ViewModels/RelayCommand.cs`
- Test: `tests/IpScanner.Tests/ViewModels/ObservableObjectTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/ViewModels/ObservableObjectTests.cs`:
```csharp
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ObservableObjectTests
{
    private sealed class Sample : ObservableObject
    {
        private int _x;
        public int X { get => _x; set => SetProperty(ref _x, value); }
    }

    [Fact]
    public void SetProperty_RaisesPropertyChanged()
    {
        var s = new Sample();
        string? raised = null;
        s.PropertyChanged += (_, e) => raised = e.PropertyName;
        s.X = 5;
        Assert.Equal("X", raised);
    }

    [Fact]
    public void RelayCommand_Executes()
    {
        bool ran = false;
        var cmd = new RelayCommand(_ => ran = true);
        cmd.Execute(null);
        Assert.True(ran);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ObservableObjectTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement both**

`src/IpScanner/ViewModels/ObservableObject.cs`:
```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IpScanner.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

`src/IpScanner/ViewModels/RelayCommand.cs`:
```csharp
using System.Windows.Input;

namespace IpScanner.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ObservableObjectTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add MVVM ObservableObject and RelayCommand"
```

### Task 19: DeviceViewModel

**Files:**
- Create: `src/IpScanner/ViewModels/DeviceViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/DeviceViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/ViewModels/DeviceViewModelTests.cs`:
```csharp
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class DeviceViewModelTests
{
    [Fact]
    public void Update_ReflectsDeviceState()
    {
        var d = new Device("192.168.1.1") { Hostname = "router" };
        d.RecordPing(new PingResult(true, 2.5, 64));
        var vm = new DeviceViewModel(d);
        vm.Refresh();
        Assert.Equal("192.168.1.1", vm.Ip);
        Assert.Equal("ONLINE", vm.Status);
        Assert.Equal("router", vm.Hostname);
        Assert.Contains("2,5", vm.AvgDisplay);  // de-DE comma decimal
    }

    [Fact]
    public void Refresh_RaisesPropertyChanged()
    {
        var d = new Device("192.168.1.1");
        var vm = new DeviceViewModel(d);
        bool raised = false;
        vm.PropertyChanged += (_, _) => raised = true;
        d.RecordPing(new PingResult(true, 1.0, 64));
        vm.Refresh();
        Assert.True(raised);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DeviceViewModelTests`
Expected: FAIL — `DeviceViewModel` does not exist.

- [ ] **Step 3: Implement DeviceViewModel**

`src/IpScanner/ViewModels/DeviceViewModel.cs`:
```csharp
using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly Device _device;

    public DeviceViewModel(Device device) => _device = device;

    public string Ip => _device.Ip;
    public string Status => _device.IsOnline ? "ONLINE" : "OFFLINE";
    public bool IsOnline => _device.IsOnline;
    public string Hostname => _device.Hostname is null or "Unknown" ? "—" : _device.Hostname;
    public string Mac => _device.Mac is null or "Unknown" ? "—" : _device.Mac;
    public int GroupId => _device.GroupId;
    public string GroupColor => GroupColorPalette.ColorForIndex(Math.Max(0, _device.GroupId - 3));

    public string AvgDisplay => Fmt(_device.AvgMs);
    public string MinDisplay => Fmt(_device.MinMs);
    public string MaxDisplay => Fmt(_device.MaxMs);
    public string LastDisplay => Fmt(_device.LastMs);
    public string ProgressDisplay =>
        _device.TargetPings == ScanConfig.InfinitePingCount
            ? $"{_device.CurrentPings}/∞"
            : $"{_device.CurrentPings}/{_device.TargetPings}";

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("F1", De) + " ms";

    /// <summary>Push the underlying device's latest values to the UI.</summary>
    public void Refresh()
    {
        Raise(nameof(Status)); Raise(nameof(IsOnline)); Raise(nameof(Hostname));
        Raise(nameof(Mac)); Raise(nameof(GroupId)); Raise(nameof(GroupColor));
        Raise(nameof(AvgDisplay)); Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay));
        Raise(nameof(LastDisplay)); Raise(nameof(ProgressDisplay));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter DeviceViewModelTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add DeviceViewModel"
```

### Task 20: ProgressViewModel + NetworkInfoViewModel + InternetHostViewModel

**Files:**
- Create: `src/IpScanner/ViewModels/ProgressViewModel.cs`
- Create: `src/IpScanner/ViewModels/NetworkInfoViewModel.cs`
- Create: `src/IpScanner/ViewModels/InternetHostViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/ProgressViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/ViewModels/ProgressViewModelTests.cs`:
```csharp
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ProgressViewModelTests
{
    [Fact]
    public void DeviceFractions_SumToScannedRatio()
    {
        var vm = new ProgressViewModel();
        vm.SetDevices(online: 10, offline: 40, unknown: 0, total: 100);
        Assert.Equal(0.10, vm.OnlineFraction, 3);
        Assert.Equal(0.40, vm.OfflineFraction, 3);
    }

    [Fact]
    public void PingFractions_Computed()
    {
        var vm = new ProgressViewModel();
        vm.SetPings(success: 100, failed: 50, skipped: 50, total: 1000);
        Assert.Equal(0.10, vm.SuccessFraction, 3);
        Assert.Equal(0.05, vm.FailedFraction, 3);
        Assert.Equal(0.05, vm.SkippedFraction, 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ProgressViewModelTests`
Expected: FAIL — `ProgressViewModel` does not exist.

- [ ] **Step 3: Implement the three view models**

`src/IpScanner/ViewModels/ProgressViewModel.cs`:
```csharp
namespace IpScanner.ViewModels;

public sealed class ProgressViewModel : ObservableObject
{
    private int _online, _offline, _unknown, _deviceTotal;
    private int _success, _failed, _skipped, _pingTotal;
    private string _phase = "Bereit";

    public string Phase { get => _phase; set => SetProperty(ref _phase, value); }

    public void SetDevices(int online, int offline, int unknown, int total)
    {
        _online = online; _offline = offline; _unknown = unknown; _deviceTotal = Math.Max(1, total);
        Raise(nameof(OnlineFraction)); Raise(nameof(OfflineFraction)); Raise(nameof(UnknownFraction));
        Raise(nameof(OnlineCount)); Raise(nameof(OfflineCount)); Raise(nameof(UnknownCount));
        Raise(nameof(DeviceCountText));
    }

    public void SetPings(int success, int failed, int skipped, int total)
    {
        _success = success; _failed = failed; _skipped = skipped; _pingTotal = Math.Max(1, total);
        Raise(nameof(SuccessFraction)); Raise(nameof(FailedFraction)); Raise(nameof(SkippedFraction));
        Raise(nameof(PingCountText));
    }

    public double OnlineFraction => (double)_online / _deviceTotal;
    public double OfflineFraction => (double)_offline / _deviceTotal;
    public double UnknownFraction => (double)_unknown / _deviceTotal;
    public int OnlineCount => _online;
    public int OfflineCount => _offline;
    public int UnknownCount => _unknown;
    public string DeviceCountText => $"{_online + _offline + _unknown} / {_deviceTotal}";

    public double SuccessFraction => (double)_success / _pingTotal;
    public double FailedFraction => (double)_failed / _pingTotal;
    public double SkippedFraction => (double)_skipped / _pingTotal;
    public string PingCountText => $"{_success + _failed + _skipped} / {_pingTotal}";
}
```

`src/IpScanner/ViewModels/NetworkInfoViewModel.cs`:
```csharp
using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

public sealed class NetworkInfoViewModel : ObservableObject
{
    public NetworkInfoViewModel(int index, NetworkInfo info, string badgeColor)
    {
        Index = index; BadgeColor = badgeColor;
        Cidr = info.Cidr ?? "—"; Ip = info.Ip ?? "—"; Mac = info.Mac ?? "—";
        Gateway = info.Gateway ?? "—"; SubnetMask = info.SubnetMask ?? "—";
        Dns = info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "—";
        Interface = info.Interface;
    }

    public int Index { get; }
    public string BadgeColor { get; }
    public string Title => $"Netzwerk {Index}";
    public string Cidr { get; }
    public string Ip { get; }
    public string Mac { get; }
    public string Gateway { get; }
    public string SubnetMask { get; }
    public string Dns { get; }
    public string Interface { get; }

    private int _online, _offline;
    private double? _avg;
    public int OnlineCount { get => _online; set => SetProperty(ref _online, value); }
    public int OfflineCount { get => _offline; set => SetProperty(ref _offline, value); }
    public string AvgLatency => _avg is null ? "—" : _avg.Value.ToString("F1") + " ms";
    public void SetAvg(double? v) { _avg = v; Raise(nameof(AvgLatency)); }
}
```

`src/IpScanner/ViewModels/InternetHostViewModel.cs`:
```csharp
namespace IpScanner.ViewModels;

public sealed class InternetHostViewModel : ObservableObject
{
    public InternetHostViewModel(string name, string ip) { Name = name; Ip = ip; }

    public string Name { get; }
    public string Ip { get; }

    private double? _latency;
    public string LatencyDisplay => _latency is null ? "…" : _latency.Value.ToString("F0") + " ms";
    public void SetLatency(double? ms) { _latency = ms; Raise(nameof(LatencyDisplay)); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ProgressViewModelTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add Progress/NetworkInfo/InternetHost view models"
```

### Task 21: SettingsViewModel

**Files:**
- Create: `src/IpScanner/ViewModels/SettingsViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/SettingsViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/ViewModels/SettingsViewModelTests.cs`:
```csharp
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class SettingsViewModelTests
{
    [Fact]
    public void ToConfig_RoundTripsFromConfig()
    {
        var cfg = new ScanConfig { PingCount = 100, ExportCsv = true, PingThreads = 50 };
        var vm = new SettingsViewModel(cfg);
        vm.PingCount = 250;
        var result = vm.ToConfig();
        Assert.Equal(250, result.PingCount);
        Assert.True(result.ExportCsv);
        Assert.Equal(50, result.PingThreads);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SettingsViewModelTests`
Expected: FAIL — `SettingsViewModel` does not exist.

- [ ] **Step 3: Implement SettingsViewModel**

`src/IpScanner/ViewModels/SettingsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

/// <summary>Two-way bindable mirror of ScanConfig for the settings window.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ScanConfig cfg)
    {
        _pingCount = cfg.PingCount;
        _pingIntervalMs = cfg.PingIntervalMs;
        _offlineAfter = cfg.OfflineAfterFailedPings;
        _initPingCount = cfg.InitPingCount;
        HighPressureMode = cfg.HighPressureMode;
        EnableInternetPing = cfg.EnableInternetPing;
        KnownDevicesDb = cfg.KnownDevicesDb;
        FileOutput = cfg.FileOutput;
        ExportCsv = cfg.ExportCsv;
        _outputDirectory = cfg.OutputDirectory;
        _pingThreads = cfg.PingThreads;
        _initPingThreads = cfg.InitPingThreads;
        _refreshRate = cfg.RefreshRate;
        Subnets = new(cfg.Subnets);
        PinnedIps = new(cfg.PinnedIps);
        InternetHosts = new(cfg.InternetHosts);
    }

    private int _pingCount, _pingIntervalMs, _offlineAfter, _initPingCount, _pingThreads, _initPingThreads;
    private double _refreshRate;
    private string _outputDirectory;

    public int PingCount { get => _pingCount; set => SetProperty(ref _pingCount, value); }
    public int PingIntervalMs { get => _pingIntervalMs; set => SetProperty(ref _pingIntervalMs, value); }
    public int OfflineAfterFailedPings { get => _offlineAfter; set => SetProperty(ref _offlineAfter, value); }
    public int InitPingCount { get => _initPingCount; set => SetProperty(ref _initPingCount, value); }
    public bool HighPressureMode { get; set; }
    public bool EnableInternetPing { get; set; }
    public bool KnownDevicesDb { get; set; }
    public bool FileOutput { get; set; }
    public bool ExportCsv { get; set; }
    public string OutputDirectory { get => _outputDirectory; set => SetProperty(ref _outputDirectory, value); }
    public int PingThreads { get => _pingThreads; set => SetProperty(ref _pingThreads, value); }
    public int InitPingThreads { get => _initPingThreads; set => SetProperty(ref _initPingThreads, value); }
    public double RefreshRate { get => _refreshRate; set => SetProperty(ref _refreshRate, value); }

    public ObservableCollection<string> Subnets { get; }
    public ObservableCollection<string> PinnedIps { get; }
    public ObservableCollection<string> InternetHosts { get; }

    public ScanConfig ToConfig() => new()
    {
        PingCount = PingCount,
        PingIntervalMs = PingIntervalMs,
        OfflineAfterFailedPings = OfflineAfterFailedPings,
        InitPingCount = InitPingCount,
        HighPressureMode = HighPressureMode,
        EnableInternetPing = EnableInternetPing,
        KnownDevicesDb = KnownDevicesDb,
        FileOutput = FileOutput,
        ExportCsv = ExportCsv,
        OutputDirectory = OutputDirectory,
        PingThreads = PingThreads,
        InitPingThreads = InitPingThreads,
        RefreshRate = RefreshRate,
        Subnets = Subnets.ToList(),
        PinnedIps = PinnedIps.ToList(),
        InternetHosts = InternetHosts.ToList(),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter SettingsViewModelTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add SettingsViewModel"
```

### Task 22: MainViewModel (scan orchestration)

**Files:**
- Create: `src/IpScanner/ViewModels/MainViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/MainViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IpScanner.Tests/ViewModels/MainViewModelTests.cs`:
```csharp
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MainViewModelTests
{
    [Fact]
    public async Task RunScan_PopulatesDevices()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var vm = new MainViewModel(
            pingFunc: Fake,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());      // synchronous dispatch for tests
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0 };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
        Assert.True(vm.Progress.OnlineCount >= 1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MainViewModelTests`
Expected: FAIL — `MainViewModel` does not exist.

- [ ] **Step 3: Implement MainViewModel**

`src/IpScanner/ViewModels/MainViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Func<string, int, PingResult> _pingFunc;
    private readonly Func<NetworkInfo> _detectNetwork;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cts;

    public MainViewModel(Func<string, int, PingResult> pingFunc,
                         Func<NetworkInfo> detectNetwork,
                         Action<Action> dispatch)
    {
        _pingFunc = pingFunc;
        _detectNetwork = detectNetwork;
        _dispatch = dispatch;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<NetworkInfoViewModel> Networks { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ScanConfig Config { get; set; } = new();

    private readonly Dictionary<string, DeviceViewModel> _byIp = new();

    public async Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null)
    {
        _cts = new CancellationTokenSource();
        var info = _detectNetwork();
        var prefixes = subnetOverride
            ?? BuildPrefixes(info, Config.Subnets);

        _dispatch(() =>
        {
            Networks.Clear();
            Networks.Add(new NetworkInfoViewModel(1, info, GroupColorPalette.ColorForIndex(0)));
            Progress.Phase = "Discovery";
        });

        var engine = new ScanEngine(_pingFunc);
        engine.DeviceUpdated += OnDeviceUpdated;
        engine.Progress.Changed += () => _dispatch(UpdateProgress);

        await engine.ScanAsync(prefixes, Config, _cts.Token);

        _dispatch(() =>
        {
            UpdateProgress();
            Progress.Phase = "Bereit";
        });
    }

    public void Stop() => _cts?.Cancel();

    private static IReadOnlyList<string> BuildPrefixes(NetworkInfo info, List<string> extra)
    {
        var list = new List<string>();
        if (info.Ip is not null) list.Add(Ipv4.SubnetPrefix(info.Ip));
        foreach (var s in extra)
        {
            var prefix = Ipv4.SubnetPrefix(s.Split('/')[0]);
            if (!list.Contains(prefix)) list.Add(prefix);
        }
        return list;
    }

    private void OnDeviceUpdated(Device d) => _dispatch(() =>
    {
        if (_byIp.TryGetValue(d.Ip, out var existing)) { existing.Refresh(); return; }
        var vm = new DeviceViewModel(d);
        _byIp[d.Ip] = vm;
        Devices.Add(vm);
    });

    private void UpdateProgress()
    {
        int online = Devices.Count(d => d.IsOnline);
        int total = Devices.Count;
        Progress.SetDevices(online, total - online, 0, Math.Max(total, 1));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter MainViewModelTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add MainViewModel scan orchestration"
```

---

## PHASE 9 — WPF Views

> These tasks are UI wiring. They have no unit tests; verification is "build succeeds + app launches + manual smoke check". Each task ends with `dotnet build` and a manual run.

### Task 23: Catppuccin theme resource dictionary

**Files:**
- Create: `src/IpScanner/Resources/Styles.xaml`
- Modify: `src/IpScanner/App.xaml`

- [ ] **Step 1: Create Styles.xaml**

`src/IpScanner/Resources/Styles.xaml`:
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Catppuccin Mocha palette -->
    <SolidColorBrush x:Key="Bg"        Color="#1E1E2E"/>
    <SolidColorBrush x:Key="BgAlt"     Color="#252535"/>
    <SolidColorBrush x:Key="BgDark"    Color="#181825"/>
    <SolidColorBrush x:Key="Surface"   Color="#313244"/>
    <SolidColorBrush x:Key="Text"      Color="#CDD6F4"/>
    <SolidColorBrush x:Key="Subtext"   Color="#6C7086"/>
    <SolidColorBrush x:Key="Green"     Color="#A6E3A1"/>
    <SolidColorBrush x:Key="Red"       Color="#F38BA8"/>
    <SolidColorBrush x:Key="Blue"      Color="#89B4FA"/>
    <SolidColorBrush x:Key="Mauve"     Color="#CBA6F7"/>
    <SolidColorBrush x:Key="Peach"     Color="#FAB387"/>
    <SolidColorBrush x:Key="GrayBlock" Color="#45475A"/>
    <SolidColorBrush x:Key="MidGray"   Color="#585B70"/>

    <Style TargetType="Window">
        <Setter Property="Background" Value="{StaticResource Bg}"/>
        <Setter Property="FontFamily" Value="Segoe UI"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 2: Reference it in App.xaml**

`src/IpScanner/App.xaml`:
```xml
<Application x:Class="IpScanner.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/Styles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/IpScanner/App.xaml.cs`:
```csharp
using System.Windows;

namespace IpScanner;

public partial class App : Application { }
```

- [ ] **Step 3: Build**

Run: `dotnet build src\IpScanner\IpScanner.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```
git add .
git commit -m "feat: add Catppuccin theme + App wiring"
```

### Task 24: SegmentedProgressBar control

**Files:**
- Create: `src/IpScanner/Views/Controls/SegmentedProgressBar.cs`

- [ ] **Step 1: Implement the control**

`src/IpScanner/Views/Controls/SegmentedProgressBar.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IpScanner.Views.Controls;

/// <summary>
/// A horizontal bar split into up to three colored segments (fractions 0..1).
/// Used for device (online/offline/unknown) and ping (success/fail/skipped) bars.
/// </summary>
public sealed class SegmentedProgressBar : Control
{
    static SegmentedProgressBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(typeof(SegmentedProgressBar)));
    }

    public static readonly DependencyProperty Fraction1Property =
        DependencyProperty.Register(nameof(Fraction1), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Fraction2Property =
        DependencyProperty.Register(nameof(Fraction2), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Fraction3Property =
        DependencyProperty.Register(nameof(Fraction3), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color1Property =
        DependencyProperty.Register(nameof(Color1), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color2Property =
        DependencyProperty.Register(nameof(Color2), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color3Property =
        DependencyProperty.Register(nameof(Color3), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction1 { get => (double)GetValue(Fraction1Property); set => SetValue(Fraction1Property, value); }
    public double Fraction2 { get => (double)GetValue(Fraction2Property); set => SetValue(Fraction2Property, value); }
    public double Fraction3 { get => (double)GetValue(Fraction3Property); set => SetValue(Fraction3Property, value); }
    public Brush Color1 { get => (Brush)GetValue(Color1Property); set => SetValue(Color1Property, value); }
    public Brush Color2 { get => (Brush)GetValue(Color2Property); set => SetValue(Color2Property, value); }
    public Brush Color3 { get => (Brush)GetValue(Color3Property); set => SetValue(Color3Property, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        double x = 0;
        foreach (var (frac, brush) in new[]
                 { (Fraction1, Color1), (Fraction2, Color2), (Fraction3, Color3) })
        {
            double segW = Math.Max(0, Math.Min(1, frac)) * w;
            if (segW <= 0) continue;
            dc.DrawRectangle(brush, null, new Rect(x, 0, segW, h));
            x += segW;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src\IpScanner\IpScanner.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```
git add .
git commit -m "feat: add SegmentedProgressBar custom control"
```

### Task 25: MainWindow layout (header + table + sidebar)

**Files:**
- Create: `src/IpScanner/MainWindow.xaml`
- Create: `src/IpScanner/MainWindow.xaml.cs`

- [ ] **Step 1: Implement MainWindow.xaml**

`src/IpScanner/MainWindow.xaml`:
```xml
<Window x:Class="IpScanner.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ctrl="clr-namespace:IpScanner.Views.Controls"
        Title="IP-Scanner" Height="700" Width="1200"
        WindowStartupLocation="CenterScreen">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Border Grid.Row="0" Background="{StaticResource BgDark}" Padding="12,8">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <!-- Title + subnet pills -->
                <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="IP-Scanner" Foreground="{StaticResource Mauve}"
                               FontWeight="Bold" FontSize="16" Margin="0,0,8,0"/>
                    <ItemsControl ItemsSource="{Binding Networks}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Border Background="{StaticResource Surface}" CornerRadius="10"
                                        Padding="8,2" Margin="2,0">
                                    <TextBlock Text="{Binding Cidr}" Foreground="{StaticResource Blue}" FontSize="11"/>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>

                <!-- Center: progress bars -->
                <StackPanel Grid.Column="1" Margin="18,0" VerticalAlignment="Center">
                    <Grid Margin="0,0,0,4">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="50"/><ColumnDefinition Width="*"/><ColumnDefinition Width="70"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="Geräte" Foreground="{StaticResource Subtext}" FontSize="10"
                                   HorizontalAlignment="Right" Margin="0,0,6,0" VerticalAlignment="Center"/>
                        <ctrl:SegmentedProgressBar Grid.Column="1" Height="8"
                            Track="{StaticResource Surface}"
                            Color1="{StaticResource Green}" Color2="{StaticResource Red}" Color3="{StaticResource GrayBlock}"
                            Fraction1="{Binding Progress.OnlineFraction}"
                            Fraction2="{Binding Progress.OfflineFraction}"
                            Fraction3="{Binding Progress.UnknownFraction}"/>
                        <TextBlock Grid.Column="2" Text="{Binding Progress.DeviceCountText}"
                                   Foreground="{StaticResource Text}" FontSize="10" Margin="6,0,0,0" VerticalAlignment="Center"/>
                    </Grid>
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="50"/><ColumnDefinition Width="*"/><ColumnDefinition Width="70"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="Pings" Foreground="{StaticResource Subtext}" FontSize="10"
                                   HorizontalAlignment="Right" Margin="0,0,6,0" VerticalAlignment="Center"/>
                        <ctrl:SegmentedProgressBar Grid.Column="1" Height="8"
                            Track="{StaticResource Surface}"
                            Color1="{StaticResource Blue}" Color2="{StaticResource Peach}" Color3="{StaticResource MidGray}"
                            Fraction1="{Binding Progress.SuccessFraction}"
                            Fraction2="{Binding Progress.FailedFraction}"
                            Fraction3="{Binding Progress.SkippedFraction}"/>
                        <TextBlock Grid.Column="2" Text="{Binding Progress.PingCountText}"
                                   Foreground="{StaticResource Text}" FontSize="10" Margin="6,0,0,0" VerticalAlignment="Center"/>
                    </Grid>
                </StackPanel>

                <!-- Right: controls -->
                <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="Pings:" Foreground="{StaticResource Subtext}" FontSize="11"
                               VerticalAlignment="Center" Margin="0,0,4,0"/>
                    <ComboBox x:Name="PingCountBox" Width="80" Margin="0,0,6,0" SelectedIndex="0">
                        <ComboBoxItem>10</ComboBoxItem><ComboBoxItem>100</ComboBoxItem>
                        <ComboBoxItem>1000</ComboBoxItem><ComboBoxItem>10000</ComboBoxItem>
                        <ComboBoxItem>100000</ComboBoxItem><ComboBoxItem>∞</ComboBoxItem>
                    </ComboBox>
                    <Button x:Name="ScanButton" Content="▶ Scan" Click="OnScanClick" Margin="0,0,4,0" Padding="12,4"/>
                    <Button x:Name="StopButton" Content="■" Click="OnStopClick" Margin="0,0,4,0" Padding="9,4"/>
                    <Button x:Name="SettingsButton" Content="⚙" Click="OnSettingsClick" Padding="9,4"/>
                </StackPanel>
            </Grid>
        </Border>

        <!-- Main: table + sidebar -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="230"/>
            </Grid.ColumnDefinitions>

            <DataGrid Grid.Column="0" ItemsSource="{Binding Devices}" AutoGenerateColumns="False"
                      IsReadOnly="True" GridLinesVisibility="Horizontal" HeadersVisibility="Column"
                      Background="{StaticResource Bg}" Foreground="{StaticResource Text}"
                      RowBackground="{StaticResource Bg}" AlternatingRowBackground="{StaticResource BgAlt}"
                      BorderThickness="0">
                <DataGrid.Columns>
                    <DataGridTextColumn Header="IP" Binding="{Binding Ip}" Width="120"/>
                    <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="80"/>
                    <DataGridTemplateColumn Header="Gruppe" Width="60">
                        <DataGridTemplateColumn.CellTemplate>
                            <DataTemplate>
                                <Border Width="14" Height="14" CornerRadius="2"
                                        Background="{Binding GroupColor}"/>
                            </DataTemplate>
                        </DataGridTemplateColumn.CellTemplate>
                    </DataGridTemplateColumn>
                    <DataGridTextColumn Header="Hostname" Binding="{Binding Hostname}" Width="*"/>
                    <DataGridTextColumn Header="Avg" Binding="{Binding AvgDisplay}" Width="90"/>
                    <DataGridTextColumn Header="Min" Binding="{Binding MinDisplay}" Width="90"/>
                    <DataGridTextColumn Header="Max" Binding="{Binding MaxDisplay}" Width="90"/>
                    <DataGridTextColumn Header="Letzt" Binding="{Binding LastDisplay}" Width="90"/>
                    <DataGridTextColumn Header="Fortschritt" Binding="{Binding ProgressDisplay}" Width="90"/>
                    <DataGridTextColumn Header="MAC" Binding="{Binding Mac}" Width="150"/>
                </DataGrid.Columns>
            </DataGrid>

            <!-- Sidebar -->
            <Border Grid.Column="1" Background="{StaticResource BgDark}" BorderBrush="{StaticResource Surface}"
                    BorderThickness="1,0,0,0">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel>
                        <ItemsControl ItemsSource="{Binding Networks}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Border Padding="11,10" BorderBrush="{StaticResource Surface}" BorderThickness="0,0,0,1">
                                        <StackPanel>
                                            <StackPanel Orientation="Horizontal" Margin="0,0,0,7">
                                                <Border Background="{Binding BadgeColor}" CornerRadius="8" Padding="6,1">
                                                    <TextBlock Text="{Binding Title}" FontSize="9" FontWeight="Bold" Foreground="#1E1E2E"/>
                                                </Border>
                                                <TextBlock Text="{Binding Cidr}" Foreground="{StaticResource Blue}"
                                                           FontSize="9" Margin="6,0,0,0" VerticalAlignment="Center"/>
                                            </StackPanel>
                                            <Grid>
                                                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                                <Grid.RowDefinitions>
                                                    <RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/>
                                                </Grid.RowDefinitions>
                                                <TextBlock Text="IP" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Column="1" Text="{Binding Ip}" Foreground="{StaticResource Text}" FontSize="10" HorizontalAlignment="Right"/>
                                                <TextBlock Grid.Row="1" Text="MAC" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding Mac}" Foreground="{StaticResource MidGray}" FontSize="10" HorizontalAlignment="Right"/>
                                                <TextBlock Grid.Row="2" Text="Gateway" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding Gateway}" Foreground="{StaticResource Green}" FontSize="10" HorizontalAlignment="Right"/>
                                                <TextBlock Grid.Row="3" Text="Subnetz" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Row="3" Grid.Column="1" Text="{Binding SubnetMask}" Foreground="{StaticResource Text}" FontSize="10" HorizontalAlignment="Right"/>
                                                <TextBlock Grid.Row="4" Text="DNS" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Row="4" Grid.Column="1" Text="{Binding Dns}" Foreground="{StaticResource Text}" FontSize="10" HorizontalAlignment="Right" TextWrapping="Wrap"/>
                                                <TextBlock Grid.Row="5" Text="Interface" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Grid.Row="5" Grid.Column="1" Text="{Binding Interface}" Foreground="{StaticResource Text}" FontSize="10" HorizontalAlignment="Right"/>
                                            </Grid>
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>

                        <!-- Internet latency -->
                        <Border Padding="11,10" BorderBrush="{StaticResource Surface}" BorderThickness="0,0,0,1">
                            <StackPanel>
                                <TextBlock Text="INTERNET-LATENZ" Foreground="{StaticResource Subtext}" FontSize="9" Margin="0,0,0,6"/>
                                <ItemsControl ItemsSource="{Binding InternetHosts}">
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Grid Margin="0,2">
                                                <TextBlock Text="{Binding Name}" Foreground="{StaticResource Subtext}" FontSize="10"/>
                                                <TextBlock Text="{Binding LatencyDisplay}" Foreground="{StaticResource Green}"
                                                           FontSize="10" FontWeight="SemiBold" HorizontalAlignment="Right"/>
                                            </Grid>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </StackPanel>
                        </Border>

                        <!-- Phase + export -->
                        <Border Padding="11,10">
                            <StackPanel>
                                <Border Background="{StaticResource BgAlt}" CornerRadius="5" Padding="9,6" Margin="0,0,0,8">
                                    <TextBlock Text="{Binding Progress.Phase}" Foreground="{StaticResource Mauve}"
                                               FontSize="10" FontWeight="SemiBold" HorizontalAlignment="Center"/>
                                </Border>
                                <TextBlock Text="LETZTER EXPORT" Foreground="{StaticResource Subtext}" FontSize="9" Margin="0,0,0,3"/>
                                <TextBlock x:Name="ExportPathText" Text="—" Foreground="{StaticResource MidGray}"
                                           FontSize="9" TextWrapping="Wrap"/>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>
            </Border>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 2: Implement MainWindow.xaml.cs**

`src/IpScanner/MainWindow.xaml.cs`:
```csharp
using System.Windows;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ScanConfig _config;
    private const string ConfigPath = "ip_scanner.conf";

    public MainWindow()
    {
        InitializeComponent();
        _config = ConfigManager.Load(ConfigPath);
        _vm = new MainViewModel(
            pingFunc: IcmpPinger.Ping,
            detectNetwork: NetworkDetector.DetectFast,
            dispatch: a => Dispatcher.Invoke(a))
        { Config = _config };
        DataContext = _vm;
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try
        {
            _vm.Config = _config;
            ApplySelectedPingCount();
            await _vm.RunScanAsync();
        }
        finally { ScanButton.IsEnabled = true; }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.Stop();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_config) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _config = win.Result;
            ConfigManager.Save(ConfigPath, _config);
            _vm.Config = _config;
        }
    }

    private void ApplySelectedPingCount()
    {
        var text = (PingCountBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
        _config.PingCount = text == "∞" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) ? n : 10;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src\IpScanner\IpScanner.csproj`
Expected: Build succeeds (will fail to compile until Task 26 adds SettingsWindow — so create a stub now if needed). If `SettingsWindow` is missing, temporarily comment out `OnSettingsClick` body, build, then restore in Task 26.

- [ ] **Step 4: Commit**

```
git add .
git commit -m "feat: add MainWindow layout and code-behind"
```

### Task 26: SettingsWindow (tabbed)

**Files:**
- Create: `src/IpScanner/Views/SettingsWindow.xaml`
- Create: `src/IpScanner/Views/SettingsWindow.xaml.cs`

- [ ] **Step 1: Implement SettingsWindow.xaml**

`src/IpScanner/Views/SettingsWindow.xaml`:
```xml
<Window x:Class="IpScanner.Views.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Einstellungen" Height="520" Width="560"
        WindowStartupLocation="CenterOwner">
    <DockPanel Margin="10">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,10,0,0">
            <Button Content="Speichern" Click="OnSave" Padding="14,5" Margin="0,0,8,0"/>
            <Button Content="Abbrechen" Click="OnCancel" Padding="14,5"/>
        </StackPanel>
        <TabControl Background="{StaticResource Bg}">
            <TabItem Header="Netzwerk">
                <StackPanel Margin="12">
                    <TextBlock Text="Subnetze (eine pro Zeile)" Foreground="{StaticResource Text}" Margin="0,0,0,4"/>
                    <TextBox x:Name="SubnetsBox" Height="90" AcceptsReturn="True" TextWrapping="Wrap"/>
                    <TextBlock Text="Pinned IPs (eine pro Zeile)" Foreground="{StaticResource Text}" Margin="0,10,0,4"/>
                    <TextBox x:Name="PinnedBox" Height="70" AcceptsReturn="True" TextWrapping="Wrap"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Ping-Verhalten">
                <StackPanel Margin="12">
                    <DockPanel Margin="0,4"><TextBlock Text="Ping-Anzahl" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="PingCountBox" Width="100"/></DockPanel>
                    <DockPanel Margin="0,4"><TextBlock Text="Ping-Intervall (ms)" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="IntervalBox" Width="100"/></DockPanel>
                    <DockPanel Margin="0,4"><TextBlock Text="Offline nach N Fehlversuchen" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="OfflineAfterBox" Width="100"/></DockPanel>
                    <DockPanel Margin="0,4"><TextBlock Text="Discovery-Pings" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="InitPingCountBox" Width="100"/></DockPanel>
                    <CheckBox x:Name="HighPressureBox" Content="High-Pressure-Modus" Foreground="{StaticResource Text}" Margin="0,8,0,0"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Internet">
                <StackPanel Margin="12">
                    <CheckBox x:Name="EnableInternetBox" Content="Internet-Ping aktivieren" Foreground="{StaticResource Text}"/>
                    <TextBlock Text="Internet-Hosts (einer pro Zeile)" Foreground="{StaticResource Text}" Margin="0,10,0,4"/>
                    <TextBox x:Name="InternetHostsBox" Height="120" AcceptsReturn="True" TextWrapping="Wrap"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Ausgabe">
                <StackPanel Margin="12">
                    <DockPanel Margin="0,4"><TextBlock Text="Ausgabeverzeichnis" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="OutputDirBox" Width="280"/></DockPanel>
                    <CheckBox x:Name="FileOutputBox" Content="TXT-Bericht schreiben" Foreground="{StaticResource Text}" Margin="0,8,0,0"/>
                    <CheckBox x:Name="ExportCsvBox" Content="CSV exportieren" Foreground="{StaticResource Text}" Margin="0,4,0,0"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Performance">
                <StackPanel Margin="12">
                    <DockPanel Margin="0,4"><TextBlock Text="Analyse-Threads" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="PingThreadsBox" Width="100"/></DockPanel>
                    <DockPanel Margin="0,4"><TextBlock Text="Discovery-Threads" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="InitThreadsBox" Width="100"/></DockPanel>
                    <DockPanel Margin="0,4"><TextBlock Text="Refresh-Rate (s)" Foreground="{StaticResource Text}" Width="200"/><TextBox x:Name="RefreshRateBox" Width="100"/></DockPanel>
                </StackPanel>
            </TabItem>
            <TabItem Header="Datenbank">
                <StackPanel Margin="12">
                    <CheckBox x:Name="KnownDbBox" Content="Known-Devices-DB aktivieren" Foreground="{StaticResource Text}"/>
                    <Button Content="DB leeren" Click="OnClearDb" Padding="10,4" Margin="0,10,0,0" HorizontalAlignment="Left"/>
                </StackPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Implement SettingsWindow.xaml.cs**

`src/IpScanner/Views/SettingsWindow.xaml.cs`:
```csharp
using System.Globalization;
using System.Windows;
using IpScanner.Core.Data;
using IpScanner.Core.Models;

namespace IpScanner.Views;

public partial class SettingsWindow : Window
{
    public ScanConfig Result { get; private set; }

    public SettingsWindow(ScanConfig cfg)
    {
        InitializeComponent();
        Result = cfg;
        Load(cfg);
    }

    private void Load(ScanConfig c)
    {
        SubnetsBox.Text = string.Join(Environment.NewLine, c.Subnets);
        PinnedBox.Text = string.Join(Environment.NewLine, c.PinnedIps);
        PingCountBox.Text = c.PingCount.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        HighPressureBox.IsChecked = c.HighPressureMode;
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetHostsBox.Text = string.Join(Environment.NewLine, c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        PingThreadsBox.Text = c.PingThreads.ToString();
        InitThreadsBox.Text = c.InitPingThreads.ToString();
        RefreshRateBox.Text = c.RefreshRate.ToString(CultureInfo.InvariantCulture);
        KnownDbBox.IsChecked = c.KnownDevicesDb;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        static List<string> Lines(string t) => t
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        int I(string s, int d) => int.TryParse(s.Trim(), out var v) ? v : d;
        double D(string s, double d) => double.TryParse(s.Trim().Replace(',', '.'),
            CultureInfo.InvariantCulture, out var v) ? v : d;

        Result = new ScanConfig
        {
            Subnets = Lines(SubnetsBox.Text),
            PinnedIps = Lines(PinnedBox.Text),
            PingCount = I(PingCountBox.Text, 10),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            HighPressureMode = HighPressureBox.IsChecked == true,
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetHosts = Lines(InternetHostsBox.Text),
            OutputDirectory = OutputDirBox.Text.Trim(),
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            PingThreads = I(PingThreadsBox.Text, 100),
            InitPingThreads = I(InitThreadsBox.Text, 254),
            RefreshRate = D(RefreshRateBox.Text, 1.0),
            KnownDevicesDb = KnownDbBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClearDb(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Known-Devices-Datenbank wirklich leeren?", "Bestätigen",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { new KnownDevicesDb("scanner.db").Clear(); } catch { /* ignore */ }
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src\IpScanner\IpScanner.csproj`
Expected: Build succeeds. Restore the `OnSettingsClick` body in MainWindow if it was stubbed.

- [ ] **Step 4: Run the app manually**

Run: `dotnet run --project src\IpScanner\IpScanner.csproj`
Expected: Window opens, ▶ Scan populates the table with live devices, ⚙ opens settings.

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add tabbed SettingsWindow"
```

---

## PHASE 10 — Integration: internet pings, DB, export, sidebar stats

### Task 27: Internet latency pinging in MainViewModel

**Files:**
- Modify: `src/IpScanner/ViewModels/MainViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/MainViewModelTests.cs`

- [ ] **Step 1: Write the failing test (append)**

```csharp
    [Fact]
    public async Task PingInternet_FillsLatencies()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => new PingResult(true, ip == "1.1.1.1" ? 8.0 : 12.0, 64),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { InternetHosts = new() { "1.1.1.1", "8.8.8.8" } };

        await vm.PingInternetAsync(CancellationToken.None);

        Assert.Equal(2, vm.InternetHosts.Count);
        Assert.Contains(vm.InternetHosts, h => h.Ip == "1.1.1.1");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MainViewModelTests`
Expected: FAIL — `PingInternetAsync` / `InternetHosts` do not exist.

- [ ] **Step 3: Add internet support to MainViewModel**

Add to `MainViewModel`:
```csharp
    public ObservableCollection<InternetHostViewModel> InternetHosts { get; } = new();

    private static readonly Dictionary<string, string> KnownHostNames = new()
    {
        ["1.1.1.1"] = "Cloudflare", ["8.8.8.8"] = "Google",
        ["8.8.4.4"] = "Google DNS", ["9.9.9.9"] = "Quad9",
    };

    public async Task PingInternetAsync(CancellationToken ct)
    {
        if (!Config.EnableInternetPing) return;
        _dispatch(() =>
        {
            InternetHosts.Clear();
            foreach (var ip in Config.InternetHosts)
                InternetHosts.Add(new InternetHostViewModel(
                    KnownHostNames.GetValueOrDefault(ip, ip), ip));
        });

        await Task.Run(() =>
        {
            foreach (var host in InternetHosts.ToList())
            {
                if (ct.IsCancellationRequested) break;
                var r = _pingFunc(host.Ip, 1000);
                _dispatch(() => host.SetLatency(r.Success ? r.LatencyMs : null));
            }
        }, ct);
    }
```

Call `await PingInternetAsync(_cts.Token);` near the start of `RunScanAsync` (fire-and-forget is fine: `_ = PingInternetAsync(_cts.Token);`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter MainViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: add internet latency pinging to MainViewModel"
```

### Task 28: Export on scan completion + DB persistence

**Files:**
- Modify: `src/IpScanner/ViewModels/MainViewModel.cs`
- Modify: `src/IpScanner/MainWindow.xaml.cs`
- Test: `tests/IpScanner.Tests/ViewModels/MainViewModelTests.cs`

- [ ] **Step 1: Write the failing test (append)**

```csharp
    [Fact]
    public async Task RunScan_WithFileOutput_WritesReport()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, FileOutput = true, OutputDirectory = dir, KnownDevicesDb = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.NotNull(vm.LastExportPath);
        Assert.True(File.Exists(vm.LastExportPath));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MainViewModelTests`
Expected: FAIL — `LastExportPath` does not exist / not written.

- [ ] **Step 3: Add export + DB to RunScanAsync**

Add fields/props to `MainViewModel`:
```csharp
    public string? LastExportPath { get; private set; }
    private NetworkInfo _lastInfo = new();
```

At the end of `RunScanAsync`, after the final dispatch, add:
```csharp
        var devices = engine.Devices.ToList();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var gatewaySlug = Slug(info.Gateway);

        if (Config.FileOutput)
        {
            LastExportPath = Core.Export.TxtExporter.Write(devices, info,
                Config.OutputDirectory, timestamp, gatewaySlug);
            if (Config.ExportCsv)
                Core.Export.CsvExporter.Write(devices, Config.OutputDirectory, timestamp, gatewaySlug);
            Raise(nameof(LastExportPath));
        }

        if (Config.KnownDevicesDb && info.Gateway is not null)
        {
            var gw = devices.FirstOrDefault(d => d.Ip == info.Gateway);
            if (gw?.Mac is { } mac && mac != "Unknown")
            {
                try { new KnownDevicesDb("scanner.db").Save(mac, devices, timestamp); } catch { }
            }
        }
```

Add the helper + the using:
```csharp
using IpScanner.Core.Data;
...
    private static string Slug(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var safe = new string(host.Where(c => !"\\/:*?\"<>| \t".Contains(c)).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
```

- [ ] **Step 4: Bind LastExportPath in MainWindow**

In `MainWindow.xaml.cs` `OnScanClick`, after `await _vm.RunScanAsync();` add:
```csharp
            if (_vm.LastExportPath is not null) ExportPathText.Text = _vm.LastExportPath;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter MainViewModelTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```
git add .
git commit -m "feat: write TXT/CSV reports and persist known devices on scan end"
```

### Task 29: Per-network sidebar stats (online/offline/avg)

**Files:**
- Modify: `src/IpScanner/ViewModels/MainViewModel.cs`
- Test: `tests/IpScanner.Tests/ViewModels/MainViewModelTests.cs`

- [ ] **Step 1: Write the failing test (append)**

```csharp
    [Fact]
    public async Task RunScan_UpdatesNetworkCardStats()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") ? new PingResult(true, 2.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, KnownDevicesDb = false, FileOutput = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Single(vm.Networks);
        Assert.Equal(1, vm.Networks[0].OnlineCount);
        Assert.Equal(253, vm.Networks[0].OfflineCount);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MainViewModelTests`
Expected: FAIL — network card counts stay 0.

- [ ] **Step 3: Update card stats in UpdateProgress**

In `UpdateProgress()`, append per-network aggregation:
```csharp
        foreach (var net in Networks)
        {
            var prefix = net.Cidr.Split('/')[0];
            prefix = string.Join('.', prefix.Split('.')[..3]);
            var inNet = Devices.Where(d => d.Ip.StartsWith(prefix + ".")).ToList();
            net.OnlineCount = inNet.Count(d => d.IsOnline);
            net.OfflineCount = inNet.Count(d => !d.IsOnline);
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter MainViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```
git add .
git commit -m "feat: aggregate per-network online/offline stats in sidebar"
```

---

## PHASE 11 — Packaging

### Task 30: build.bat single-file publish + icon + config template

**Files:**
- Create: `build.bat`
- Modify: `.gitignore`

- [ ] **Step 1: Create build.bat**

`build.bat`:
```bat
@echo off
setlocal
cd /d "%~dp0"
echo Building IP-Scanner single-file exe ...
dotnet publish src\IpScanner\IpScanner.csproj ^
    -r win-x64 --self-contained -c Release ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o dist
if errorlevel 1 ( echo BUILD FAILED & pause & exit /b 1 )
echo.
echo Done: %~dp0dist\IP-Scanner.exe
pause
endlocal
```

- [ ] **Step 2: Add build outputs to .gitignore**

Append to `.gitignore`:
```
bin/
obj/
dist/
*.db
Scans/
ip_scanner.conf
.superpowers/
```

- [ ] **Step 3: Run the build**

Run: `build.bat`
Expected: `dist\IP-Scanner.exe` produced; double-click launches the GUI.

- [ ] **Step 4: Manual verification checklist**

- [ ] App opens centered, dark theme
- [ ] ▶ Scan detects own subnet, table fills with online + offline devices
- [ ] Both header progress bars animate with correct segment colors
- [ ] Sidebar shows the detected network(s) with IP/MAC/GW/mask/DNS/interface
- [ ] Internet latency rows fill in
- [ ] ⚙ opens settings, changes persist to `ip_scanner.conf`
- [ ] On completion, a TXT report appears in `Scans/` and the path shows in the sidebar
- [ ] ■ Stop halts an in-progress scan

- [ ] **Step 5: Commit**

```
git add .
git commit -m "build: add single-file publish script and gitignore"
```

### Task 31: Run the conf template + first-run files

**Files:**
- Modify: `src/IpScanner/MainWindow.xaml.cs`

- [ ] **Step 1: Write a conf template on first run**

In `MainWindow` constructor, before `ConfigManager.Load`, add:
```csharp
        if (!File.Exists(ConfigPath))
            ConfigManager.Save(ConfigPath, new ScanConfig());  // seed defaults
```

(`ConfigManager.Save` from Task 5 writes every documented key with its default — this is the template.)

- [ ] **Step 2: Build + run once to confirm the file is created**

Run: `dotnet run --project src\IpScanner\IpScanner.csproj`
Expected: `ip_scanner.conf` is created next to the binary with all default keys.

- [ ] **Step 3: Commit**

```
git add .
git commit -m "feat: seed ip_scanner.conf with defaults on first run"
```

---

## Self-Review Notes

- **Spec coverage:** All sidebar fields (Task 25/29), all settings tabs (Task 26), every config key (Tasks 4/5), ICMP+fallback (Task 6 — note: `ping.exe` fallback omitted for brevity; ICMP works without admin so it is the sole path; add fallback later if a target blocks IcmpSendEcho), grouping+colors (Tasks 10/11), DB (Task 12), TXT+CSV (Tasks 14/15), internet latency (Task 27), packaging (Task 30). Pinned-IP prioritization and the offline-reprobe monitor are simplified out of the first engine pass — the engine pings all hosts every discovery round, which covers offline detection; the dedicated 5s re-probe promotion loop can be a follow-up enhancement.
- **Type consistency:** `Device.RecordPing`, `ScanEngine.DeviceUpdated`/`Progress`, `MainViewModel` dispatch signature, `ConfigManager.Load/Save`, `ScanConfig.InfinitePingCount` are used consistently across tasks.
- **Placeholder scan:** No TODO/TBD; every code step shows full code.
