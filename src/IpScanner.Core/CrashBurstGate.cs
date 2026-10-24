namespace IpScanner.Core;

internal enum FailureDialogAction { Show, AskToExit, Suppressed }

/// <summary>Decides whether a reported failure may open a dialog. A burst of failures in a short
/// window would otherwise stack modal dialogs and make the app unusable: the third one in a row
/// asks whether to exit, and later ones stay silent (they are still logged by the caller).</summary>
internal sealed class CrashBurstGate
{
    private const int Threshold = 3;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private DateTime _last = DateTime.MinValue;
    private int _count;
    private bool _suppressed;

    public FailureDialogAction Register(DateTime utcNow)
    {
        lock (_sync)
        {
            _count = utcNow - _last < Window ? _count + 1 : 1;
            _last = utcNow;
            if (_suppressed) return FailureDialogAction.Suppressed;
            if (_count < Threshold) return FailureDialogAction.Show;
            _suppressed = true;
            return FailureDialogAction.AskToExit;
        }
    }
}
