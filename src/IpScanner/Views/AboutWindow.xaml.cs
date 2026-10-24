using System.Diagnostics;
using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>The "About" window: version, manufacturer, license, icon-set
/// attribution and a link to the project page. The browser only starts once
/// the link is clicked, never on its own.</summary>
public partial class AboutWindow : Window
{
    private const string ProjectPageUrl = "https://github.com/wbgcoding/IP-Scanner";

    /// <summary>The copyright notice from the assembly metadata, so the year lives in one place.</summary>
    internal static string CopyrightLine =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyCopyrightAttribute>(
            typeof(AboutWindow).Assembly)?.Copyright ?? "";

    public AboutWindow()
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        VersionText.Text = $"{Loc.AboutVersionLabel} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        ManufacturerText.Text = $"{Loc.AboutManufacturerLabel}: BGCoding";
        LicenseText.Text = $"{Loc.AboutLicenseLabel}: MIT";
        CopyrightText.Text = CopyrightLine;
        AttributionText.Text = Loc.AboutIconAttribution;
        ProjectPageBtn.Content = Loc.AboutOpenProjectPage;
        OkBtn.Content = Loc.Ok;
    }

    private void OnOpenProjectPage(object sender, RoutedEventArgs e)
    {
        // Fixed literal URL (never user input) - safe to shell-execute directly.
        try { Process.Start(new ProcessStartInfo(ProjectPageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Opening the browser failed", ex); /* no default browser handler configured - nothing more we can do */ }
    }

    private void OnOk(object sender, RoutedEventArgs e) => this.CloseWith(true);
}
