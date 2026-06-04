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
            MessageBox.Show(args.Exception.Message, Loc.ScanError,
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            args.SetObserved();
    }
}
