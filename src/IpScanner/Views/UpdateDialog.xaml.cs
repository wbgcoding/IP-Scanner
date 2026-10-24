using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>Themed "update available" dialog (replaces the default MessageBox).</summary>
public partial class UpdateDialog : Window
{
    public UpdateDialog(string newVersion, string currentVersion)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TitleText.Text = Loc.UpdateTitle;
        BodyText.Text = Loc.UpdateBody(newVersion, currentVersion);
        UpdateBtn.Content = Loc.UpdateNow;
        LaterBtn.Content = Loc.UpdateLater;
    }

    private void OnUpdate(object sender, RoutedEventArgs e) => this.CloseWith(true);
    private void OnLater(object sender, RoutedEventArgs e) => this.CloseWith(false);
}
