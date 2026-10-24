namespace IpScanner.ViewModels;

/// <summary>Which parts of the profile row show for a given profile list and selection.</summary>
internal static class ProfileRowState
{
    /// <summary>The switcher appears once a named profile exists.</summary>
    internal static bool SwitcherVisible(int namedProfiles) => namedProfiles > 0;

    /// <summary>Rename/delete applies to named profiles only; index 0 is the default profile.</summary>
    internal static bool EditButtonVisible(int namedProfiles, int selectedIndex) => namedProfiles > 0 && selectedIndex > 0;
}
