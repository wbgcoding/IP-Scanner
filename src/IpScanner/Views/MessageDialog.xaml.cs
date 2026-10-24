using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

public enum MessageDialogButtons { Ok, YesNo }

public enum MessageDialogKind { Info, Warning, Error, Question }

public enum MessageDialogResult { Ok, Yes, No }

/// <summary>Themed replacement for the system message box: a severity icon, a title, a wrapped
/// body and an OK or Yes/No button row. Works without an owner, so it can also report a failure
/// before the main window exists.</summary>
public partial class MessageDialog : Window
{
    private readonly MessageDialogButtons _buttons;
    private MessageDialogResult _result;

    internal MessageDialog(string body, string title, MessageDialogButtons buttons, MessageDialogKind kind)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        // Callers often pass the menu label that opened the action; its trailing ellipsis is no title.
        title = CleanTitle(title);
        Title = title;
        AutomationProperties.SetName(this, title);
        TitleText.Text = title;
        Body.Text = body;
        _buttons = buttons;
        _result = CancelResult(buttons);

        IconFrame.Data = Geometry.Parse(IconFrameData(kind));
        IconGlyph.Data = Geometry.Parse(IconGlyphData(kind));
        IconFrame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, IconBrushKey(kind));
        IconGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, IconBrushKey(kind));

        if (buttons == MessageDialogButtons.Ok)
        {
            ButtonRow.Children.Remove(NoBtn);
            YesBtn.Content = Loc.Ok;
            YesBtn.ToolTip = Loc.TipDialogOk;
            YesBtn.IsDefault = true;
            YesBtn.IsCancel = true;
            YesBtn.Margin = new Thickness(0);
            AutomationProperties.SetName(YesBtn, Loc.Ok);
        }
        else
        {
            YesBtn.Content = Loc.Yes;
            YesBtn.ToolTip = Loc.TipMessageYes;
            NoBtn.Content = Loc.No;
            NoBtn.ToolTip = Loc.TipMessageNo;
            NoBtn.IsDefault = true;
            NoBtn.IsCancel = true;
            AutomationProperties.SetName(YesBtn, Loc.Yes);
            AutomationProperties.SetName(NoBtn, Loc.No);
        }
    }

    /// <summary>Shows the dialog modally and returns the pressed button. Esc and the window close
    /// answer No for Yes/No and OK for a plain notice; Enter presses No on Yes/No, so a stray key
    /// never confirms.</summary>
    public static MessageDialogResult Show(Window? owner, string body, string title,
        MessageDialogButtons buttons = MessageDialogButtons.Ok, MessageDialogKind kind = MessageDialogKind.Info)
    {
        var dlg = new MessageDialog(body, title, buttons, kind);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>Answer for Esc or closing the window.</summary>
    internal static MessageDialogResult CancelResult(MessageDialogButtons buttons)
        => buttons == MessageDialogButtons.YesNo ? MessageDialogResult.No : MessageDialogResult.Ok;

    internal static string CleanTitle(string title) => title.TrimEnd().TrimEnd('…', '.').TrimEnd();

    /// <summary>Theme brush resource that colors the icon of a severity.</summary>
    internal static string IconBrushKey(MessageDialogKind kind) => kind switch
    {
        MessageDialogKind.Warning => "Yellow",
        MessageDialogKind.Error => "Red",
        MessageDialogKind.Question => "Mauve",
        _ => "Blue",
    };

    // Icon geometry on a 32x32 grid: an outline (circle, or triangle for a warning) plus a glyph;
    // a zero-length segment with round caps draws a dot.
    internal static string IconFrameData(MessageDialogKind kind) => kind == MessageDialogKind.Warning
        ? "M16,4 L29,27 L3,27 Z"
        : "M16,3 A13,13 0 1 1 15.99,3 Z";

    internal static string IconGlyphData(MessageDialogKind kind) => kind switch
    {
        MessageDialogKind.Warning => "M16,12 L16,19 M16,23.2 L16,23.3",
        MessageDialogKind.Error => "M11,11 L21,21 M21,11 L11,21",
        MessageDialogKind.Question => "M12.3,12.6 A3.9,3.9 0 1 1 17.2,16.4 Q16,17.2 16,19 M16,23 L16,23.1",
        _ => "M16,14 L16,23 M16,9.4 L16,9.5",
    };

    private void OnYes(object sender, RoutedEventArgs e)
    {
        _result = _buttons == MessageDialogButtons.Ok ? MessageDialogResult.Ok : MessageDialogResult.Yes;
        this.CloseWith(true);
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _result = MessageDialogResult.No;
        this.CloseWith(false);
    }
}
