using System.Windows;
using IpScanner.Core.Localization;

namespace IpScanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Never let a stray exception close the app silently (e.g. a race
        // while stopping a scan) — surface it and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            var ex = args.Exception;
            while (ex.InnerException is { } inner) ex = inner;   // root cause
            try
            {
                System.IO.File.AppendAllText("error.log",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
            }
            catch { /* log is best-effort */ }
            MessageBox.Show(ex.Message, Loc.ScanError,
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            args.SetObserved();
    }
}
