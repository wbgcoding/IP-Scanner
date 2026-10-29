namespace IpScanner.ViewModels.Tools;

/// <summary>Owns one child view model per Tools-window page. This class
/// only wires them together - each tool's own behavior is filled in by its own
/// plan item (67-72).</summary>
public sealed class ToolsViewModel
{
    public WakeOnLanToolViewModel WakeOnLan { get; } = new();
    public PortCheckToolViewModel PortCheck { get; } = new();
    public DnsLookupToolViewModel DnsLookup { get; } = new();
    public PingRouteToolViewModel PingRoute { get; } = new();
    public CalculatorToolViewModel Calculator { get; } = new();
    public MacToolViewModel Mac { get; } = new();
    public CertificateToolViewModel Certificate { get; } = new();
    public HostDiscoveryToolViewModel HostDiscovery { get; } = new();
    public ServiceDetectionToolViewModel ServiceDetection { get; } = new();
    public OsEstimateToolViewModel OsEstimate { get; } = new();
    public ChecksToolViewModel Checks { get; } = new();
    public ScanExportToolViewModel ScanExport { get; } = new();

    /// <summary>Cancels every tool's in-flight work - called
    /// once when the Tools window closes, never on a category switch (a running tool
    /// keeps going in the background while you look at another page, by design).</summary>
    public void StopAll()
    {
        WakeOnLan.Stop();
        PortCheck.Stop();
        DnsLookup.Stop();
        PingRoute.Stop();
        Certificate.Stop();
        HostDiscovery.Stop();
        ServiceDetection.Stop();
        OsEstimate.Stop();
        Checks.Stop();
        ScanExport.Stop();
    }
}
