using System;
using System.IO;
using Avalonia;
using Rove.Ui.Session;

namespace Rove.Ui;

internal sealed class Program
{
    // Held for the process lifetime and never disposed — the kernel releases
    // the lock on exit, including on kill -9.
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        // Two instances would each restore the same session file and each write
        // back to it, which looks exactly like tabs duplicating themselves.
        if (!TryAcquireSingleInstance())
        {
            Console.Error.WriteLine("rove is already running.");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            var dir = JsonSessionStore.DefaultDirectory();
            Directory.CreateDirectory(dir);

            // FileShare.None maps to an exclusive flock on Unix, so a second
            // process fails here instead of silently sharing the session file.
            _instanceLock = new FileStream(
                Path.Combine(dir, "instance.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
