using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.ViewModels.Tools;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

public class UiGuardTests
{
    private static string MethodBody(string relativePath, string signature)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IP-Scanner.slnx")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relativePath)).Replace("\r\n", "\n");
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, signature + " has no block body");
        return text.Substring(start, end - start);
    }

    [Fact]
    public void SidebarDeleteDisarm_KeepsTheNotice_AndEscapeClosesTheEditPopup()
    {
        var sidebar = System.IO.Path.Combine("src", "IpScanner", "MainWindow.Sidebar.cs");
        // The undo hint must survive a list rebuild (it is the only way back after a delete).
        Assert.DoesNotContain("HideNotice(", MethodBody(sidebar, "private void DisarmSidebarDeletes()"));
        Assert.Contains("Key.Escape", MethodBody(sidebar, "private void OnEntryEditKey("));
    }

    private static string ReadSource(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IP-Scanner.slnx")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relativePath)).Replace("\r\n", "\n");
    }

    [Fact]
    public void EntryEditPopup_ClosesOnEscapeFromAnyFocusedControl()
    {
        var sidebar = System.IO.Path.Combine("src", "IpScanner", "MainWindow.Sidebar.cs");
        var body = MethodBody(sidebar, "private void OnEntryEditPreviewKey(");
        Assert.Contains("Key.Escape", body);
        Assert.Contains("EntryEditPopup.IsOpen = false", body);
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        Assert.Contains("PreviewKeyDown=\"OnEntryEditPreviewKey\"", xaml);
        var window = MethodBody(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml.cs"), "private void OnWindowKeyDown(");
        Assert.Contains("Key.Escape when EntryEditPopup.IsOpen", window);
    }

    [Fact]
    public void EntryEditPopup_ButtonRowGivesTheRemoveButtonItsOwnColumn()
    {
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var start = xaml.IndexOf("x:Name=\"EntryEditDeleteBtn\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var row = xaml.Substring(xaml.LastIndexOf("<Grid ", start, StringComparison.Ordinal), 1400);
        Assert.Contains("<Grid.ColumnDefinitions>", row);
        Assert.Contains("<StackPanel Grid.Column=\"2\"", row);
    }

    [Fact]
    public void ProfileEditPopup_ClosesOnEscapeAndGivesTheRemoveButtonItsOwnColumn()
    {
        var body = MethodBody(System.IO.Path.Combine("src", "IpScanner", "MainWindow.Profiles.cs"), "private void OnProfileEditPreviewKey(");
        Assert.Contains("Key.Escape", body);
        Assert.Contains("ProfileEditPopup.IsOpen = false", body);
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        Assert.Contains("PreviewKeyDown=\"OnProfileEditPreviewKey\"", xaml);
        var window = MethodBody(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml.cs"), "private void OnWindowKeyDown(");
        Assert.Contains("Key.Escape when ProfileEditPopup.IsOpen", window);
        var start = xaml.IndexOf("x:Name=\"ProfileEditDeleteBtn\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var row = xaml.Substring(xaml.LastIndexOf("<Grid ", start, StringComparison.Ordinal), 1400);
        Assert.Contains("<Grid.ColumnDefinitions>", row);
        Assert.Contains("<StackPanel Grid.Column=\"2\"", row);
    }

    [Fact]
    public void GroupChip_IsOutlinedInASelectedRowSoTheUngroupedChipStaysVisible()
    {
        // The ungrouped chip color equals the selected-cell background (Surface2).
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var start = xaml.IndexOf("x:Name=\"GroupChip\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var chip = xaml.Substring(start, 900);
        Assert.Contains("AncestorType=DataGridRow", chip);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource Subtext}\"", chip);
    }

    [Fact]
    public void ArchiveRunDetails_LatencyHeaderCarriesTheUnit()
    {
        // The cells are bare numbers, so a bare "Avg" header left the unit open.
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml"));
        Assert.DoesNotContain("Header=\"{loc:L Avg}\"", xaml);
    }

    [Fact]
    public void ToolsNavItems_DoNotRepeatThePageDescriptionAsTooltip()
    {
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "ToolsWindow.xaml"));
        var items = System.Text.RegularExpressions.Regex.Matches(xaml, "<ListBoxItem [^>]*>");
        Assert.NotEmpty(items);
        foreach (System.Text.RegularExpressions.Match item in items)
            Assert.DoesNotMatch("ToolTip=\"\\{loc:L Tool\\w*Desc\\}\"", item.Value);
    }

    [Fact]
    public void ImportListsRows_WrapInsteadOfRunningOffTheEdge()
    {
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var from = xaml.IndexOf("{loc:L ImportListsHeader}", StringComparison.Ordinal);
        var to = xaml.IndexOf("x:Name=\"ImportListsPreview\"", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from);
        var block = xaml.Substring(from, to - from);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(block, "<WrapPanel[ >]").Count >= 2);
    }

    [Fact]
    public void ArchivePage_FitsItsHostWidthAndCoversTheFullArea()
    {
        // Opening scrolls back to the title: filling the run list would otherwise scroll it away at 200 %.
        Assert.Contains("ArchiveScroll.ScrollToTop", MethodBody(System.IO.Path.Combine("src", "IpScanner", "MainWindow.Archive.cs"),
            "private async void OnArchiveClick("));
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var from = xaml.IndexOf("<ScrollViewer x:Name=\"ArchiveScroll\"", StringComparison.Ordinal);
        var to = xaml.IndexOf("</ScrollViewer>", from, StringComparison.Ordinal);
        Assert.True(from > 0 && to > from);
        var block = xaml.Substring(from, to - from);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", block);
        Assert.Contains("Background=\"{DynamicResource Bg}\"", block);
        Assert.DoesNotContain("MinWidth", block);
        Assert.DoesNotContain("Width=\"{Binding", block);
        var page = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml"));
        Assert.Contains("<DockPanel x:Name=\"CompareActions\"", page);
        // The two run pickers stack: side by side their labels are cut at 700 px.
        Assert.Contains("x:Name=\"CompareRunBBox\" ToolTip=\"{loc:L TipCompareRunB}\" Grid.Row=\"1\"", page);
        Assert.Contains("<WrapPanel x:Name=\"ManagementPanel\"", page);
    }

    [Fact]
    public void ArchiveDatePickers_UseTheThemedTemplateAndGlyph()
    {
        // The stock DatePicker draws a gray "15" calendar bitmap that ignores both themes.
        var page = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml"));
        Assert.Contains("<ControlTemplate TargetType=\"DatePicker\">", page);
        foreach (var part in new[] { "PART_Root", "PART_TextBox", "PART_Button", "PART_Popup" })
            Assert.Contains("x:Name=\"" + part + "\"", page);
        Assert.Contains("Stroke=\"{DynamicResource Subtext}\"", page);
        // Its popup calendar paints a white face with fixed dark glyphs unless every part is restyled.
        foreach (var target in new[] { "Calendar", "CalendarItem", "CalendarDayButton", "CalendarButton" })
            Assert.Contains("<ControlTemplate TargetType=\"" + target + "\">", page);
        foreach (var part in new[] { "PART_HeaderButton", "PART_PreviousButton", "PART_NextButton",
                     "PART_MonthView", "PART_YearView", "PART_DisabledVisual", "PART_CalendarItem" })
            Assert.Contains("x:Name=\"" + part + "\"", page);
        // An implicit Calendar style never reaches the popup: the picker binds the calendar's Style itself.
        Assert.Contains("<Setter Property=\"CalendarStyle\"", page);
        // A presenter's text takes the app-wide TextBlock color, so selected and today states would not show.
        var dayTemplate = page[page.IndexOf("<ControlTemplate TargetType=\"CalendarDayButton\">")..];
        dayTemplate = dayTemplate[..dayTemplate.IndexOf("</ControlTemplate>")];
        Assert.DoesNotContain("<ContentPresenter", dayTemplate);
    }

    [Fact]
    public void About_ShowsTheCopyrightNotice()
    {
        Assert.Matches(@"^© \d{4} BGCoding$", IpScanner.Views.AboutWindow.CopyrightLine);
        Assert.Contains("x:Name=\"AboutCopyrightText\"", ReadSource(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml")));
        Assert.Contains("x:Name=\"CopyrightText\"", ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "AboutWindow.xaml")));
    }

    [Fact]
    public void CheckBoxTick_IsNotMirroredInRtl()
    {
        var styles = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Resources", "Styles.xaml"));
        Assert.Matches("<Path x:Name=\"check\"[^>]*FlowDirection=\"LeftToRight\"", styles);
    }

    [Fact]
    public void MainWindowClosing_ToleratesAShutdownAlreadyInProgress()
    {
        // Setting ShutdownMode during logoff or an update restart throws and lands in error.log.
        var body = MethodBody(System.IO.Path.Combine("src", "IpScanner", "MainWindow.Graphs.cs"),
            "protected override void OnClosing(");
        Assert.Contains("catch (InvalidOperationException)", body);
    }

    [Fact]
    public async Task Run_CancellationIsSilent()
    {
        var reported = new List<Exception>();

        await UiGuard.Run(() => throw new OperationCanceledException(), reported.Add);
        await UiGuard.Run(async () => { await Task.Yield(); throw new TaskCanceledException(); }, reported.Add);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task Run_ReportsAFailureOnceAndDoesNotRethrow()
    {
        var reported = new List<Exception>();

        await UiGuard.Run(async () => { await Task.Yield(); throw new InvalidOperationException("boom"); }, reported.Add);

        Assert.Equal("boom", Assert.Single(reported).Message);
    }

    [Fact]
    public async Task Run_ReportingThatThrowsStillDoesNotEscape()
    {
        await UiGuard.Run(() => throw new InvalidOperationException("boom"), _ => throw new InvalidOperationException("dialog"));
    }

    [Fact]
    public async Task Run_SuccessReportsNothing()
    {
        var reported = new List<Exception>();
        var ran = false;

        await UiGuard.Run(() => { ran = true; return Task.CompletedTask; }, reported.Add);

        Assert.True(ran);
        Assert.Empty(reported);
    }

    [Fact]
    public void Clipboard_EmptyTextIsRefusedWithoutTouchingTheClipboard()
    {
        Assert.False(ClipboardHelper.TrySetText(null));
        Assert.False(ClipboardHelper.TrySetText(""));
    }

    [Fact]
    public async Task BackgroundTask_LogsAFaultAndIgnoresCancellationAndSuccess()
    {
        var logged = new List<string>();

        BackgroundTask.Observe(Task.FromException(new InvalidOperationException("late")), "job", logged.Add);
        BackgroundTask.Observe(Task.FromCanceled(new System.Threading.CancellationToken(true)), "cancelled", logged.Add);
        BackgroundTask.Observe(Task.CompletedTask, "fine", logged.Add);
        await Task.Delay(100);

        var line = Assert.Single(logged);
        Assert.Contains("job", line);
        Assert.Contains("late", line);
    }

    [Fact]
    public async Task OnDispatcher_FromAPoolThread_RunsOnTheDispatcherThread()
    {
        var ran = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            await Task.Run(() => UiGuard.OnDispatcher(dispatcher, () => ran.SetResult(Environment.CurrentManagedThreadId)));
            return Environment.CurrentManagedThreadId;
        });

        Assert.Equal(uiThread, await ran.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ShowFailure_AThirdFailureInABurstAsksToExitAndLaterOnesStaySilent()
    {
        var gate = new CrashBurstGate();
        int dialogs = 0, prompts = 0;
        Exception? prompted = null;
        var failure = new InvalidOperationException("outer", new ArgumentException("root cause"));

        for (int i = 0; i < 6; i++)
            App.ShowFailure(failure, _ => dialogs++, gate, root => { prompts++; prompted = root; });

        Assert.Equal(2, dialogs);
        Assert.Equal(1, prompts);
        Assert.Equal("root cause", prompted!.Message);
    }

    [Fact]
    public void CrashBurstGate_FailuresFarApartNeverCountAsABurst()
    {
        var gate = new CrashBurstGate();
        var at = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 5; i++)
            Assert.Equal(FailureDialogAction.Show, gate.Register(at.AddSeconds(i * 10)));
    }

    [Fact]
    public void WakeOnLanTool_StatusLineSitsDirectlyUnderTheButtonRow()
    {
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "Tools", "WakeOnLanTool.xaml"));
        var status = xaml.IndexOf("Text=\"{Binding Status}\"", StringComparison.Ordinal);
        Assert.True(status > 0);
        // Right after the pickers, before the recent targets list, not a separate bottom grid row.
        Assert.True(status > xaml.IndexOf("x:Name=\"NetworkCardBox\"", StringComparison.Ordinal));
        Assert.True(status < xaml.IndexOf("HasRecentTargets", StringComparison.Ordinal));
        Assert.DoesNotContain("Grid.Row=\"2\"", xaml);
    }

    [Fact]
    public void WakeOnLanTool_PickersWrapBelowSoTheMacBoxKeepsItsWidth()
    {
        var xaml = ReadSource(System.IO.Path.Combine("src", "IpScanner", "Views", "Tools", "WakeOnLanTool.xaml"));
        // The pickers sit in their own row below the MAC box and its button, so the box keeps its width.
        var buttonRowEnd = xaml.IndexOf("x:Name=\"StopButton\"", StringComparison.Ordinal);
        Assert.True(buttonRowEnd > xaml.IndexOf("x:Name=\"MacBox\"", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("x:Name=\"KnownDevicesBox\"", StringComparison.Ordinal) > buttonRowEnd);
        Assert.True(xaml.IndexOf("x:Name=\"NetworkCardBox\"", StringComparison.Ordinal) > buttonRowEnd);
        // "Ethernet (192.168.2.17)" turns its brackets around when laid out right-to-left.
        var doc = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "Views", "Tools", "WakeOnLanTool.xaml"));
        Assert.Equal("LeftToRight", (string?)NamedElement(doc, "NetworkCardBox").Attribute("FlowDirection"));
    }

    [Fact]
    public async Task WakeOnLanTool_FailedInitializationResetsTheFlagSoTheNextLoadRetries()
    {
        var vm = new WakeOnLanToolViewModel();

        await Assert.ThrowsAnyAsync<Exception>(() => vm.InitializeAsync(null!, _ => { }));

        Assert.False(vm.Initialized);
    }

    private static readonly System.Xml.Linq.XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static System.Xml.Linq.XDocument LoadXaml(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IP-Scanner.slnx")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(dir!, relativePath));
    }

    private static System.Xml.Linq.XElement NamedElement(System.Xml.Linq.XDocument doc, string name)
        => System.Linq.Enumerable.Single(doc.Descendants(), e => (string?)e.Attribute(XamlNs + "Name") == name);

    [Fact]
    public void ArchiveDatePickers_HaveAThemedLocalizedHintStyle()
    {
        var text = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml")).ToString();
        Assert.Contains("TargetType=\"DatePickerTextBox\"", text);
        Assert.Contains("{loc:L PickDate}", text);
    }

    [Fact]
    public void ScanPresetBox_KeepsAReadableMinimumWidth()
    {
        var box = NamedElement(LoadXaml(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml")), "ScanPresetBox");
        Assert.True(double.Parse((string?)box.Attribute("MinWidth") ?? "0", System.Globalization.CultureInfo.InvariantCulture) >= 160);
    }

    [Fact]
    public void NumericAndCidrInputs_StayLeftToRightInRtl()
    {
        var doc = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var names = new List<string> { "CidrBox", "SubnetBox", "InternetTimeoutBox", "GraphMaxBox" };
        names.AddRange(doc.Descendants()
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource SettingNumberBox}")
            .Select(e => (string?)e.Attribute(XamlNs + "Name") ?? ""));
        Assert.True(names.Count > 20);
        foreach (var name in names)
        {
            var box = NamedElement(doc, name);
            Assert.True((string?)box.Attribute("FlowDirection") == "LeftToRight", name + " box");
            var panel = box.Parent!;
            if (panel.Name.LocalName == "StackPanel" && panel.Elements().Any(e => e.Name.LocalName == "TextBlock"))
                Assert.True((string?)panel.Attribute("FlowDirection") == "LeftToRight", name + " unit panel");
        }
    }

    [Fact]
    public void HeaderCountTexts_StayLeftToRightInRtl()
    {
        var doc = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        foreach (var path in new[] { "Progress.DeviceCountText", "Progress.PingCountText" })
        {
            var tb = doc.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                && ((string?)e.Attribute("Text"))?.Contains(path) == true);
            Assert.True((string?)tb.Attribute("FlowDirection") == "LeftToRight", path);
        }
    }

    [Fact]
    public void SettingsCloseButton_IsNotInsideAWrapPanel()
    {
        var doc = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "MainWindow.xaml"));
        var close = System.Linq.Enumerable.Single(doc.Descendants(), e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "OnSettingsCancel"
            && ((string?)e.Attribute("Content") ?? "").Contains("Close"));
        Assert.DoesNotContain(close.Ancestors(), a => a.Name.LocalName == "WrapPanel");
    }

    [Fact]
    public void ArchiveManagementRow_WrapsAndHidesInCompareMode()
    {
        var doc = LoadXaml(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml"));
        var panel = NamedElement(doc, "ManagementPanel");
        Assert.Equal("WrapPanel", panel.Name.LocalName);
        Assert.DoesNotContain(panel.Ancestors(), a => a.Name.LocalName == "ScrollViewer");
        Assert.Contains("ManagementRow.Visibility", MethodBody(System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.Compare.cs"), "private void SetCompareMode("));
    }

    [Fact]
    public void ArchiveAndToolGridTextColumns_UseTheTrimmedCellStyle()
    {
        var files = new List<string> { System.IO.Path.Combine("src", "IpScanner", "Views", "ArchivePage.xaml") };
        var root = AppContext.BaseDirectory;
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root, "IP-Scanner.slnx")))
            root = System.IO.Path.GetDirectoryName(root);
        foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.Combine(root!, "src", "IpScanner", "Views", "Tools"), "*.xaml"))
            files.Add(System.IO.Path.GetRelativePath(root!, f));

        var bare = new List<string>();
        var columns = 0;
        foreach (var file in files)
            foreach (var col in LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "DataGridTextColumn"))
            {
                columns++;
                if (!System.Text.RegularExpressions.Regex.IsMatch((string?)col.Attribute("ElementStyle") ?? "",
                        @"^\{StaticResource (TrimmedCell|ArchiveCenterCell|RunNetworkCell|ChangeTypeCell)\}$")
                    && !col.Elements().Any(c => c.Name.LocalName == "DataGridTextColumn.ElementStyle"))
                    bare.Add(file + ": " + (string?)col.Attribute("Header"));
            }
        Assert.True(columns > 0);
        Assert.Empty(bare);
    }
}
