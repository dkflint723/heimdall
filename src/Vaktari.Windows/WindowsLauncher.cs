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
    /// What this machine has, in the order a Windows user most likely wants.
    ///
    /// **Detection only — the user's preference is applied above this.**
    /// Settings live in the UI assembly, which references this one and not the
    /// other way round; a launcher reaching for them would invert that.
    ///
    /// Read while a context menu is being built, so the detection behind it is
    /// cached: a dozen file probes on the UI thread is what makes a menu feel
    /// slow to open.
    /// </summary>
    public IReadOnlyList<TerminalOption> Terminals => InstalledTerminals.All();

    /// <summary>
    /// F4, and the plain menu entry: the chosen terminal, or the first one
    /// found if that choice is gone or was never made.
    ///
    /// **Falls back to the old chain when detection finds nothing.** A machine
    /// with a terminal somewhere none of the probes look must still get a
    /// terminal, and "no entries were detected" is not the same fact as "there
    /// is nothing installed".
    /// </summary>
    public void OpenTerminal(string directory)
    {
        if (Terminals.FirstOrDefault() is { } preferred)
        {
            OpenTerminal(directory, preferred);
            return;
        }

        foreach (var (program, arguments) in new (string, string[])[]
        {
            ("wt.exe", ["-d", directory]),
            ("pwsh.exe", []),
            ("powershell.exe", []),
            ("cmd.exe", []),
        })
        {
            if (Start(program, arguments, directory)) return;
        }
    }

    /// <summary>
    /// Windows has the verb and owns the consent dialog, so this is a real
    /// answer here.
    /// </summary>
    public bool CanElevate => true;

    /// <summary>
    /// Runs a file as administrator, through the shell's own "runas" verb.
    ///
    /// **Vaktari never acquires rights of its own.** The verb asks the SYSTEM
    /// to start a new process elevated, and the system shows its consent dialog
    /// and makes the decision. Nothing here can bypass that, and nothing here
    /// should try — the file manager stays unelevated whatever the answer is.
    ///
    /// Declining raises ERROR_CANCELLED, which is a person saying no rather
    /// than a fault, so it is swallowed like any other cancelled dialog.
    /// </summary>
    public void OpenElevated(string path)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            // ERROR_CANCELLED among them: the consent dialog was declined, which
            // is an answer and not an error.
            Quiet.Swallowed("launcher", ex);
        }
    }

    /// <summary>
    /// A terminal here, elevated — the same consent dialog, and the same
    /// refusal to hold rights of our own.
    /// </summary>
    public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null)
    {
        var chosen = terminal ?? Terminals.FirstOrDefault();

        if (chosen is null)
        {
            // Nothing detected is not the same as nothing installed, and cmd is
            // on every Windows machine there has ever been.
            Elevate("cmd.exe", [], directory);
            return;
        }

        var arguments = chosen.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        Elevate(chosen.Command, arguments, directory);
    }

    private static void Elevate(string program, IReadOnlyList<string> arguments, string directory)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = directory,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var started = Process.Start(info);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
        }
    }

    /// <summary>One named terminal, from the menu.</summary>
    public void OpenTerminal(string directory, TerminalOption terminal)
    {
        var arguments = terminal.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        // The folder reaches it one way or the other: as an argument where the
        // terminal takes one, and as the working directory where it does not.
        if (Start(terminal.Command, arguments, directory)) return;

        // Uninstalled since the list was built, or refusing to start. Anything
        // is better than a menu entry that does nothing.
        OpenTerminal(directory);
    }

    private static bool Start(string program, IReadOnlyList<string> arguments, string directory)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                WorkingDirectory = directory,
                UseShellExecute = true,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            if (Process.Start(info) is not { } started) return false;

            started.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            // Win32Exception for "not found" is the expected case here, so it
            // is not worth a diagnostic line per candidate.
            Quiet.Swallowed("launcher", ex);
            return false;
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
