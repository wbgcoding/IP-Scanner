using System.IO;
using System.Windows;
using System.Windows.Input;
using IpScanner.Core.Backup;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;

namespace IpScanner;

// Full backup and restore of everything the app keeps (settings page, Data section).
public partial class MainWindow
{
    /// <summary>The files of the running installation, as they are read and written right now.</summary>
    private BackupLayout CurrentBackupLayout()
    {
        var db = ScanConfig.ResolvePath(_config.DatabasePath);
        var dbDir = Path.GetDirectoryName(db) ?? AppContext.BaseDirectory;
        return new BackupLayout(
            Conf: ActiveConfPath(),
            Database: db,
            Overrides: OverridesPathFor(_config.DatabasePath),
            Key: Path.Combine(dbDir, KnownDevicesDb.KeyFileName),
            Settings: SealedSettings.PathFor(SealedHome),
            Profiles: ProfileStore.PathFor(ProfilesConfigDir));
    }

    private static string AppVersionText => Core.UpdateService.CurrentVersion.ToString(3);

    private async void OnBackupAll(object sender, RoutedEventArgs e)
        => await UiGuard.Run(BackupAllAsync, Loc.BackupFailedTitle);

    private async Task BackupAllAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.BackupFileFilter,
            DefaultExt = "zip",
            FileName = $"IP-Scanner-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;

        var password = new BackupPasswordDialog(set: true) { Owner = this };
        if (password.ShowDialog() != true) return;

        FlushPersistConfig();
        var layout = CurrentBackupLayout();
        var secret = password.Password;
        var target = dlg.FileName;
        try
        {
            await Task.Run(() => BackupService.Create(layout, target, secret, AppVersionText));
            ShowNotice(Loc.BackupSaved(target), null, null);
        }
        catch (BackupException ex) { ShowBackupError(Loc.BackupFailedTitle, ex, target); }
    }

    private async void OnRestoreAll(object sender, RoutedEventArgs e)
        => await UiGuard.Run(RestoreAllAsync, Loc.RestoreFailedTitle);

    private async Task RestoreAllAsync()
    {
        if (_restoreGate.Active) return;
        if (_vm.IsScanning)
        {
            ShowNotice(Loc.RestoreStopScanFirst, null, null);
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.BackupFileFilter };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        var source = dlg.FileName;
        var layout = CurrentBackupLayout();

        BackupService.PreparedRestore? prepared = null;
        var entered = false;
        try
        {
            // The password is asked before anything is touched, and again after a wrong one.
            string? secret = null;
            BackupInfo info;
            var wrong = false;
            while (true)
            {
                info = await Task.Run(() => BackupService.Inspect(source, null));
                if (!info.Encrypted) break;
                var ask = new BackupPasswordDialog(set: false, wrongPassword: wrong) { Owner = this };
                if (ask.ShowDialog() != true) return;
                secret = ask.Password;
                try
                {
                    var typed = secret;
                    info = await Task.Run(() => BackupService.Inspect(source, typed));
                    break;
                }
                catch (BackupException ex) when (ex.Failure == BackupFailure.WrongPassword) { wrong = true; }
            }

            var manifest = info.Manifest!;
            var confirm = new ArchiveConfirmDialog(
                Loc.RestoreConfirm(manifest.CreatedUtc.LocalDateTime, manifest.AppVersion),
                title: Loc.RestoreConfirmTitle) { Owner = this };
            if (confirm.ShowDialog() != true) return;

            // From here on no scan, repeat scan or debounced config write may start; the state is
            // checked again right before the swap, because the staging above takes a while.
            if (!_restoreGate.TryEnter(_vm.IsScanning))
            {
                ShowNotice(Loc.RestoreStopScanFirst, null, null);
                return;
            }
            entered = true;
            // Nothing in the window may write a file or the database until the restore ends: the staging
            // takes a while and the closing notice below pumps messages.
            IsEnabled = false;
            var pw = secret;
            prepared = await Task.Run(() => BackupService.Prepare(source, pw, layout));
            await _restoreGate.DrainAsync();
            if (!_restoreGate.CanCommit(_vm.IsScanning))
            {
                ShowNotice(Loc.RestoreStopScanFirst, null, null);
                return;
            }
            FlushPersistConfig();

            string? safety;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // Synchronous on purpose: no timer or edit may write a file between the swap and the exit.
                safety = prepared.Commit(AppVersionText);
            }
            finally { Mouse.OverrideCursor = null; }

            // The in-memory state is now older than the files, so the normal shutdown (which saves
            // the config again) must not run. The app ends here and has to be started again.
            _closing = true;
            _graphTimer?.Dispose();
            _repeatScanTimer?.Stop();
            _persistDebounce?.Stop();
            DisposeTray();
            MessageDialog.Show(this, Loc.RestoreDone(safety ?? "-"), Loc.RestoreConfirmTitle);
            Environment.Exit(0);
        }
        catch (BackupException ex) { ShowBackupError(Loc.RestoreFailedTitle, ex, source); }
        finally
        {
            prepared?.Dispose();
            if (entered)
            {
                IsEnabled = true;
                _restoreGate.Leave();
                if (_persistPending) PersistConfigDebounced();
            }
        }
    }

    private void ShowBackupError(string title, BackupException ex, string path)
    {
        var cause = ex.Failure switch
        {
            BackupFailure.NotABackup => Loc.BackupNotABackup,
            BackupFailure.NewerFormat => Loc.BackupNewerFormat,
            BackupFailure.PasswordRequired or BackupFailure.WrongPassword => Loc.BackupPasswordWrong,
            BackupFailure.Corrupt => Loc.BackupCorrupt,
            BackupFailure.KeyForeign => Loc.BackupKeyForeign,
            BackupFailure.KeyUnreadable => Loc.BackupKeyUnreadable,
            _ => Loc.BackupIoFailed,
        };
        Core.Log.Warn(Core.LogArea.Db, "Backup or restore failed: " + ex.Failure, ex);
        ErrorDialog.Show(this, title, $"{ex.Message}\n{path}", cause, null);
    }
}
