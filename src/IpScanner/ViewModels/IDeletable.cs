namespace IpScanner.ViewModels;

/// <summary>Sidebar list entry that supports the arm-then-confirm delete gesture
/// (first click arms it, a second click within a few seconds removes it).</summary>
public interface IDeletable
{
    bool IsDeleteArmed { get; set; }
}
