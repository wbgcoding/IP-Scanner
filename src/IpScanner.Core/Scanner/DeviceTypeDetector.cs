using System.Collections.Generic;
using System.Linq;

namespace IpScanner.Core.Scanner;

/// <summary>Broad device categories shown as a small icon next to the hostname.
/// Detection is a best-effort heuristic over hostname, MAC vendor
/// and already-known open ports - it never triggers a port scan of its own,
/// so the icon only sharpens once a device's services happen to be known
/// (e.g. its detail view was opened before).</summary>
public enum DeviceType { Unknown, Server, Router, Printer, Camera, Phone, Tv, Nas, Pc }

public static class DeviceTypeDetector
{
    /// <summary>Resource key (see Resources/Icons.xaml) for a device type.</summary>
    public static string IconKey(DeviceType type) => "Icon" + type;

    public static DeviceType Detect(string? hostname, string? vendor, IEnumerable<int>? openPorts = null)
    {
        var text = $"{hostname} {vendor}".ToLowerInvariant();
        var ports = openPorts as ICollection<int> ?? openPorts?.ToList() ?? new List<int>();
        bool Has(params string[] needles) => needles.Any(text.Contains);
        bool Open(params int[] p) => p.Any(ports.Contains);

        if (Has("router", "gateway", "fritz.box", "fritzbox", "access point", "ubiquiti",
                "mikrotik", "tp-link", "tplink", "netgear", "asustek"))
            return DeviceType.Router;
        if (Has("printer", "drucker", "hewlett packard", " hp ", "epson", "canon", "brother", "lexmark")
            || Open(631, 9100))
            return DeviceType.Printer;
        if (Has("camera", "kamera", "webcam", "hikvision", "dahua", "axis communications", "reolink", "foscam")
            || Open(554))
            return DeviceType.Camera;
        if (Has("iphone", "android", "smartphone", "handy", "samsung electronics", "xiaomi", "huawei", "oneplus"))
            return DeviceType.Phone;
        if (Has("apple-tv", "appletv", "roku", "chromecast", "firetv", "fire tv", "smart-tv", "smarttv", "-tv", " tv "))
            return DeviceType.Tv;
        if (Has("nas", "synology", "qnap", "truenas", "freenas", "western digital"))
            return DeviceType.Nas;
        if (Has("server", "srv-", "-srv", "esxi", "proxmox")
            || (Open(22) && Open(80, 443)) || Open(3306, 5432, 1433))
            return DeviceType.Server;
        if (Has("desktop", "laptop", "notebook", "workstation", "-pc", "pc-", "dell inc", "lenovo", " acer ", "msi ")
            || Open(3389) || Open(445))
            return DeviceType.Pc;
        return DeviceType.Unknown;
    }
}
