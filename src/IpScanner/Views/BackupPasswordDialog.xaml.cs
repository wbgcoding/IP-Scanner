using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>
/// Password prompt of the full backup. Set mode asks twice (empty means no password), ask mode asks once
/// for the password of an existing encrypted backup.
/// </summary>
public partial class BackupPasswordDialog : Window
{
    private readonly bool _set;

    /// <param name="set">True while creating a backup, false while opening one.</param>
    /// <param name="wrongPassword">Shows the "wrong password" notice above the buttons (ask mode).</param>
    public BackupPasswordDialog(bool set, bool wrongPassword = false)
    {
        _set = set;
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        InitializeComponent();
        var title = set ? Loc.BackupPasswordSetTitle : Loc.BackupPasswordTitle;
        TitleText.Text = title;
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        Body.Text = set ? Loc.BackupPasswordSetHint : Loc.BackupPasswordAskHint;
        RepeatPanel.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        First.ToolTip = set ? Loc.TipBackupPwSet : Loc.TipBackupPwAsk;
        ConfirmBtn.ToolTip = set ? Loc.TipBackupConfirmSet : Loc.TipBackupConfirmAsk;
        if (wrongPassword) ShowError(Loc.BackupPasswordWrong);
        ContentRendered += (_, _) => First.Focus();
    }

    /// <summary>The entered password; null when the field was left empty.</summary>
    public string? Password => First.Password.Length == 0 ? null : First.Password;

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnCancel(object sender, RoutedEventArgs e) => this.CloseWith(false);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_set && First.Password != Second.Password)
        {
            ShowError(Loc.BackupPasswordMismatch);
            return;
        }
        if (!_set && First.Password.Length == 0)
        {
            ShowError(Loc.BackupPasswordEmpty);
            return;
        }
        this.CloseWith(true);
    }
}
