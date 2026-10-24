using System.Windows;

namespace IpScanner.Views;

/// <summary>Wraps the body of an async event handler so a failure ends up in the error log and a
/// dialog instead of the dispatcher's unhandled-exception path. Cancellation is not a failure.</summary>
internal static class UiGuard
{
    public static Task Run(Func<Task> work, string title)
        => Run(work, ex => Report(ex, title));

    internal static async Task Run(Func<Task> work, Action<Exception> report)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try { report(ex); }
            catch { /* reporting must never throw out of an event handler */ }
        }
    }

    private static void Report(Exception ex, string title)
    {
        IpScanner.Core.Log.Error(IpScanner.Core.LogArea.Ui, title, ex);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        // A handler can fail on a pool thread (network change events), and the dialog counts
        // against the same burst limit as unhandled exceptions.
        OnDispatcher(dispatcher, () => App.ShowFailure(ex, owner => ErrorDialog.Show(owner, title, ex, null)));
    }

    internal static void OnDispatcher(System.Windows.Threading.Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
