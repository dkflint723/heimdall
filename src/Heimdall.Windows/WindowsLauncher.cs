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
    /// **Empty, and the interface allows it** — "empty if the desktop provides
    /// no way to enumerate them". The shell's handler list lives behind
    /// IAssocHandler, which is COM, so this is a gap awaiting that decision
    /// rather than a claim that no application can open the file.
    /// <see cref="OpenWith"/> still works, because the shell's own picker does
    /// not need the list to be enumerated first.
    /// </summary>
    public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];

    /// <summary>
    /// Falls through to the shell's "Open with" dialog. With
    /// <see cref="GetOpenWithOptions"/> empty there is no specific option to
    /// honour yet, and showing the picker is the useful thing to do with the
    /// request — it is what the user asked for, one dialog further along.
    /// </summary>
    public void OpenWith(string path, LaunchOption option)
    {
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
