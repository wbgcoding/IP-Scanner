using IpScanner.Core.Localization;

namespace IpScanner.Core.Scanner;

/// <summary>Alerts of one kind that were collected within <see cref="OfflineAlertBatcher.WindowMs"/>.
/// One entry reads as the single-device balloon, several as one counted balloon.</summary>
public sealed record OfflineAlertGroup(OfflineAlertKind Kind, IReadOnlyList<(string Name, TimeSpan Down)> Items)
{
    public bool IsLost => Kind == OfflineAlertKind.Lost;

    public string Title => IsLost ? Loc.TrayDeviceLostTitle : Loc.TrayDeviceBackTitle;

    public string Text
    {
        get
        {
            if (Items.Count == 1)
                return IsLost
                    ? string.Format(Loc.TrayDeviceLostTextFmt, Items[0].Name)
                    : string.Format(Loc.TrayDeviceBackTextFmt, Items[0].Name, NumberFormat.Duration(Items[0].Down));
            return string.Format(IsLost ? Loc.TrayDevicesLostTextFmt : Loc.TrayDevicesBackTextFmt, Items.Count);
        }
    }
}

/// <summary>Merges the offline/back alerts that arrive close together (a local link drop makes every
/// watched device go quiet at once) into one group per kind, so the tray shows one balloon instead of a
/// flood. A group opens with its first alert and is due <see cref="WindowMs"/> later. Pure state, the
/// caller passes a millisecond timestamp and asks for due groups.</summary>
public sealed class OfflineAlertBatcher
{
    public const long WindowMs = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<OfflineAlertKind, (long StartMs, List<(string Name, TimeSpan Down)> Items)> _open = new();

    public void Add(string name, OfflineAlert alert, long nowMs)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(alert.Kind, out var g))
                _open[alert.Kind] = g = (nowMs, new List<(string, TimeSpan)>());
            g.Items.Add((name, alert.Down));
        }
    }

    /// <summary>True while a group waits for its window to end.</summary>
    public bool HasPending { get { lock (_lock) return _open.Count > 0; } }

    /// <summary>Removes and returns the groups whose window has ended, lost before back.</summary>
    public IReadOnlyList<OfflineAlertGroup> TakeDue(long nowMs)
    {
        lock (_lock)
        {
            var due = new List<OfflineAlertGroup>();
            foreach (var kind in new[] { OfflineAlertKind.Lost, OfflineAlertKind.Back })
            {
                if (!_open.TryGetValue(kind, out var g) || nowMs - g.StartMs < WindowMs) continue;
                _open.Remove(kind);
                due.Add(new OfflineAlertGroup(kind, g.Items));
            }
            return due;
        }
    }
}
