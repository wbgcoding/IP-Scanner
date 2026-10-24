using System.Windows;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

public partial class ManualExportDialog : Window
{
    public ExportScope Scope => (ExportScope)ScopeBox.SelectedIndex;
    public ExportFormat Format => (ExportFormat)FormatBox.SelectedIndex;

    /// <summary>True when the user asked to probe the ports of devices without a result.</summary>
    public bool ProbeMissingPorts => ProbePortsBox.Visibility == Visibility.Visible && ProbePortsBox.IsChecked == true;

    public ManualExportDialog(ExportScope scope, ExportFormat format, bool offerPortCheck = false)
    {
        InitializeComponent();
        if (offerPortCheck) ProbePortsBox.Visibility = Visibility.Visible;
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ScopeBox.SelectedIndex = (int)scope;
        FormatBox.SelectedIndex = (int)format;
        ContentRendered += (_, _) => ScopeBox.Focus();
    }

    private void OnExport(object sender, RoutedEventArgs e) => this.CloseWith(true);
    private void OnCancel(object sender, RoutedEventArgs e) => this.CloseWith(false);
}
