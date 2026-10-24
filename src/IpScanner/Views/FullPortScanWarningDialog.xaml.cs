using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

public partial class FullPortScanWarningDialog : Window
{
    public FullPortScanWarningDialog(bool outsideNetworks, int ports = 65535)
    {
        InitializeComponent();
        BodyText.Text = Loc.FullPortScanWarningBody(ports);
        OutsideNote.Visibility = outsideNetworks ? Visibility.Visible : Visibility.Collapsed;
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ContentRendered += (_, _) => CancelBtn.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => this.CloseWith(true);
    private void OnCancel(object sender, RoutedEventArgs e) => this.CloseWith(false);
}
