using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace IpScanner.Tests;

/// <summary>One shared STA thread with a WPF dispatcher and an Application carrying the app's
/// theme and styles, so tests can create real controls. All UI work runs through
/// <see cref="RunAsync"/>.</summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is not null) return _dispatcher;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                // Relative pack URIs in the app's XAML resolve against the resource assembly.
                typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(null, typeof(MainWindow).Assembly);
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "Theme.Dark", "Styles", "Icons" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/IP-Scanner;component/Resources/{name}.xaml", UriKind.Absolute)
                    });
                _dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_dispatcher));
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "WpfTestHost" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return _dispatcher!;
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = EnsureStarted();
        return await await dispatcher.InvokeAsync(work);
    }

    public static Task RunAsync(Func<Task> work) => RunAsync(async () => { await work(); return 0; });
}
