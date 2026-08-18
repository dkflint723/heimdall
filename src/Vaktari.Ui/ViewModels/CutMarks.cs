namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The files waiting to be moved by a paste.
///
/// **Explorer greys them, and Vaktari showed nothing at all.** Cut wrote the
/// paths to the clipboard and the listing carried on looking exactly as it had,
/// so there was no way to tell a cut from a copy, to see what was pending, or to
/// notice that a second cut had replaced the first.
///
/// Application-wide rather than per pane, because the clipboard is: cutting in
/// one tab and pasting in another is the ordinary case, and both listings should
/// show the same thing.
///
/// A static seam like <see cref="PaneViewModel.ShellMenu"/> and
/// <see cref="PaneViewModel.AskConflict"/>, because a pane has no reference to
/// the shell and inventing one to carry a set of strings would be a larger
/// change than the feature.
/// </summary>
public static class CutMarks
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(Comparer);

    private static IReadOnlySet<string> _paths = Nothing;

    /// <summary>Case-insensitively on Windows, exactly on Linux: the same rule
    /// the rest of the application compares paths by.</summary>
    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlySet<string> Paths => _paths;

    /// <summary>Raised after the set has been replaced, so a handler reading it
    /// sees the new one.</summary>
    public static event EventHandler? Changed;

    public static void Mark(IEnumerable<string> paths)
    {
        _paths = new HashSet<string>(paths, Comparer);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Called by a copy as well as by a paste. **A copy replaces the cut**, which
    /// is what the clipboard itself does — leaving the old marks up would show
    /// files as pending a move that will never happen.
    /// </summary>
    public static void Clear()
    {
        if (_paths.Count == 0) return;

        _paths = Nothing;

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
