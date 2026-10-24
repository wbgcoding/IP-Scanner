using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.ViewModels;

namespace IpScanner.Views;

public partial class MergeProgressDialog : Window
{
    private readonly KnownDevicesDb _destination;
    private readonly string _sourcePath;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _elapsed = new();
    private bool _completed;

    public int MergedCount { get; private set; }
    public Exception? Error { get; private set; }

    public MergeProgressDialog(KnownDevicesDb destination, string sourcePath)
    {
        _destination = destination;
        _sourcePath = sourcePath;
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SizeText.Text = Loc.MergeSourceSize(new FileInfo(sourcePath).Length / 1048576.0);
        ContentRendered += OnContentRendered;
        Closing += (_, e) =>
        {
            if (_completed) return;
            e.Cancel = true;
            CancelMerge();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CancelMerge();
            e.Handled = true;
        };
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            ContentRendered -= OnContentRendered;
            CancelButton.Focus();
            _elapsed.Start();
            var progress = new Progress<(int done, int total)>(value =>
            {
                var (done, total) = value;
                double percent = total == 0 ? 100 : 100.0 * done / total;
                ProgressBar.Value = percent;
                RecordsText.Text = Loc.MergeRecords(done, total);
                ProgressText.Text = percent.ToString("0%", CultureInfo.CurrentCulture);
                var remaining = total == 0 ? null : ProgressViewModel.ComputeRemaining((double)done / total, _elapsed.Elapsed);
                EtaText.Text = remaining is { } eta ? ProgressViewModel.FormatEta(eta) : "";
            });
            try
            {
                MergedCount = await Task.Run(() => _destination.MergeFrom(_sourcePath, progress, _cancellation.Token));
                _completed = true;
                this.CloseWith(true);
            }
            catch (OperationCanceledException)
            {
                _completed = true;
                this.CloseWith(false);
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Merging the databases failed", ex);
                Error = ex;
                _completed = true;
                this.CloseWith(false);
            }
            finally
            {
                _cancellation.Dispose();
            }
        }, Loc.MergeDb);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
        => CancelMerge();

    private void CancelMerge()
    {
        CancelButton.IsEnabled = false;
        _cancellation.Cancel();
    }
}
