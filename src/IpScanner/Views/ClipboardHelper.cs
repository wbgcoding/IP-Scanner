using System.Windows;

namespace IpScanner.Views;

/// <summary>The one place that writes text to the clipboard. Another process can hold the
/// clipboard open, so a failed write is reported through the return value instead of thrown.</summary>
internal static class ClipboardHelper
{
    /// <summary>True when <paramref name="text"/> is on the clipboard afterwards.</summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Ui, "Copying to the clipboard failed", ex);
            return false;
        }
    }
}
