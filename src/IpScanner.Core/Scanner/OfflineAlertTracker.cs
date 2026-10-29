namespace IpScanner.Core.Scanner;

public enum OfflineAlertKind { Lost, Back }

/// <summary>One alert: the device just stopped answering (<see cref="Lost"/>) or answered again
/// (<see cref="Back"/>, with how long it was silent).</summary>
public readonly record struct OfflineAlert(OfflineAlertKind Kind, TimeSpan Down);

/// <summary>Turns a device's ping results into at most two alerts per outage: one when
/// <see cref="LostAfter"/> pings in a row were lost, one on the first reply after that.
/// Losses count only after the device has answered once (a device that is already off when the
/// watch starts is not "lost"), and a flapping device is announced at most once per
/// <see cref="CooldownMs"/>: an outage that starts inside that window raises no alert, neither
/// "lost" nor the matching "back", unless it is still going on when the window ends.
/// Pure state, no clock of its own: the caller passes a millisecond timestamp.</summary>
public sealed class OfflineAlertTracker
{
    public const int LostAfter = 3;
    /// <summary>Shortest gap between two "lost" alerts of one device.</summary>
    public const long CooldownMs = 5 * 60 * 1000;

    private bool _armed;
    private int _fails;
    private long _firstFailMs;
    private bool _down;
    private bool _announced;
    private long? _lastLostMs;

    /// <summary>True while an announced outage runs (between the "lost" alert and the first reply after it).</summary>
    public bool IsDown => _down && _announced;

    /// <summary>Feeds one ping result; returns the alert it triggers, if any.</summary>
    public OfflineAlert? Observe(bool success, long nowMs)
    {
        if (success)
        {
            _armed = true;
            var back = _down && _announced
                ? new OfflineAlert(OfflineAlertKind.Back, TimeSpan.FromMilliseconds(Math.Max(0, nowMs - _firstFailMs)))
                : (OfflineAlert?)null;
            _fails = 0;
            _down = false;
            _announced = false;
            return back;
        }

        if (!_armed) return null;
        if (_fails == 0) _firstFailMs = nowMs;
        _fails++;
        if (_fails < LostAfter) return null;
        _down = true;
        if (_announced) return null;
        // Inside the cooldown the outage stays unannounced; it is raised later if it lasts that long.
        if (_lastLostMs is { } last && nowMs - last < CooldownMs) return null;
        _announced = true;
        _lastLostMs = nowMs;
        return new OfflineAlert(OfflineAlertKind.Lost, TimeSpan.FromMilliseconds(Math.Max(0, nowMs - _firstFailMs)));
    }

    /// <summary>Forgets a running outage and the first-reply requirement, e.g. when the alert is
    /// switched off for the device.</summary>
    public void Reset()
    {
        _armed = false;
        _fails = 0;
        _down = false;
        _announced = false;
        _lastLostMs = null;
    }
}
