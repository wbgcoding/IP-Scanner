using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IpScanner.Core.Cli;
using IpScanner.Core.Localization;

namespace IpScanner;

// Reference part of the Help page: purpose, tools, command line options, examples and exit codes.
public partial class MainWindow
{
    private StackPanel? _helpReferencePanel;
    private readonly Dictionary<string, FrameworkElement> _helpAnchors = new();

    private static TextBlock MonoText(string text, string brushKey, double size, Thickness margin)
    {
        var tb = HelpText(text, brushKey, size, null, margin);
        tb.FontFamily = new FontFamily("Consolas");
        return tb;
    }

    private static Grid TwoColumnGrid(double firstColumnMax, string sharedGroup)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = firstColumnMax, SharedSizeGroup = sharedGroup });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static void AddRow(Grid grid, UIElement first, UIElement second)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(first, row); Grid.SetColumn(first, 0);
        Grid.SetRow(second, row); Grid.SetColumn(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
    }

    /// <summary>(Re)builds the reference part of the Help page; called again on a language change.</summary>
    private void BuildReferenceHelp()
    {
        if (_helpReferencePanel is null)
        {
            _helpReferencePanel = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
            Grid.SetIsSharedSizeScope(_helpReferencePanel, true);   // one option column width across all sections
            HelpPage.Children.Add(_helpReferencePanel);
        }
        var panel = _helpReferencePanel;
        panel.Children.Clear();
        _helpAnchors.Clear();

        panel.Children.Add(HelpText(Loc.HelpAboutTitle, "Mauve", 15, FontWeights.Bold));
        panel.Children.Add(HelpText(Loc.HelpAboutText, "Text", 13, null, new Thickness(0, 8, 0, 0)));
        panel.Children.Add(HelpText(Loc.HelpLegalNote, "Text", 13, FontWeights.SemiBold, new Thickness(0, 8, 0, 0)));

        foreach (var tool in HelpDoc.Tools())
        {
            panel.Children.Add(HelpText(tool.Name, "Text", 13, FontWeights.SemiBold, new Thickness(0, 14, 0, 0)));
            panel.Children.Add(HelpText(tool.Description, "Subtext", 12, null, new Thickness(0, 3, 0, 0)));
        }

        var cliTitle = HelpText(Loc.HelpCommandLineTitle, "Mauve", 15, FontWeights.Bold, new Thickness(0, 26, 0, 0));
        _helpAnchors["options"] = cliTitle;
        panel.Children.Add(cliTitle);
        panel.Children.Add(HelpText(Loc.HelpCommandLineIntro, "Subtext", 12, null, new Thickness(0, 6, 0, 0)));
        foreach (var section in HelpDoc.Options())
        {
            panel.Children.Add(HelpText(section.Title, "Mauve", 12, FontWeights.SemiBold, new Thickness(0, 14, 0, 2)));
            var grid = TwoColumnGrid(260, "HelpOption");
            foreach (var (option, help) in section.Rows)
                AddRow(grid, MonoText(option, "Text", 12, new Thickness(0, 3, 14, 3)),
                    HelpText(help, "Subtext", 12, null, new Thickness(0, 3, 0, 3)));
            panel.Children.Add(grid);
        }

        var examplesTitle = HelpText(Loc.HelpExamplesTitle, "Mauve", 15, FontWeights.Bold, new Thickness(0, 26, 0, 0));
        _helpAnchors["examples"] = examplesTitle;
        panel.Children.Add(examplesTitle);
        foreach (var (command, description) in HelpDoc.Examples())
        {
            panel.Children.Add(HelpText(description, "Text", 13, null, new Thickness(0, 10, 0, 0)));
            panel.Children.Add(MonoText(command, "Subtext", 12, new Thickness(0, 2, 0, 0)));
        }

        var exitTitle = HelpText(Loc.HelpExitCodesTitle, "Mauve", 15, FontWeights.Bold, new Thickness(0, 26, 0, 0));
        _helpAnchors["exit"] = exitTitle;
        panel.Children.Add(exitTitle);
        var codes = TwoColumnGrid(60, "HelpExitCode");
        foreach (var (code, meaning) in HelpDoc.ExitCodes())
            AddRow(codes, MonoText(code.ToString(), "Text", 12, new Thickness(0, 3, 14, 3)),
                HelpText(meaning, "Subtext", 12, null, new Thickness(0, 3, 0, 3)));
        panel.Children.Add(codes);
    }

    /// <summary>Scrolls the Help page so that the named part of the reference starts near the top.</summary>
    internal void ReviewScrollToReference(string part = "")
    {
        UpdateLayout();
        if (!_helpAnchors.TryGetValue(part, out var anchor))
        {
            _helpReferencePanel?.BringIntoView();
            return;
        }
        for (DependencyObject? p = anchor; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + anchor.TranslatePoint(new Point(0, 0), sv).Y - 10);
            break;
        }
    }
}
