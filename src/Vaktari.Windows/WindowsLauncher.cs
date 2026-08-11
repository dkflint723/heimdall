using System.Diagnostics;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Opening things the way the shell would. Markedly simpler than the Linux side,
/// which parses .desktop files and the mime database by hand: here the shell
/// already knows, and <c>UseShellExecute</c> asks it.
/// </summary>
public sealed class WindowsLauncher : IApplicationLauncher
{
    /// <summary>
    /// Windows has its own chooser, so this offers that rather than a
    /// home-made one.
    ///
    /// SHOpenWithDialog is the dialog the shell shows for "Open with > Choose
    /// another app": it lists what is installed, offers "Look for another app
    /// on this PC" to browse, and can make the choice permanent. Reproducing
    /// any of that would be worse in every respect, and would not update the
    /// association the rest of the system reads.
    /// </summary>
    public bool CanChooseApplication => true;

    public bool ChooseApplication(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        var shown = false;

        // The shell wants an STA, exactly as IAssocHandler.Invoke and the
        // property sheet do. Joined, unlike the property sheet: this dialog is
        // modal and returns when it closes, so there is nothing to keep alive
        // afterwards and the caller wants to know whether it ran.
        var thread = new Thread(() => shown = ShowOnThisThread(path)) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return shown;
    }

    private static bool ShowOnThisThread(string path)
    {
        var info = new Native.OpenAsInfo
        {
            FileName = path,
            ClassName = null,

            // EXEC opens the file once something is chosen — without it the
            // dialog sets the association and does nothing, which reads as the
            // menu entry having failed.
            //
            // ALLOW_REGISTRATION is what puts "Always use this app" on it. That
            // is the whole point of choosing: a chooser that forgets is a
            // one-shot launcher.
            Flags = Native.OpenAsFlags.Exec | Native.OpenAsFlags.AllowRegistration,
        };

        var hr = Native.SHOpenWithDialog(IntPtr.Zero, ref info);

        // The user pressing Cancel comes back as ERROR_CANCELLED, which is not
        // a failure worth reporting anywhere.
        if (hr == 0 || hr == unchecked((int)0x800704C7)) return true;

        Console.Error.WriteLine($"[vaktari] open-with chooser refused: 0x{hr:X8}");
        return false;
    }

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
