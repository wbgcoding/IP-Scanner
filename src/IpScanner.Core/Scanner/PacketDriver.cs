using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace IpScanner.Core.Scanner;

/// <summary>Whether a packet capture driver is usable. Raw-packet features (half-open and special
/// port scans, TCP pings, active OS fingerprint) ask here and grey themselves out with the reason.
/// The result is cached; <see cref="WaitForInstallAsync"/> detects again after an install so the
/// scan types unlock without a restart.</summary>
public static class PacketDriver
{
    public const string ReasonMissing = "driver-missing";
    public const string ReasonAdmin = "admin-missing";
    public const string ReasonIpv6 = "ipv6-unsupported";
    public const string ReasonStopped = "driver-stopped";

    /// <summary>Official download page for the driver; the app only ever opens it in the browser.</summary>
    public const string DownloadUrl = "https://npcap.com/#download";

    private static PacketDriverStatus? _override;
    private static bool? _elevatedOverride;
    private static readonly Lazy<bool> ElevatedNow = new(() => new SystemDriverProbe().IsElevated());
    private static readonly object Gate = new();
    private static PacketDriverStatus? _detected;

    /// <summary>Detection run by the first read and by every re-detect; tests replace it.</summary>
    internal static Func<PacketDriverStatus> DetectOverride { get; set; } = () => PacketDriverDetector.Detect(new SystemDriverProbe());

    /// <summary>Current status, detected on first use and cached until a re-detect.</summary>
    public static PacketDriverStatus Status
    {
        get
        {
            if (_override is { } forced) return forced;
            lock (Gate) return _detected ??= DetectOverride();
        }
    }

    public static bool Available => Status.Available;

    /// <summary>Installed driver version, empty when unknown or not installed.</summary>
    public static string Version => Status.Version;

    /// <summary>True when the process runs with administrator rights.</summary>
    public static bool Elevated => _elevatedOverride ?? ElevatedNow.Value;

    /// <summary>Detects the driver again and replaces the cached status. A load failure remembered from
    /// before the install is dropped so the freshly installed library gets loaded.</summary>
    public static PacketDriverStatus Redetect()
    {
        var fresh = DetectOverride();
        lock (Gate) _detected = fresh;
        if (fresh.Available) Wpcap.ResetLoadFailure();
        return fresh;
    }

    /// <summary>Polls for the driver after the user started the install. It counts as installed once it was
    /// seen on two polls in a row (the installer copies the files before the service is ready); the cached
    /// status is then replaced, whether it ended up usable or still needs administrator rights.</summary>
    /// <returns>True when the driver showed up, false on timeout or cancellation.</returns>
    public static async Task<bool> WaitForInstallAsync(CancellationToken ct, TimeSpan? interval = null, TimeSpan? timeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var step = interval ?? TimeSpan.FromSeconds(2);
        var limit = timeout ?? TimeSpan.FromMinutes(10);
        delay ??= Task.Delay;
        int rounds = step <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(limit / step), seen = 0;
        try
        {
            for (int i = 0; i < rounds; i++)
            {
                await delay(step, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var status = await Task.Run(DetectOverride, ct).ConfigureAwait(false);
                seen = status.Reason == ReasonMissing ? 0 : seen + 1;
                if (seen < 2) continue;
                Redetect();
                return true;
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    /// <summary>Test seam: forces a status, null restores detection.</summary>
    internal static void OverrideForTests(PacketDriverStatus? status, bool? elevated = null)
    {
        _override = status;
        _elevatedOverride = elevated;
    }

    /// <summary>Test seam: drops the cached status, the detection replacement and every override.</summary>
    internal static void ResetForTests()
    {
        _override = null;
        _elevatedOverride = null;
        lock (Gate) _detected = null;
        DetectOverride = () => PacketDriverDetector.Detect(new SystemDriverProbe());
    }
}

/// <param name="Available">True when raw packets can be sent and captured.</param>
/// <param name="Reason">Why it is unavailable (driver missing, administrator rights missing), null when available.</param>
/// <param name="Version">Driver version when it could be read.</param>
public sealed record PacketDriverStatus(bool Available, string? Reason, string Version = "")
{
    public static PacketDriverStatus Missing { get; } = new(false, PacketDriver.ReasonMissing);
}

/// <summary>What detection needs from the machine; a fake in tests.</summary>
public interface IPacketDriverProbe
{
    string SystemDirectory { get; }
    bool FileExists(string path);
    string FileVersion(string path);
    bool RegistryKeyExists(string subKey);
    int? RegistryDword(string subKey, string name);
    bool IsElevated();

    /// <summary>Current state of a Windows service (SERVICE_STOPPED = 1, SERVICE_RUNNING = 4), null when it cannot be read.</summary>
    int? ServiceState(string serviceName);
}

public static class PacketDriverDetector
{
    public const string NpcapKey = @"SOFTWARE\Npcap";
    public const string ServiceParamsKey = @"SYSTEM\CurrentControlSet\Services\npcap\Parameters";
    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\npcap";
    public const string ServiceName = "npcap";
    public const int ServiceStopped = 1;
    private const int StartOnDemand = 3;

    public static PacketDriverStatus Detect(IPacketDriverProbe probe)
    {
        var dir = Path.Combine(probe.SystemDirectory, "Npcap");
        var wpcap = Path.Combine(dir, "wpcap.dll");
        var packet = Path.Combine(dir, "Packet.dll");
        if (!probe.FileExists(wpcap) || !probe.FileExists(packet) || !probe.RegistryKeyExists(NpcapKey))
            return PacketDriverStatus.Missing;

        var version = probe.FileVersion(packet);
        // A stopped service blocks every raw feature. One that starts on demand is started by the
        // library on first use, so only a service meant to run (or disabled) counts as a problem.
        var start = probe.RegistryDword(ServiceKey, "Start");
        if (probe.ServiceState(ServiceName) == ServiceStopped && start is { } st && st != StartOnDemand)
            return new PacketDriverStatus(false, PacketDriver.ReasonStopped, version);
        // An install limited to administrators only opens for an elevated process.
        var adminOnly = probe.RegistryDword(ServiceParamsKey, "AdminOnly") == 1;
        if (adminOnly && !probe.IsElevated())
            return new PacketDriverStatus(false, PacketDriver.ReasonAdmin, version);
        return new PacketDriverStatus(true, null, version);
    }
}

internal sealed class SystemDriverProbe : IPacketDriverProbe
{
    public string SystemDirectory => Environment.SystemDirectory;

    public bool FileExists(string path) => File.Exists(path);

    public string FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion ?? ""; }
        catch { return ""; }
    }

    public bool RegistryKeyExists(string subKey)
    {
        // The Npcap installer is a 32-bit program, so its own key lives in the 32-bit view on x64.
        return KeyIn(RegistryView.Registry64, subKey) || KeyIn(RegistryView.Registry32, subKey);
    }

    private static bool KeyIn(RegistryView view, string subKey)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(subKey);
            return key != null;
        }
        catch { return false; }
    }

    public int? RegistryDword(string subKey, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(subKey);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    public int? ServiceState(string serviceName)
    {
        const uint ScmConnect = 0x0001, ServiceQueryStatus = 0x0004;
        var scm = OpenSCManager(null, null, ScmConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try { return QueryServiceStatus(svc, out var status) ? (int)status.CurrentState : null; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
