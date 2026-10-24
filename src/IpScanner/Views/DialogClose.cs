using System.Windows;

namespace IpScanner.Views;

internal static class DialogClose
{
    /// <summary>Closes a dialog with a result. <see cref="Window.DialogResult"/> throws when the window was
    /// shown modeless (the surface tool does that), so the window is simply closed in that case.</summary>
    internal static void CloseWith(this Window window, bool result)
    {
        try { window.DialogResult = result; }
        catch (InvalidOperationException) { window.Close(); }
    }
}
