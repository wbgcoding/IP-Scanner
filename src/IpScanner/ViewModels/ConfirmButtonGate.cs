namespace IpScanner.ViewModels;

/// <summary>Arm-then-confirm gesture for a single button: the first press arms it
/// (the caller shows the confirm look through <see cref="ArmedChanged"/>), a second
/// press within the timeout confirms. Built on <see cref="ArmThenConfirm"/> so every
/// delete or reset gesture shares one state machine and one timer seam.</summary>
public sealed class ConfirmButtonGate
{
    private sealed class Flag : IDeletable
    {
        private readonly Action<bool> _changed;
        private bool _armed;
        public Flag(Action<bool> changed) => _changed = changed;
        public bool IsDeleteArmed
        {
            get => _armed;
            set
            {
                if (_armed == value) return;
                _armed = value;
                _changed(value);
            }
        }
    }

    private readonly ArmThenConfirm _arm;
    private readonly Flag _flag;

    /// <param name="armedChanged">Called with true when the button becomes armed and false when it disarms.</param>
    /// <param name="timeout">How long the armed look stays before it reverts.</param>
    /// <param name="scheduleTimeout">Test seam for the disarm timer.</param>
    public ConfirmButtonGate(Action<bool> armedChanged, TimeSpan? timeout = null,
        Action<Action, TimeSpan>? scheduleTimeout = null)
    {
        _flag = new Flag(armedChanged);
        _arm = new ArmThenConfirm(timeout, scheduleTimeout);
    }

    public bool IsArmed => _flag.IsDeleteArmed;

    /// <summary>Returns true when this press is the confirming one.</summary>
    public bool Press() => _arm.Confirm(_flag);

    public void Reset() => _arm.Disarm();
}
