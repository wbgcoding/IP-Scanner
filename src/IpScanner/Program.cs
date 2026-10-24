using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using IpScanner.Core.Cli;

namespace IpScanner;

/// <summary>Entry point: with arguments the exe runs the command line scanner without any window (it is the
/// one and only program, there is no separate console exe), otherwise it starts the WPF app.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        if (args.Length > 0 && args[0] == "--ipv6-list") return DebugIpv6List.Run(args);
#endif
        if (args.Length > 0 && !IsSurfaceTool(args)) return RunHeadless(args);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static bool IsSurfaceTool(string[] args)
    {
#if DEBUG
        return args[0] is "--render" or "--shot" or "--review" or "--ids";
#else
        return false;
#endif
    }

    private const int AttachParentProcess = -1;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint FileTypeUnknown = 0;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    private static bool Usable(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != FileTypeUnknown;

    /// <summary>Prints into the console the exe was started from. The exe is a window subsystem program, so
    /// it has no console of its own: it attaches to the parent's. A stream that was redirected to a file or
    /// a pipe keeps its handle; without any console and without a redirect the output is dropped (no window
    /// is ever created for it) and only the exit code remains.</summary>
    private static int RunHeadless(string[] args)
    {
        // AttachConsole replaces the standard handles with the console's, so remember redirected ones first.
        var origOut = GetStdHandle(StdOutput);
        var origErr = GetStdHandle(StdError);
        bool attached = AttachConsole(AttachParentProcess);
        if (Usable(origOut)) SetStdHandle(StdOutput, origOut);
        if (Usable(origErr)) SetStdHandle(StdError, origErr);
        var utf8 = new UTF8Encoding(false);
        using var stdout = Open(StdOutput, attached, utf8);
        using var stderr = Open(StdError, attached, utf8);
        Console.SetOut(stdout);
        Console.SetError(stderr);

        using var cts = new CancellationTokenSource();
        // The first Ctrl+C stops the scan cleanly; a second one ends the process at once.
        int presses = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            if (Interlocked.Increment(ref presses) > 1) return;
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        int code = CliApp.Run(args, stdout, stderr, cts.Token, null, "IP-Scanner");
        stdout.Flush();
        stderr.Flush();
        return code;
    }

    private static StreamWriter Open(int which, bool consoleAttached, Encoding encoding)
    {
        if (!Usable(GetStdHandle(which)) && consoleAttached)
        {
            // Not redirected: bind this stream to the attached console.
            const uint genericWrite = 0x40000000, shareReadWrite = 3, openExisting = 3;
            var h = CreateFile("CONOUT$", genericWrite, shareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
            if (h != new IntPtr(-1) && h != IntPtr.Zero) SetStdHandle(which, h);
        }
        Stream stream = Usable(GetStdHandle(which))
            ? (which == StdOutput ? Console.OpenStandardOutput() : Console.OpenStandardError())
            : Stream.Null;
        return new StreamWriter(stream, encoding) { AutoFlush = true };
    }
}
