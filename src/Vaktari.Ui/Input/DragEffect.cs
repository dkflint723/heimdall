namespace Vaktari.Ui.Input;

/// <summary>What an unmodified drag means.</summary>
public enum DragIntent { Copy, Move }

/// <summary>
/// Copy or move, when nothing was held down.
///
/// **Windows decides by volume, and Vaktari decided by origin.** Explorer moves
/// within a drive and copies between drives — the reasoning being that a move
/// inside a volume is a rename of an entry and effectively free, while one
/// across volumes is a copy and a delete, which is slow and destroys the
/// original. Dragging to a place on another disk therefore did something
/// materially different from what Windows would have done, without saying so.
///
/// Holding a key still wins outright, as it does everywhere.
/// </summary>
public static class DragEffect
{
    public static DragIntent For(
        bool control, bool shift, bool internalDrag, IReadOnlyList<string> sources, string destination)
    {
        if (control) return DragIntent.Copy;
        if (shift) return DragIntent.Move;

        // From another application, a move would mean taking somebody else's
        // file away on a plain drag. Copying is the safe reading and what every
        // desktop does.
        if (!internalDrag) return DragIntent.Copy;

        return sources.Count > 0 && sources.All(s => SameVolume(s, destination))
            ? DragIntent.Move
            : DragIntent.Copy;
    }

    /// <summary>
    /// Whether two paths live on the same volume.
    ///
    /// Unknown counts as different, which errs towards copying — the answer
    /// that leaves the original where it was. A network path has no drive
    /// letter, and treating two unrelated shares as one volume would move files
    /// across a network on a plain drag.
    /// </summary>
    private static bool SameVolume(string a, string b)
    {
        try
        {
            var left = Path.GetPathRoot(Path.GetFullPath(a));
            var right = Path.GetPathRoot(Path.GetFullPath(b));

            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            return string.Equals(left, right, Comparison);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
