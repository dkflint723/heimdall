using System.Diagnostics;
using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// Makes Vaktari the desktop's file manager, through the MIME association that
/// every freedesktop desktop already agrees on.
///
/// **Unlike Windows, this is the real setting**, not an approximation of one:
/// <c>inode/directory</c> is what a desktop consults whenever anything asks it
/// to open a folder, so a browser's "show in folder", a chat client's download
/// button and a double-click in another file manager all follow it.
///
/// Done by shelling out to <c>xdg-mime</c> rather than by editing
/// <c>mimeapps.list</c> directly. The file has a precedence order across three
/// directories, a deprecated section that some desktops still read, and a cache
/// that wants updating afterwards; xdg-mime is the part of the spec that knows
/// all that, it is present wherever a desktop is, and DesktopEntries already
/// depends on it for the same reason.
/// </summary>
internal sealed class LinuxDefaultFileManager : IDefaultFileManager
{
    /// <summary>
    /// The two names for the same thing. <c>x-directory/normal</c> is the older
    /// spelling and some applications still ask for it by that name, so setting
    /// only the modern one leaves those going somewhere else.
    /// </summary>
    private static readonly string[] Types = ["inode/directory", "x-directory/normal"];

    private const string Entry = "vaktari.desktop";

    public string Caveat => "";

    public bool IsDefault()
    {
        // The first type is the one that matters; the second is a legacy alias
        // and a desktop that has never heard of it answers blank, which must
        // not read as "something else owns this".
        var current = Run("query", "default", Types[0]);

        return current is not null && current.Trim() == Entry;
    }

    public DefaultChange MakeDefault()
    {
        foreach (var type in Types)
        {
            if (Run("default", Entry, type) is null)
                return new DefaultChange(false,
                    "xdg-mime is not available, so the desktop's file-type "
                    + "database could not be changed.");
        }

        return IsDefault()
            ? new DefaultChange(true, "Vaktari now opens folders.")

            // Reported rather than assumed. The command can succeed and the
            // association still not take, when the desktop entry is not on the
            // search path — which is exactly the case for a build run from its
            // own directory rather than installed.
            : new DefaultChange(false,
                $"The desktop did not take the change. Vaktari's {Entry} may not "
                + "be installed — run install.sh, or check ~/.local/share/applications.");
    }

    public DefaultChange Restore() =>

        // **There is nothing to restore to.** xdg-mime can set an association
        // but has no "unset"; the previous default was whatever the desktop
        // would have picked, which is not recorded anywhere once it is
        // replaced. Saying so is better than deleting the line from
        // mimeapps.list and calling that the old behaviour — it is not, it is
        // merely undefined.
        new(false,
            "The desktop has no way to undo this. Choose another file manager "
            + "in your desktop's default-applications settings to change it back.");

    private static string? Run(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("xdg-mime")
            {
                RedirectStandardOutput = true,

                // Discarded: xdg-mime writes its own complaints to stderr and
                // they are not ours to relay.
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] xdg-mime failed: {ex.Message}");
            return null;
        }
    }
}
