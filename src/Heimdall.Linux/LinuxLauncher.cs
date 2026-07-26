using System.Diagnostics;
using Heimdall.Core.FileSystem;

namespace Heimdall.Linux;

public sealed class LinuxLauncher : IApplicationLauncher
{
    public void Open(string path) => Spawn("xdg-open", path);

    public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        => DesktopEntries.ForFile(path);

    public void OpenWith(string path, LaunchOption option)
    {
        // Fall back to the default handler rather than doing nothing if the
        // desktop file has gone missing since the menu was built.
        if (!DesktopEntries.Launch(option.Id, path))
            Open(path);
    }

    public void OpenTerminal(string directory)
    {
        // $TERMINAL first so the user's own choice always wins.
        var preferred = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrWhiteSpace(preferred) && TrySpawn(preferred, "--workdir", directory))
            return;

        foreach (var (exe, args) in new (string, string[])[]
        {
            ("konsole", ["--workdir", directory]),
            ("gnome-terminal", ["--working-directory", directory]),
            ("alacritty", ["--working-directory", directory]),
            ("kitty", ["--directory", directory]),
            ("xterm", ["-e", "cd " + directory + " && $SHELL"]),
        })
        {
            if (TrySpawn(exe, args)) return;
        }
    }

    private static void Spawn(string exe, params string[] args) => TrySpawn(exe, args);

    private static bool TrySpawn(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            // Detached: the file manager closing must not take the opened
            // application with it.
            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
