using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Which terminals this machine actually has.
///
/// **There was no choice at all.** OpenTerminal tried Windows Terminal, then
/// PowerShell, then cmd, and opened whichever started first — so on a machine
/// where somebody lives in Warp, or WSL, or the Git Bash their toolchain needs,
/// "open a terminal here" opened the wrong program and there was nowhere to say
/// otherwise.
///
/// **Found by looking, not by asking the registry.** Terminals are installed
/// every possible way on Windows — a Store package, an MSI under Program Files,
/// a per-user copy under LocalAppData, a winget shim on PATH — and there is no
/// single list of them. Probing a table of known executables is unglamorous and
/// it is also the thing that works.
///
/// The result is cached for the life of the process: it is read while a context
/// menu is being built, on the UI thread, and a dozen file probes there is what
/// makes a menu feel slow to open. Installing a terminal while Vaktari is
/// running is not worth a file watcher.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstalledTerminals
{
    /// <summary>
    /// Ordered by what a Windows user most likely wants when they have several,
    /// which is the same order the old hardcoded chain used, with the terminals
    /// people actually install added around it.
    ///
    /// `{dir}` is replaced with the folder. An entry with no arguments is
    /// started IN the folder instead — cmd and PowerShell have no "start here"
    /// flag and inherit the working directory, while Windows Terminal ignores
    /// the inherited one and needs -d.
    /// </summary>
    private static readonly (string Id, string Name, string Exe, string[] Args)[] Known =
    [
        ("windows-terminal", "Windows Terminal",  "wt.exe",           ["-d", "{dir}"]),
        ("warp",             "Warp",              "warp.exe",         []),
        ("pwsh",             "PowerShell",        "pwsh.exe",         []),
        ("powershell",       "Windows PowerShell","powershell.exe",   []),
        ("cmd",              "Command Prompt",    "cmd.exe",          []),
        ("git-bash",         "Git Bash",          "git-bash.exe",     []),
        ("wsl",              "WSL",               "wsl.exe",          []),
        ("alacritty",        "Alacritty",         "alacritty.exe",    ["--working-directory", "{dir}"]),
        ("wezterm",          "WezTerm",           "wezterm-gui.exe",  ["start", "--cwd", "{dir}"]),
        ("tabby",            "Tabby",             "Tabby.exe",        []),
        ("hyper",            "Hyper",             "Hyper.exe",        []),
        ("cmder",            "Cmder",             "Cmder.exe",        ["/START", "{dir}"]),
        ("conemu",           "ConEmu",            "ConEmu64.exe",     ["-Dir", "{dir}"]),
    ];

    /// <summary>
    /// Where a terminal might be beyond PATH. Warp and Hyper install per-user
    /// and put nothing on PATH; Git Bash sits in its own install root; Windows
    /// Terminal is a Store package whose shim lives in WindowsApps.
    /// </summary>
    private static IEnumerable<string> SearchRoots()
    {
        foreach (var variable in new[]
        {
            "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
            {
                yield return root;
                yield return Path.Combine(root, "Programs");
            }
        }

        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            yield return Path.Combine(local, "Microsoft", "WindowsApps");
    }

    private static IReadOnlyList<TerminalOption>? _cache;

    /// <summary>Everything found, in the table's order.</summary>
    public static IReadOnlyList<TerminalOption> All() => _cache ??= Detect();

    private static IReadOnlyList<TerminalOption> Detect()
    {
        var found = new List<TerminalOption>();

        foreach (var (id, name, exe, args) in Known)
        {
            if (Locate(exe) is not { } path) continue;

            found.Add(new TerminalOption(id, name, path, args));
        }

        return found;
    }

    /// <summary>
    /// The executable's full path, or null. PATH first, since a winget or
    /// Store shim there is the copy the user expects to run.
    /// </summary>
    private static string? Locate(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            try
            {
                var candidate = Path.Combine(directory, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry — quotes, or a character illegal in a
                // path — is not a reason to stop looking at the rest.
            }
        }

        var stem = Path.GetFileNameWithoutExtension(exe);

        foreach (var root in SearchRoots())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, exe),
                Path.Combine(root, stem, exe),
                Path.Combine(root, stem, "bin", exe),
            })
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // An unreadable directory is not an error worth surfacing
                    // from something that runs at startup.
                }
            }
        }

        return null;
    }
}
