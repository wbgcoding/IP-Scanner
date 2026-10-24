using System.Diagnostics;
using System.IO;
using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>Themed error dialog (replaces the bare-technical-text MessageBox) - always shows
/// a plain-language cause and next step, with the raw exception text collapsed underneath and
/// a way to copy it or open the on-disk error log.</summary>
public partial class ErrorDialog : Window
{
    private readonly string _details;

    internal ErrorDialog(string title, string details, string? cause, string? nextStep)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _details = details;
        TitleText.Text = title;
        CauseText.Text = cause ?? Loc.ErrorDialogCause;
        NextStepText.Text = nextStep ?? Loc.ErrorDialogNextStep;
        NextStepText.Visibility = string.IsNullOrEmpty(NextStepText.Text) ? Visibility.Collapsed : Visibility.Visible;
        DetailsExpander.Header = Loc.ErrorDialogDetails;
        DetailsText.Text = details;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
        CopyBtn.Content = Loc.ErrorDialogCopyDetails;
        CopyBtn.Visibility = DetailsExpander.Visibility;
        OpenLogBtn.Content = Loc.ErrorDialogOpenLog;
        OpenLogBtn.IsEnabled = File.Exists(App.ErrorLogPath);
        OkBtn.Content = Loc.Ok;
    }

    /// <summary>Shows the themed error dialog. <paramref name="cause"/>/<paramref name="nextStep"/>
    /// default to generic localized text when the caller has nothing more specific to say.</summary>
    public static void Show(Window? owner, string title, string technicalDetails, string? cause = null, string? nextStep = null)
    {
        var dlg = new ErrorDialog(title, technicalDetails, cause, nextStep);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    /// <summary>Shows the dialog for an exception: cause and next step come from
    /// <see cref="IpScanner.Core.FailureText.Describe"/>, the raw exception text stays in the
    /// collapsed details.</summary>
    public static void Show(Window? owner, string title, Exception ex, string? path)
    {
        var (cause, next) = IpScanner.Core.FailureText.Describe(ex, path);
        Show(owner, title, ex.ToString(), cause, next);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ClipboardHelper.TrySetText(_details)) CopyBtn.Content = Loc.ErrorDialogCopied;
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.ErrorLogPath) { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Opening the log failed", ex); /* no default text-file handler configured - nothing more we can do */ }
    }

    private void OnOk(object sender, RoutedEventArgs e) => this.CloseWith(true);
}
