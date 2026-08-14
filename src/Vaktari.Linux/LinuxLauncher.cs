using System.Diagnostics;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

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

    /// <summary>
    /// The terminals on PATH, the user's choice first — the same list the old
    /// fall-through chain walked, now offered rather than guessed at.
    ///
    /// $TERMINAL still comes first when it is set: it is the desktop's own way
    /// of saying which terminal to use, and a setting inside one application
    /// has no business overruling it.
    /// </summary>
    /// Detection only: the user's preference is applied above this, because
    /// settings live in the UI assembly and this one sits underneath it.
    public IReadOnlyList<TerminalOption> Terminals => _terminals ??= Detect();

    private IReadOnlyList<TerminalOption>? _terminals;

    /// <summary>
    /// Probed once. This is read while a context menu is built, on the UI
    /// thread, and a PATH walk per candidate is what makes a menu feel slow.
    /// </summary>
    private static IReadOnlyList<TerminalOption> Detect()
    {
        var found = new List<TerminalOption>();

        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } wanted
            && OnPath(wanted) is { } path)
            found.Add(new TerminalOption("terminal-env", wanted, path, ["--workdir", "{dir}"]));

        foreach (var (id, name, exe, args) in Known)
        {
            if (found.Any(t => t.Command.EndsWith("/" + exe, StringComparison.Ordinal))) continue;
            if (OnPath(exe) is not { } located) continue;

            found.Add(new TerminalOption(id, name, located, args));
        }

        return found;
    }

    private static readonly (string Id, string Name, string Exe, string[] Args)[] Known =
    [
        ("konsole",        "Konsole",        "konsole",        ["--workdir", "{dir}"]),
        ("gnome-terminal", "GNOME Terminal", "gnome-terminal", ["--working-directory", "{dir}"]),
        ("alacritty",      "Alacritty",      "alacritty",      ["--working-directory", "{dir}"]),
        ("kitty",          "kitty",          "kitty",          ["--directory", "{dir}"]),
        ("wezterm",        "WezTerm",        "wezterm",        ["start", "--cwd", "{dir}"]),
        ("foot",           "foot",           "foot",           ["--working-directory={dir}"]),
        ("xfce4-terminal", "Xfce Terminal",  "xfce4-terminal", ["--working-directory={dir}"]),
        ("xterm",          "xterm",          "xterm",          []),
    ];

    private static string? OnPath(string exe)
    {
        if (exe.Contains('/')) return File.Exists(exe) ? exe : null;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (dir.Length == 0) continue;

            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void OpenTerminal(string directory)
    {
        if (Terminals.FirstOrDefault() is { } preferred)
        {
            OpenTerminal(directory, preferred);
            return;
        }

        // Nothing detected. xterm without a working-directory flag still lands
        // in the right place through the shell.
        TrySpawn("xterm", "-e", "cd " + directory + " && $SHELL");
    }

    public void OpenTerminal(string directory, TerminalOption terminal)
    {
        var args = terminal.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        if (terminal.UsesWorkingDirectory)
        {
            if (TrySpawnIn(directory, terminal.Command)) return;
        }
        else if (TrySpawn(terminal.Command, args))
        {
            return;
        }

        OpenTerminal(directory);
    }

    private static bool TrySpawnIn(string directory, string exe)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };

            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
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
