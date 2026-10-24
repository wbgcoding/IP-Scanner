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

    /// <summary>Assigns group IDs and returns only the devices whose GroupId actually
    /// changed, so a caller can redraw just those rows instead of the whole table.</summary>
    public static IReadOnlyList<Device> AssignGroups(IList<Device> devices, string? gatewayIp)
    {
        var finalIds = new Dictionary<Device, int>();
        var dynamicDevices = new List<Device>();
        foreach (var d in devices)
        {
            if (d.Ip == gatewayIp) { finalIds[d] = GatewayGroupId; continue; }
            dynamicDevices.Add(d);
            finalIds[d] = UnknownGroup;
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
            foreach (var d in members) finalIds[d] = nextId;
            nextId++;
        }

        var changed = new List<Device>();
        foreach (var d in devices)
        {
            var id = finalIds[d];
            if (d.GroupId == id) continue;
            d.GroupId = id;
            changed.Add(d);
        }
        return changed;
    }

    private static string? GroupKey(Device d)
    {
        // Group by hostname prefix first, then fall back to MAC OUI.
        if (!string.IsNullOrEmpty(d.Hostname) && d.Hostname != Device.Unknown && d.Hostname.Length >= 2)
        {
            // Leading letters only: "pc-a"/"pc-b" -> "pc", "desktop-01" -> "desktop".
            int len = 0;
            while (len < d.Hostname.Length && char.IsLetter(d.Hostname[len])) len++;
            if (len >= 2) return "host:" + d.Hostname[..len].ToLowerInvariant();
        }
        if (!string.IsNullOrEmpty(d.Mac) && d.Mac != Device.Unknown && d.Mac.Length >= 8)
            return "mac:" + d.Mac[..8].ToUpperInvariant();
        return null;
    }
}
