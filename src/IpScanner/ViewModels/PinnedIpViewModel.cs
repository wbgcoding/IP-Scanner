namespace IpScanner.ViewModels;

/// <summary>One "always show at the top" pinned IP, as configured (not tied to
/// whether the device currently appears in a scan).</summary>
public sealed class PinnedIpViewModel : ObservableObject, IDeletable
{
    public PinnedIpViewModel(string ip, string name, string color)
    {
        Ip = ip; Name = name; Color = color;
    }

    public string Ip    { get; }
    public string Name  { get; }
    public string Color { get; }
    public string DisplayName => Name.Length > 0 ? Name : Ip;
    public bool HasCustomName => Name.Length > 0;
    public string SwatchColor => Color.Length > 0 ? Color : Core.Palette.Surface2;
    /// <summary>Pushes a re-read of SwatchColor - needed only when it falls back to
    /// the theme-dependent default (no custom color set) and the theme just changed.</summary>
    public void RefreshTheme() => Raise(nameof(SwatchColor));

    private bool _deleteArmed;
    public bool IsDeleteArmed
    {
        get => _deleteArmed;
        set { if (_deleteArmed != value) { _deleteArmed = value; Raise(nameof(IsDeleteArmed)); } }
    }

    /// <summary>True when this entry is a hostname that could not be
    /// resolved during the last scan start. Never true for a plain IP entry.</summary>
    private bool _isUnresolved;
    public bool IsUnresolved
    {
        get => _isUnresolved;
        set { if (_isUnresolved != value) { _isUnresolved = value; Raise(nameof(IsUnresolved)); } }
    }
}
