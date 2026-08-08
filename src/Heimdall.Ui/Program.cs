using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.X11;

namespace Heimdall.Ui;

internal sealed class Program
{
    /// <summary>
    /// The build, and the file it is running from.
    ///
    /// **This exists because a stale `~/.local` install shadowed an RPM for
    /// three days** and every diagnosis went looking at the code instead. rpm
    /// answers "what is installed"; only the running process can answer "what is
    /// running", and until now it could not answer either.
    ///
    /// `GetName().Version` rather than reflecting over assembly attributes:
    /// AssemblyName metadata survives trimming and NativeAOT, which is how this
    /// ships.
    /// </summary>
    /// <summary>
    /// `GetName().Version` rather than reflecting over assembly attributes:
    /// AssemblyName metadata survives trimming and NativeAOT, which is how this
    /// ships. Split out from <see cref="Describe"/> so the settings dialog shows
    /// the same number by the same route — two ways of asking the version is
    /// how you end up with a window and a `--version` that disagree.
    /// </summary>
    internal static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>
    /// The file this process is actually running from. **The reason the version
    /// alone is not enough**: a stale `~/.local` install shadowed an RPM for
    /// three days, and both copies reported plausible versions.
    /// </summary>
    internal static string RunningFrom => Environment.ProcessPath ?? "(unknown path)";

    private static string Describe() => $"heimdall {Version}\n{RunningFrom}";

    /// <summary>
    /// The name the Windows installer watches for, so it can tell that Heimdall
    /// is running before it starts replacing a 28 MB executable underneath it.
    ///
    /// **Must match `AppMutex` in packaging/heimdall.iss**, and a test asserts
    /// that it does — the failure mode otherwise is silent in the worst way: the
    /// installer simply stops noticing, which looks exactly like everything
    /// working until someone upgrades with the app open.
    ///
    /// Session-local rather than `Global\`, deliberately. Creating a global
    /// mutex needs SeCreateGlobalPrivilege, which an ordinary user does not
    /// have, so a per-user install — the default here — could never create one.
    /// Local names are per-SESSION, not per-token, so an elevated setup started
    /// from the same desktop still sees it, which covers the "install for all
    /// users" path too.
    /// </summary>
    public const string InstanceMutexName = "Heimdall.Ui.Running";

    /// <summary>
    /// Held for the life of the process. Static so nothing collects it — a
    /// local would be eligible the moment Main stopped using it, and the mutex
    /// would quietly disappear while the window was still open.
    /// </summary>
    private static Mutex? _instanceMutex;

    /// <summary>
    /// **Not single-instance.** The mutex is a flag for the installer to read,
    /// not a lock: a second copy is welcome to start and simply opens the
    /// existing mutex instead. Refusing to launch would be a behaviour change
    /// nobody asked for, smuggled in as packaging.
    /// </summary>
    private static void ClaimInstanceMutex()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName);
        }
        catch (Exception ex)
        {
            // Nothing here is worth failing to start over. The cost of losing it
            // is an installer that cannot detect a running copy — which is
            // exactly where things were before this existed.
            Console.Error.WriteLine($"[heimdall] instance mutex unavailable: {ex.Message}");
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Answered BEFORE anything else — no window, no settings, no theme. It
        // has to work with no display attached, because CI is exactly where a
        // binary that cannot start needs to say so.
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine(Describe());
            return;
        }

        // After --version, which must stay free of side effects: it runs in CI
        // and on machines with no display, and claiming a mutex to print a
        // string would be work done for nobody.
        ClaimInstanceMutex();

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
