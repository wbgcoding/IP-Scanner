using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IpScanner.Core;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>One line of the log viewer: the readable title, the short columns and the full text
/// that shows when the row is expanded.</summary>
public sealed class LogRowViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string _copyLabel = Loc.LogCopyRow;

    public LogRowViewModel(LogEntry entry)
    {
        Entry = entry;
        Level = entry.Level;
        LevelText = LogFilter.LevelName(entry.Level);
        AreaText = LogFilter.AreaName(entry.Area);
        TimeText = entry.Time.ToString("yyyy-MM-dd HH:mm:ss");
        Title = LogTitles.For(entry) ?? entry.Message;
        FullText = Log.Format(entry);
    }

    public LogEntry Entry { get; }
    public LogLevel Level { get; }
    public string LevelText { get; }
    public string AreaText { get; }
    public string TimeText { get; }
    public string Title { get; }
    public string FullText { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    public string CopyLabel
    {
        get => _copyLabel;
        set { if (_copyLabel != value) { _copyLabel = value; Raise(nameof(CopyLabel)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The log viewer: same overlay shell as the archive page. Filters by area and level, searches
/// the text, and expands a row to its full text with a copy button.</summary>
public partial class LogPage : UserControl
{
    private const int RefreshDelayMs = 250;

    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(RefreshDelayMs) };
    private readonly HashSet<LogEntry> _expanded = [];
    private bool _loading;

    public event EventHandler? CloseRequested;

    /// <summary>The user flipped the "Verbose" switch; the host saves the setting.</summary>
    public event EventHandler<bool>? VerboseChanged;

    public LogPage()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => { _refresh.Stop(); Reload(); };
        FillFilters();
        // New entries arrive on any thread; they only mark the list stale, the timer reloads once.
        // The static event would keep a closed page alive, so it is only held while the page is loaded.
        Loaded += (_, _) => { Log.Added -= OnLogAdded; Log.Added += OnLogAdded; };
        Unloaded += (_, _) => { Log.Added -= OnLogAdded; _refresh.Stop(); };
    }

    /// <summary>Runs on whatever thread wrote the entry: visibility and the timer belong to the page's thread.</summary>
    internal void OnLogAdded(LogEntry entry)
        => Dispatcher.BeginInvoke(() => { if (IsVisible && !_refresh.IsEnabled) _refresh.Start(); });

    /// <summary>Shows what is buffered at once, then merges the log file of earlier sessions in the
    /// background so a fresh start does not open an empty list.</summary>
    public async Task OpenAsync()
    {
        FillFilters();
        SearchBox.Text = "";
        Reload();
        SearchBox.Focus();
        if (await Task.Run(Log.LoadHistory) > 0) Reload();
    }

    /// <summary>Shows the saved setting without raising <see cref="VerboseChanged"/>.</summary>
    public void SetVerbose(bool on)
    {
        var was = _loading;
        _loading = true;
        try { VerboseBox.IsChecked = on; }
        finally { _loading = was; }
    }

    private void OnVerboseToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        VerboseChanged?.Invoke(this, VerboseBox.IsChecked == true);
    }

    public void RefreshLanguage() { FillFilters(); Reload(); }

    private void FillFilters()
    {
        _loading = true;
        try
        {
            var area = AreaBox.SelectedIndex;
            var level = LevelBox.SelectedIndex;
            AreaBox.Items.Clear();
            AreaBox.Items.Add(Loc.LogAllAreas);
            foreach (var a in Enum.GetValues<LogArea>()) AreaBox.Items.Add(LogFilter.AreaName(a));
            LevelBox.Items.Clear();
            LevelBox.Items.Add(Loc.LogAllLevels);
            foreach (var l in Enum.GetValues<LogLevel>()) LevelBox.Items.Add(LogFilter.LevelName(l));
            AreaBox.SelectedIndex = Math.Max(0, area);
            LevelBox.SelectedIndex = Math.Max(0, level);
        }
        finally { _loading = false; }
    }

    private LogArea? SelectedArea => AreaBox.SelectedIndex > 0 ? (LogArea)(AreaBox.SelectedIndex - 1) : null;
    private LogLevel? SelectedLevel => LevelBox.SelectedIndex > 0 ? (LogLevel)(LevelBox.SelectedIndex - 1) : null;

    private void Reload()
    {
        if (_loading) return;
        var all = Log.Snapshot();
        var shown = LogFilter.Apply(all, SelectedArea, SelectedLevel, SearchBox.Text);
        var rows = new List<LogRowViewModel>(shown.Count);
        foreach (var entry in shown)
        {
            var row = new LogRowViewModel(entry) { IsExpanded = _expanded.Contains(entry) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LogRowViewModel.IsExpanded)) return;
                if (row.IsExpanded) _expanded.Add(row.Entry); else _expanded.Remove(row.Entry);
            };
            rows.Add(row);
        }
        RowsList.ItemsSource = rows;
        CountText.Text = Loc.LogRowsText(rows.Count, all.Count);
        EmptyText.Text = all.Count == 0 ? Loc.LogEmpty : Loc.LogNoMatch;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterChanged(object sender, EventArgs e) => Reload();

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LogRowViewModel row } && ClipboardHelper.TrySetText(row.FullText))
            row.CopyLabel = Loc.ErrorDialogCopied;
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn(LogArea.Ui, "Opening the log failed", ex); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
