using System.Threading;
using System.Windows;
using System.Windows.Automation;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>Small modal-looking notice with an indeterminate bar. The owner is disabled while it
/// runs; the dialog cannot be closed by the user, but work started with
/// <see cref="RunCancellableAsync{T}"/> shows a cancel button.</summary>
public partial class BusyDialog : Window
{
    private bool _release;
    private CancellationTokenSource? _cancel;

    public BusyDialog(string text)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        MessageText.Text = text;
        AutomationProperties.SetName(this, text);
        AutomationProperties.SetName(Bar, text);
        Closing += (_, e) => e.Cancel = !_release;
    }

    /// <summary>Runs <paramref name="work"/> on a worker thread while the dialog is up and the
    /// owner window is disabled, then restores both.</summary>
    public static async Task<T> RunAsync<T>(Window owner, string text, Func<T> work)
    {
        var dialog = new BusyDialog(text) { Owner = owner };
        owner.IsEnabled = false;
        dialog.Show();
        try { return await Task.Run(work); }
        finally
        {
            dialog._release = true;
            dialog.Close();
            owner.IsEnabled = true;
            owner.Activate();
        }
    }

    /// <summary>Like <see cref="RunAsync{T}"/> for work that honours a token: the dialog shows a
    /// cancel button that cancels it, and <paramref name="work"/> decides what a stop returns.</summary>
    public static async Task<T> RunCancellableAsync<T>(Window owner, string text, Func<CancellationToken, Task<T>> work)
    {
        using var cancel = new CancellationTokenSource();
        var dialog = new BusyDialog(text) { Owner = owner, _cancel = cancel };
        dialog.CancelButton.Visibility = Visibility.Visible;
        owner.IsEnabled = false;
        dialog.Show();
        try { return await work(cancel.Token); }
        finally
        {
            dialog._release = true;
            dialog.Close();
            owner.IsEnabled = true;
            owner.Activate();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        try { _cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
