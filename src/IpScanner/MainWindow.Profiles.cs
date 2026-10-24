using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;

namespace IpScanner;

public partial class MainWindow : Window
{
    // Named profiles, each with its own Subnets/PinnedIps/InternetHosts.
    // The default (unnamed, ActiveProfile == "") profile's lists ARE _config's own
    // fields - no separate storage, no migration for existing users. Every other
    // profile's lists live in the sealed settings (ProfileStore), keyed by name; the
    // default profile's data also gets mirrored there under the "" key purely as
    // a stash while some OTHER profile is active (see SwitchProfile).
    private Dictionary<string, ProfileData>? _profilesCache;
    private bool _editingNewProfile;

    private string ProfilesConfigDir => Core.Models.ScanConfig.ResolvePath(
        string.IsNullOrWhiteSpace(_config.ConfigDirectory)
            ? Core.Models.ScanConfig.DefaultConfigDirectory : _config.ConfigDirectory);

    /// <summary>Folder of the sealed settings (next to the database), where the profiles are stored.</summary>
    private string ProfilesDataDir => SealedHome;

    private LoadProblem _profilesLoadProblem;

    private Dictionary<string, ProfileData> Profiles
    {
        get
        {
            if (_profilesCache is null)
            {
                _profilesCache = ProfileStore.Load(ProfilesConfigDir, ProfilesDataDir, out var problem);
                _profilesLoadProblem = problem;
            }
            return _profilesCache;
        }
    }

    /// <summary>Shows one notice for the most serious problem found while loading the
    /// persisted data files at startup (key unavailable first, then a restored backup).</summary>
    private void ShowStartupLoadProblems()
    {
        _ = Profiles;   // loads the profiles now so a damaged file is reported at startup
        var problems = new[] { _vm.OverridesLoadProblem, _profilesLoadProblem, StartupSealedProblem };
        var worst = problems.Contains(LoadProblem.KeyUnavailable) ? LoadProblem.KeyUnavailable
            : problems.Contains(LoadProblem.NewerFormat) ? LoadProblem.NewerFormat
            : problems.Contains(LoadProblem.RestoredFromBackup) ? LoadProblem.RestoredFromBackup
            : problems.Contains(LoadProblem.Unreadable) ? LoadProblem.Unreadable
            : LoadProblem.None;
        ShowLoadProblem(worst);
    }

    /// <summary>Rebuilds the profile combo (default + named), preserving the
    /// active selection - called at startup, after language switch and after any
    /// add/rename/delete.</summary>
    private void SetProfileItems()
    {
        ProfileBox.ItemsSource = new[] { Loc.DefaultProfileName }.Concat(_config.ProfileNames).ToArray();
        int idx = _config.ActiveProfile.Length == 0 ? 0 : _config.ProfileNames.IndexOf(_config.ActiveProfile) + 1;
        ProfileBox.SelectedIndex = idx >= 0 ? idx : 0;
        UpdateProfileRow();
    }

    /// <summary>With only the default profile the row shows nothing but the add button; the switcher
    /// and the rename button appear once a named profile exists.</summary>
    private void UpdateProfileRow()
    {
        int named = _config.ProfileNames.Count;
        var shown = ViewModels.ProfileRowState.SwitcherVisible(named) ? Visibility.Visible : Visibility.Collapsed;
        ProfileLabel.Visibility = shown;
        ProfileBox.Visibility = shown;
        ProfileEditBtn.Visibility = ViewModels.ProfileRowState.EditButtonVisible(named, ProfileBox.SelectedIndex)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ProfileBox.SelectedIndex < 0) return;
        var name = ProfileBox.SelectedIndex == 0 ? "" : _config.ProfileNames[ProfileBox.SelectedIndex - 1];
        UpdateProfileRow();
        if (name == _config.ActiveProfile) return;
        if (!SwitchProfile(name)) ResetProfileItems();
    }

    /// <summary>Stashes the outgoing profile's live lists, loads the incoming
    /// profile's lists (empty for a brand-new one), and refreshes every view that
    /// depends on Subnets/PinnedIps/InternetHosts. Returns false (nothing changed, a notice is
    /// shown) when the profile data could not be stored.</summary>
    private bool SwitchProfile(string name)
    {
        if (_restoreGate.Active) return false;   // the profile files are about to be replaced
        if (!ProfileStore.TrySwitch(_config, Profiles, name, () => ProfileStore.Save(ProfilesDataDir, Profiles)))
        {
            ShowNotice(Loc.NoticeProfileNotSaved, null, null);
            return false;
        }
        _vm.StopAndClear();   // a scan of the old profile ends and its rows go before the new lists apply
        _vm.Config = _config;
        ApplySnmpTrust();
        _vm.RefreshSubnetMeta();
        _vm.RefreshPinnedNames();
        Sidebar.Refresh(ViewModels.EntryKind.Host);
        PersistConfigDebounced();
        return true;
    }

    /// <summary>Puts the profile combo back on the active profile without raising a switch.</summary>
    private void ResetProfileItems()
    {
        _loadingSettings = true;
        try { SetProfileItems(); }
        finally { _loadingSettings = false; }
    }

    private void OnProfileAddClick(object sender, RoutedEventArgs e)
    {
        _editingNewProfile = true;
        ProfileEditTitle.Text = Loc.ProfileAddTitle;
        ProfileEditNameBox.Text = "";
        ProfileEditError.Visibility = Visibility.Collapsed;
        ProfileEditDeleteBtn.Visibility = Visibility.Collapsed;
        CloseOtherEditPopups(ProfileEditPopup);
        ProfileEditPopup.PlacementTarget = ProfileAddBtn;
        PlaceAvoidingBottomEdge(ProfileEditPopup, ProfileAddBtn);
        ProfileEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => ProfileEditNameBox.Focus());
    }

    private void OnProfileEditClick(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedIndex <= 0) return;   // default profile has no rename/delete
        _editingNewProfile = false;
        ProfileEditTitle.Text = Loc.ProfileRenameTitle;
        ProfileEditNameBox.Text = _config.ProfileNames[ProfileBox.SelectedIndex - 1];
        ProfileEditError.Visibility = Visibility.Collapsed;
        ProfileEditDeleteBtn.Visibility = Visibility.Visible;
        CloseOtherEditPopups(ProfileEditPopup);
        ProfileEditPopup.PlacementTarget = ProfileEditBtn;
        PlaceAvoidingBottomEdge(ProfileEditPopup, ProfileEditBtn);
        ProfileEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            ProfileEditNameBox.Focus();
            ProfileEditNameBox.SelectAll();
        });
    }

    private void OnProfileEditKey(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) OnProfileEditSave(sender, e); }

    /// <summary>Esc closes the popup without saving from any focused control inside it.</summary>
    private void OnProfileEditPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        ProfileEditPopup.IsOpen = false;
        e.Handled = true;
    }

    private void OnProfileEditCancel(object sender, RoutedEventArgs e) => ProfileEditPopup.IsOpen = false;

    private void OnProfileEditSave(object sender, RoutedEventArgs e)
    {
        var name = ProfileEditNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ProfileEditError.Text = Loc.ProfileNameRequired;
            ProfileEditError.Visibility = Visibility.Visible;
            return;
        }
        var oldName = _editingNewProfile ? null : _config.ProfileNames[ProfileBox.SelectedIndex - 1];
        var others = _config.ProfileNames.Where(n => !string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase))
            .Append(Loc.DefaultProfileName);
        if (ProfileNameRule.Validate(name, others) is { } nameError)
        {
            ProfileEditError.Text = nameError;
            ProfileEditError.Visibility = Visibility.Visible;
            return;
        }
        if (_editingNewProfile)
        {
            _config.ProfileNames = _config.ProfileNames.Append(name).ToList();
            // SetProfileItems/SelectedIndex would otherwise fire OnProfileChanged with
            // _config.ActiveProfile still pointing at the OLD profile, triggering a
            // redundant extra SwitchProfile before the explicit one below runs.
            _loadingSettings = true;
            try
            {
                SetProfileItems();
                ProfileBox.SelectedIndex = _config.ProfileNames.Count;   // 0 = default, so Count = the new one
                UpdateProfileRow();   // the guarded selection change above skips OnProfileChanged
            }
            finally { _loadingSettings = false; }
            if (!SwitchProfile(name))
            {
                _config.ProfileNames = _config.ProfileNames.Where(n => n != name).ToList();
                ResetProfileItems();
            }
        }
        else if (oldName != name)
        {
            var profiles = Profiles;
            if (!ProfileStore.TryEdit(profiles,
                    () => { if (profiles.Remove(oldName!, out var data)) profiles[name] = data; },
                    () => ProfileStore.Save(ProfilesDataDir, profiles)))
            {
                ShowNotice(Loc.NoticeProfileNotSaved, null, null);
                ProfileEditPopup.IsOpen = false;
                return;
            }
            var names = _config.ProfileNames.ToList();
            names[names.IndexOf(oldName!)] = name;
            _config.ProfileNames = names;
            if (_config.ActiveProfile == oldName) _config.ActiveProfile = name;
            PersistConfigDebounced();
            SetProfileItems();
        }
        ProfileEditPopup.IsOpen = false;
    }

    private void OnProfileEditDelete(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedIndex <= 0) return;
        var name = _config.ProfileNames[ProfileBox.SelectedIndex - 1];
        if (_config.ActiveProfile == name && !SwitchProfile(""))   // never delete the active profile out from under the UI
        {
            ResetProfileItems();
            ProfileEditPopup.IsOpen = false;
            return;
        }
        var profiles = Profiles;
        if (!ProfileStore.TryEdit(profiles, () => profiles.Remove(name), () => ProfileStore.Save(ProfilesDataDir, profiles)))
        {
            ShowNotice(Loc.NoticeProfileNotSaved, null, null);
            ProfileEditPopup.IsOpen = false;
            return;
        }
        _config.ProfileNames = _config.ProfileNames.Where(n => n != name).ToList();
        PersistConfigDebounced();
        SetProfileItems();
        ProfileEditPopup.IsOpen = false;
    }
}
