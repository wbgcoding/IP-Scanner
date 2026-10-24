using System.Windows;
using System.IO;
using IpScanner.Core.Import;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // Import subnets/pinned IPs/internet hosts from a text or CSV file.
    // Browsing only parses and previews (accepted/skipped counts) - nothing is
    // written to _config until the user explicitly clicks "Import", and the
    // append/replace choice is theirs, never assumed.
    private ListImportParser.Result? _pendingImport;
    private string? _pendingImportText;   // the browsed file, kept so a target change can re-parse it
    private bool _importItemsLoading;     // SetImportListsItems resets the combos; that is no user choice

    private void SetImportListsItems()
    {
        int target = Math.Max(0, ImportTargetBox.SelectedIndex);
        int mode = Math.Max(0, ImportModeBox.SelectedIndex);
        _importItemsLoading = true;
        try
        {
            ImportTargetBox.ItemsSource = new[] { Loc.Subnets, Loc.PinnedIps, Loc.InternetLatency };
            ImportModeBox.ItemsSource = new[] { Loc.ImportModeAppend, Loc.ImportModeReplace };
            ImportTargetBox.SelectedIndex = target;
            ImportModeBox.SelectedIndex = mode;
        }
        finally { _importItemsLoading = false; }
        ImportTargetBox.SelectionChanged -= OnImportTargetChanged;   // idempotent: this runs again on a language switch
        ImportTargetBox.SelectionChanged += OnImportTargetChanged;
    }

    private static ImportTargetKind ImportKindFor(int comboIndex) => comboIndex switch
    {
        1 => ImportTargetKind.Pinned,
        2 => ImportTargetKind.Host,
        _ => ImportTargetKind.Network,
    };

    private static EntryKind EntryKindFor(ImportTargetKind kind) => kind switch
    {
        ImportTargetKind.Pinned => EntryKind.Pinned,
        ImportTargetKind.Host => EntryKind.Host,
        _ => EntryKind.Network,
    };

    private void OnImportListsBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.ImportListsFileFilter };
        if (dlg.ShowDialog(this) != true) return;
        string text;
        try { text = File.ReadAllText(dlg.FileName); }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Tools, "Reading the import file failed", ex);
            ErrorDialog.Show(this, Loc.ListImportFailedTitle, ex, dlg.FileName);
            return;
        }
        _pendingImportText = text;
        ShowImportPreview();
    }

    // The kind chosen after browsing re-reads the same file: the preview and counts must
    // describe what Import will really write, and a stale "N entries saved" line goes.
    private void OnImportTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_importItemsLoading || _pendingImportText is null || ImportTargetBox.SelectedIndex < 0) return;
        ShowImportPreview();
    }

    private void ShowImportPreview()
    {
        var result = ListImportParser.Parse(_pendingImportText ?? "", ImportKindFor(ImportTargetBox.SelectedIndex));
        _pendingImport = result;
        ImportListsPreview.Text = result.Accepted.Count > 0
            ? Loc.ImportListsPreviewText(result.Accepted.Count, result.SkippedLines, result.TotalLines)
            : Loc.ImportListsNoneText(result.SkippedLines, result.TotalLines);
        ImportListsCommitBtn.IsEnabled = result.Accepted.Count > 0;
    }

    private void OnImportListsCommit(object sender, RoutedEventArgs e)
    {
        if (_pendingImport is not { Accepted.Count: > 0 } result) return;
        bool replace = ImportModeBox.SelectedIndex == 1;
        var kind = EntryKindFor(ImportKindFor(ImportTargetBox.SelectedIndex));
        var existing = replace ? new List<NetEntry>() : EntryListFor(kind).Select(NetEntry.Parse).ToList();
        var targets = new HashSet<string>(existing.Select(en => en.Target));
        foreach (var entry in result.Accepted)
        {
            if (!targets.Add(entry.Target)) continue;   // first occurrence wins, avoids silent duplicates
            existing.Add(entry);
        }
        Sidebar.ReplaceAll(kind, existing.Select(en => en.ToString()).ToList());
        _pendingImport = null;
        ImportListsCommitBtn.IsEnabled = false;
        ImportListsPreview.Text = Loc.ImportListsDoneText(existing.Count);
    }
}
