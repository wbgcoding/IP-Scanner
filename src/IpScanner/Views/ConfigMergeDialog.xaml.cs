using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

public partial class ConfigMergeDialog : Window
{
    public ConfigMergeDialog()
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ContentRendered += (_, _) => CancelBtn.Focus();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => this.CloseWith(true);
    private void OnCancel(object sender, RoutedEventArgs e) => this.CloseWith(false);
}
