using System;
using System.Threading;
using Avalonia;

namespace Rove.Ui;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
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
