using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.X11;

namespace Heimdall.Ui;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // An unhandled exception on a pool thread terminates the process with
        // nothing but a core dump. Logging first turns "it vanished" into
        // something diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[heimdall] FATAL: {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[heimdall] unobserved: {e.Exception}");
            e.SetObserved();
        };

        Run(args);
    }

    /// <summary>Folders named on the command line, for the window to open.</summary>
    public static string[] StartupPaths { get; private set; } = [];

    /// <summary>The live channel, so the window can listen for later launches.</summary>
    public static SingleInstance? Instance { get; private set; }

    private static void Run(string[] args)
    {
        var paths = args.Where(a => !a.StartsWith('-')).ToArray();

        var instance = new SingleInstance();

        // Two instances would each restore the same session file and each write
        // back to it, which looks exactly like tabs duplicating themselves —
        // and as the desktop's default file manager, every "open folder" would
        // start another copy.
        if (!instance.TryAcquire())
        {
            // Hand the folders over and get out of the way immediately. A
            // caller that waits for its file manager to exit must not wait on a
            // window that never closes.
            instance.Dispose();

            // Said out loud. Refusing silently with exit code 0 is
            // indistinguishable from crashing on startup — which cost a
            // diagnostic round trip when the published binary "did nothing"
            // and the real answer was that a copy was already running.
            Console.Error.WriteLine(
                paths.Length > 0
                    ? $"[heimdall] already running — handed over {paths.Length} path(s)"
                    : "[heimdall] already running — raising the existing window");

            SingleInstance.TryForward(paths);
            return;
        }

        Instance = instance;
        StartupPaths = paths;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()

            // WM_CLASS is how the desktop matches a running window back to its
            // .desktop file. Avalonia's default derives from the assembly name
            // — "Heimdall.Ui" — while the entry is "heimdall.desktop", so the
            // panel could not associate the two and showed a placeholder icon
            // until the window published its own embedded one.
            //
            // Setting it here rather than adding StartupWMClass= to the desktop
            // entry, because there are TWO desktop entries (brand/ and
            // packaging/) and this fixes both, plus any distro package that
            // writes its own.
            .With(new X11PlatformOptions { WmClass = "heimdall" })

            .WithInterFont()
            .LogToTrace();
}
