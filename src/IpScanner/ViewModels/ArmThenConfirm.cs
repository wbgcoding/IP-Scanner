namespace IpScanner.ViewModels;

/// <summary>Shared "arm-then-confirm" delete gesture: the first call arms an
/// entry (returns false); a second call on the SAME entry within the timeout
/// confirms it (returns true) and disarms; a call on a DIFFERENT entry
/// re-arms that one instead and disarms the previous one. Extracted out of
/// MainWindow.Sidebar.cs so the state machine is testable without a live
/// Window, and so the sidebar's three delete gestures share one
/// implementation instead of three copies.</summary>
public sealed class ArmThenConfirm
{
    private readonly TimeSpan _timeout;
    private readonly Action<Action, TimeSpan> _scheduleTimeout;
    private IDeletable? _armed;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private Action? _pendingTimeout;

    /// <param name="timeout">How long an arm stays active before auto-disarming.</param>
    /// <param name="scheduleTimeout">Test seam: replace the WPF DispatcherTimer
    /// with an immediate/manual trigger. Defaults to a real DispatcherTimer.</param>
    public ArmThenConfirm(TimeSpan? timeout = null, Action<Action, TimeSpan>? scheduleTimeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _scheduleTimeout = scheduleTimeout ?? DefaultSchedule;
    }

    private void DefaultSchedule(Action onTimeout, TimeSpan delay)
    {
        // _pendingTimeout is reassigned per call, but the Tick handler itself is
        // subscribed exactly once for this instance's lifetime - a local function
        // closing over `onTimeout` would create a distinct delegate target on
        // every call, so `-=` before `+=` wouldn't actually unsubscribe the
        // previous one and handlers would accumulate on every Arm().
        _pendingTimeout = onTimeout;
        if (_timer is null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Tick += OnTimerTick;
        }
        _timer.Interval = delay;
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer!.Stop();
        _pendingTimeout?.Invoke();
    }

    public void Arm(IDeletable entry)
    {
        if (_armed is not null && !ReferenceEquals(_armed, entry)) _armed.IsDeleteArmed = false;
        entry.IsDeleteArmed = true;
        _armed = entry;
        _scheduleTimeout(Disarm, _timeout);
    }

    public void Disarm()
    {
        if (_armed is not null) _armed.IsDeleteArmed = false;
        _armed = null;
        _timer?.Stop();
    }

    /// <summary>Returns true when this call is the confirming second click.</summary>
    public bool Confirm(IDeletable entry)
    {
        if (!ReferenceEquals(_armed, entry) || !entry.IsDeleteArmed) { Arm(entry); return false; }
        Disarm();
        return true;
    }
}
