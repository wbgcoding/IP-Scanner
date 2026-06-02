using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IpScanner.Views;

/// <summary>Makes the native window title bar (the row with the min/max/close
/// buttons) dark via the DWM immersive-dark-mode attribute, so it matches the
/// app's dark theme instead of the default light Windows chrome.</summary>
internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;        // Win10 2004+
    private const int DwmwaUseImmersiveDarkModeOld = 19;     // earlier Win10 builds

    public static void ApplyDark(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }
}
