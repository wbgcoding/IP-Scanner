#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;
using IpScanner.Views.Review;
using Xunit;

namespace IpScanner.Tests;

/// <summary>Every interactive control of the main window, the tools window and the dialogs carries
/// a tooltip (its own or one inherited from a wrapping row) that adds information to the label.</summary>
public class TooltipCoverageTests
{
    // Window chrome never gets a tooltip.
    private static readonly HashSet<string> Decorative = new()
    {
        "MinimizeButton", "MaximizeRestoreButton", "CloseButton",
    };

    private static bool IsInteractive(DependencyObject d) =>
        d is Button or ToggleButton or ComboBox or TextBox or Slider or PasswordBox or ListBoxItem;

    private static bool IsTemplatePart(FrameworkElement e) => e.TemplatedParent is Control;

    private static bool HasTip(DependencyObject? d)
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
        {
            if (cur is FrameworkElement fe)
            {
                var tip = ToolTipService.GetToolTip(fe) ?? fe.ToolTip;
                if (tip is string s ? s.Trim().Length > 0 : tip is not null) return true;
            }
        }
        return false;
    }

    private static string? TipText(FrameworkElement e)
        => (ToolTipService.GetToolTip(e) ?? e.ToolTip) as string;

    private static string Describe(FrameworkElement e)
    {
        var label = e is ContentControl { Content: string s } ? s : "";
        var name = string.IsNullOrEmpty(e.Name) ? "" : e.Name;
        var parent = VisualTreeHelper.GetParent(e) as FrameworkElement;
        return $"{e.GetType().Name} name='{name}' label='{label}' parent={parent?.GetType().Name}/{parent?.Name}";
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            yield return cur;
            foreach (var child in LogicalTreeHelper.GetChildren(cur).OfType<DependencyObject>()) stack.Push(child);
            if (cur is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int n = VisualTreeHelper.GetChildrenCount(cur);
                for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(cur, i));
            }
        }
    }

    internal static List<string> Problems(string surface, DependencyObject root)
    {
        var list = new List<string>();
        foreach (var d in Walk(root))
        {
            if (d is not FrameworkElement e || !IsInteractive(e) || IsTemplatePart(e)) continue;
            if (Decorative.Contains(e.Name)) continue;
            if (!HasTip(e)) { list.Add($"{surface}: no tooltip: {Describe(e)}"); continue; }
            var tip = TipText(e);
            if (tip is not null && e is ContentControl { Content: string label }
                && string.Equals(tip.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase))
                list.Add($"{surface}: tooltip repeats the label: {Describe(e)}");
        }
        return list;
    }

    private static ReviewContext Context(string temp, string locale)
        => new(locale, "dark", 1320, 720, 100, temp, false);

    private static string NewTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipscanner-tooltips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task MainWindow_EveryInteractiveControlHasATooltip()
    {
        var temp = NewTemp();
        var problems = await WpfTestHost.RunAsync(async () =>
        {
            var found = new List<string>();
            var c = Context(temp, "en");
            var main = await ReviewSurfaces.NewMainAsync(c);
            found.AddRange(Problems("main", main));
            await main.ReviewOpenArchiveAsync();
            await ReviewSurfaces.SettleAsync(400);
            found.AddRange(Problems("archive", main));
            await main.ReviewOpenArchiveCompareAsync();
            await ReviewSurfaces.SettleAsync(400);
            found.AddRange(Problems("archive.compare", main));
            await main.ReviewOpenLogAsync();
            await ReviewSurfaces.SettleAsync(400);
            found.AddRange(Problems("log", main));
            foreach (var page in main.ReviewSettingsPages)
            {
                main.ReviewOpenSettings(page);
                await ReviewSurfaces.SettleAsync(200);
                found.AddRange(Problems("settings." + page, main));
            }
            main.Hide(); // Close would end the shared test Application
            return found.Distinct().ToList();
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task ToolsWindow_EveryPageAndControlHasATooltip()
    {
        var temp = NewTemp();
        var problems = await WpfTestHost.RunAsync(async () =>
        {
            var found = new List<string>();
            var main = await ReviewSurfaces.NewMainAsync(Context(temp, "en"));
            foreach (var page in ReviewSurfaces.ToolPages)
            {
                var tools = main.ReviewOpenTools(page);
                await ReviewSurfaces.SettleAsync(200);
                found.AddRange(Problems("tools." + page, tools));
                if (tools.FindName("ToolDescription") is not TextBlock { Text: var desc } || desc.Trim().Length == 0)
                    found.Add($"tools.{page}: page description missing");
                tools.Close();
            }
            main.Hide(); // Close would end the shared test Application
            return found.Distinct().ToList();
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Dialogs_EveryInteractiveControlHasATooltip()
    {
        var problems = await WpfTestHost.RunAsync(() =>
        {
            var found = new List<string>();
            var windows = new (string Name, Func<Window> Make)[]
            {
                ("about", () => new AboutWindow()),
                ("error", () => new ErrorDialog("Title", "Message", null, null)),
                ("update", () => new UpdateDialog("2.11.0", "2.10.0")),
                ("fullscan", () => new FullPortScanWarningDialog(true)),
                ("intrusivecheck", () => new IntrusiveCheckWarningDialog(true)),
                ("archiveconfirm", () => new ArchiveConfirmDialog("Message", "Ok", true, "Title")),
                ("manualexport", () => new ManualExportDialog(IpScanner.Core.Export.ExportScope.Filtered, IpScanner.Core.Export.ExportFormat.Csv, true)),
                ("configmerge", () => new ConfigMergeDialog()),
                ("backuppassword.set", () => new BackupPasswordDialog(true)),
                ("backuppassword.ask", () => new BackupPasswordDialog(false, true)),
                ("busy", () => new BusyDialog("Working")),
            };
            foreach (var (name, make) in windows)
            {
                var w = make();
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -20000;
                w.ShowActivated = false;
                w.ShowInTaskbar = false;
                w.Show();
                w.UpdateLayout();
                found.AddRange(Problems("dialog." + name, w));
                w.Close();
            }
            // Never shown (it would start the merge); its logical tree is complete after construction.
            var temp = NewTemp();
            var source = Path.Combine(temp, "merge-source.db");
            _ = new IpScanner.Core.Data.KnownDevicesDb(source);
            var merge = new MergeProgressDialog(new IpScanner.Core.Data.KnownDevicesDb(Path.Combine(temp, "merge-dest.db")), source);
            found.AddRange(Problems("dialog.merge", merge));
            return Task.FromResult(found.Distinct().ToList());
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
#endif
