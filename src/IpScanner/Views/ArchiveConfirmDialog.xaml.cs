using System.Windows;

namespace IpScanner.Views;

public partial class ArchiveConfirmDialog : Window
{
    /// <param name="checkboxLabel">Shows an extra option below the message when set.</param>
    /// <param name="title">Replaces the default "change archive" heading.</param>
    public ArchiveConfirmDialog(string message, string? checkboxLabel = null, bool checkboxDefault = false,
                                string? title = null)
    {
        InitializeComponent();
        Body.Text = message;
        if (title is not null)
        {
            TitleText.Text = title;
            System.Windows.Automation.AutomationProperties.SetName(this, title);
        }
        if (checkboxLabel is not null)
        {
            Option.Content = checkboxLabel;
            Option.IsChecked = checkboxDefault;
            Option.Visibility = Visibility.Visible;
        }
    }

    /// <summary>State of the optional checkbox; false when the dialog has none.</summary>
    public bool IsChecked => Option.Visibility == Visibility.Visible && Option.IsChecked == true;

    private void OnCancel(object sender, RoutedEventArgs e) => this.CloseWith(false);
    private void OnConfirm(object sender, RoutedEventArgs e) => this.CloseWith(true);
}
