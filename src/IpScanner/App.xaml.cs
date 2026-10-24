using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;

namespace IpScanner;

public partial class App : Application
{
    /// <summary>Config loaded once at startup (theme needs it before the window); reused by MainWindow.</summary>
    internal static ScanConfig? BootConfig { get; private set; }

    // Crash log lives next to the exe (not the process's current directory,
    // which varies with how the app was launched), capped so a repeating
    // error can't grow it without bound.
    internal static string ErrorLogPath => IpScanner.Core.Log.FilePath;

    private static readonly IpScanner.Core.CrashBurstGate FailureBurst = new();

    private Mutex? _singleInstanceMutex;

#if DEBUG
    internal static void ReviewSetBootConfig(ScanConfig config) => BootConfig = config;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        if (Views.Review.ReviewTool.TryStart(this, e.Args)) return;
#endif

        // Only one instance at a time - a second launch just brings the
        // existing window forward instead of opening a second config/DB
        // writer that would fight the first over the same files.
        _singleInstanceMutex = new Mutex(true, "Local\\IpScanner-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        // Load config and merge the theme before the main window is created, so
        // every StaticResource brush resolves to the chosen palette.
        BootConfig = IpScanner.MainWindow.LoadPersistedConfig();
        IpScanner.Core.Log.VerboseProvider = () => BootConfig?.VerboseLog == true;
        IpScanner.Core.Scanner.RawTransportProvider.RegisterDriverTransport();
        ThemeManager.Apply(this, BootConfig.Theme);

        // Never let a stray exception close the app silently (e.g. a race
        // while stopping a scan) - surface it and keep running, but if the
        // same failure keeps firing back-to-back, stop popping dialogs (they'd
        // otherwise make the app unusable) and offer to exit instead.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            AppendErrorLog(args.Exception.ToString());
            ShowFailure(args.Exception, owner => ErrorDialog.Show(owner, Loc.UnexpectedError, args.Exception, null));
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppendErrorLog(args.Exception.ToString());
            args.SetObserved();
        };
    }

    /// <summary>Shows a failure dialog unless a burst of failures has already used up the dialogs:
    /// the shared entry for the unhandled-exception handler and the event-handler guard. Must run
    /// on the dispatcher thread.</summary>
    internal static void ShowFailure(Exception failure, Action<Window?> showDialog)
        => ShowFailure(failure, showDialog, FailureBurst, root =>
        {
            var r = MessageDialog.Show(null, Loc.RepeatedErrorsMsg(root.Message), Loc.UnexpectedError,
                MessageDialogButtons.YesNo, MessageDialogKind.Error);
            if (r == MessageDialogResult.Yes) Environment.Exit(1);
        });

    internal static void ShowFailure(Exception failure, Action<Window?> showDialog,
        IpScanner.Core.CrashBurstGate gate, Action<Exception> askToExit)
    {
        var root = failure;
        while (root.InnerException is { } inner) root = inner;
        switch (gate.Register(DateTime.UtcNow))
        {
            case IpScanner.Core.FailureDialogAction.Suppressed: return;
            case IpScanner.Core.FailureDialogAction.AskToExit: askToExit(root); return;
            default: showDialog(OwnerWindow()); return;
        }
    }

    // MainWindow may only be read on the UI thread; failures can be reported from any thread.
    private static Window? OwnerWindow()
        => Current is { } app && app.Dispatcher.CheckAccess() ? app.MainWindow : null;

    protected override void OnExit(ExitEventArgs e)
    {
        IpScanner.Core.Scanner.IcmpPinger.Shutdown();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Writes an unexpected failure to the shared log at error level. New code logs through
    /// <see cref="IpScanner.Core.Log"/> with an area; this stays for the crash handlers and the
    /// call sites that only have free text.</summary>
    internal static void AppendErrorLog(string text)
        => IpScanner.Core.Log.Error(IpScanner.Core.LogArea.App, text);

    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id);
            if (other?.MainWindowHandle is { } h && h != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(h);
            }
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Showing the existing window failed", ex); /* best-effort - worst case the user just sees no window pop up */ }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
