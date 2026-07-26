using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace Rove.Ui;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // An unhandled exception on a pool thread terminates the process with
        // nothing but a core dump. Logging first turns "it vanished" into
        // something diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[rove] FATAL: {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[rove] unobserved: {e.Exception}");
            e.SetObserved();
        };

        Run(args);
    }

    private static void Run(string[] args)
    {
        // Two instances would each restore the same session file and each write
        // back to it, which looks exactly like tabs duplicating themselves.
        using var mutex = new Mutex(true, "rove-single-instance", out var isFirst);
        if (!isFirst) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
