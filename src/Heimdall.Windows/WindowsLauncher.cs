using System.Diagnostics;
using Heimdall.Core;
using Heimdall.Core.FileSystem;

namespace Heimdall.Windows;

/// <summary>
/// Opening things the way the shell would. Markedly simpler than the Linux side,
/// which parses .desktop files and the mime database by hand: here the shell
/// already knows, and <c>UseShellExecute</c> asks it.
/// </summary>
public sealed class WindowsLauncher : IApplicationLauncher
{
    public void Open(string path)
    {
        try
        {
            // UseShellExecute is what makes this ShellExecute rather than
            // CreateProcess — without it, opening a .txt tries to execute it.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
        }
    }

    /// <summary>
    /// Windows Terminal first, then PowerShell, then cmd. Terminal is the
    /// default on Windows 11 but is a Store package that can be absent, and
    /// falling back keeps F4 working on a machine that has had it removed.
    /// </summary>
    public void OpenTerminal(string directory)
    {
        foreach (var (program, arguments) in new (string, string[])[]
        {
            ("wt.exe", ["-d", directory]),
            ("pwsh.exe", []),
            ("powershell.exe", []),
            ("cmd.exe", []),
        })
        {
            try
            {
                var info = new ProcessStartInfo(program)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = true,
                };

                foreach (var argument in arguments) info.ArgumentList.Add(argument);

                if (Process.Start(info) is { } started)
                {
                    started.Dispose();
                    return;
                }
            }
            catch (Exception ex)
            {
                // Win32Exception for "not found" is the expected case here, so
                // it is not worth a diagnostic line per candidate.
                Quiet.Swallowed("launcher", ex);
            }
        }
    }

    /// <summary>
    /// The shell's own handler list, which is what Explorer's "Open with"
    /// submenu is built from — so the names match what the user already sees
    /// elsewhere on their machine, default first.
    ///
    /// Was empty, on the grounds that this needed COM and COM under NativeAOT
    /// was the risky combination. The interface does permit empty — "empty if
    /// the desktop provides no way to enumerate them" — but that was never
    /// true here; nobody had tested the assumption. See <see cref="AssocHandlers"/>.
    /// </summary>
    public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        => AssocHandlers.For(path);

    /// <summary>
    /// Hands the file to the chosen handler, and falls back to the shell's
    /// picker if that cannot be done.
    ///
    /// The fallback matters more than it looks: the option was built from a
    /// list that may be a minute old, so the application behind it can have
    /// been uninstalled since the menu opened. Showing the picker is then still
    /// what the user asked for, one dialog further along.
    /// </summary>
    public void OpenWith(string path, LaunchOption option)
    {
        if (!string.IsNullOrEmpty(option.Id) && AssocHandlers.Invoke(path, option.Id)) return;

        try
        {
            var info = new ProcessStartInfo("rundll32.exe") { UseShellExecute = true };
            info.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            info.ArgumentList.Add(path);

            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
        }
    }
}
