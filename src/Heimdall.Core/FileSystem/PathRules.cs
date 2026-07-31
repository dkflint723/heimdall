namespace Heimdall.Core.FileSystem;

/// <summary>
/// The handful of questions this application asks about a path's *shape* —
/// is it the root, what is its parent, what is its leaf, are these two the same
/// place — answered without assuming the separator is <c>/</c>.
///
/// **Why this exists.** Fifteen places spelled these questions out inline as
/// <c>TrimEnd('/')</c>, <c>== "/"</c> and <c>Split('/')</c>. Every one is correct
/// on Linux and wrong on Windows, and the worst of them —
/// <c>if (current == "/") break;</c> walking up to the root — would not have
/// terminated at all on <c>C:\</c>. Fifteen scattered bugs are fifteen chances to
/// fix fourteen of them.
///
/// **Behaviour on Linux is unchanged, deliberately.** This is a refactor that
/// unblocks a port, not a change of behaviour, and every method below was checked
/// against what the inline code already did.
///
/// **What this is NOT.** It does not canonicalise, resolve symlinks, or touch the
/// filesystem — it is pure string shape. Anything that needs the disk belongs on
/// <see cref="IFileSystemProvider"/>.
/// </summary>
public static class PathRules
{
    /// <summary>
    /// How two paths are compared for identity.
    ///
    /// **Ordinal on Linux, case-insensitive on Windows** — not a style choice:
    /// <c>/Home</c> and <c>/home</c> are two different directories on ext4 and
    /// the same one on NTFS, so comparing them the same way on both platforms is
    /// wrong on one of them.
    /// </summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// True for <c>/</c>, and on Windows for <c>C:\</c> and a UNC share root.
    ///
    /// Asks the framework rather than comparing to a literal:
    /// <see cref="Path.GetPathRoot(string)"/> already knows what a root looks
    /// like on the platform it is running on, and a path equal to its own root
    /// IS the root.
    /// </summary>
    public static bool IsRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var root = Path.GetPathRoot(path);

        return !string.IsNullOrEmpty(root) && string.Equals(root, path, Comparison);
    }

    /// <summary>
    /// Removes a trailing separator so two spellings of one folder compare equal
    /// — <c>/home/flint</c> and <c>/home/flint/</c> are the same place.
    ///
    /// **A root keeps its separator**, because <c>/</c> and <c>C:\</c> ARE the
    /// trailing separator; trimming it would leave <c>""</c> and <c>C:</c>, and
    /// on Windows <c>C:</c> means "the current directory on drive C", which is a
    /// different place entirely.
    /// </summary>
    public static string Normalise(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (IsRoot(path)) return path;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Trimming can expose a root that the trailing separators were hiding,
        // e.g. "C:\\\\" or "//".
        return trimmed.Length == 0 ? path[..1] : trimmed;
    }

    /// <summary>
    /// The containing folder, or null at a root — and null rather than empty
    /// <b>on purpose</b>.
    ///
    /// <see cref="Path.GetDirectoryName(string)"/> returns an EMPTY STRING for a
    /// bare name with no separator, not null. That difference already caused a
    /// live bug: the Up button reported a parent for the virtual path
    /// <c>heimdall:recent-files</c>, enabled itself, and then did nothing when
    /// pressed. Callers should be able to write <c>Parent(p) is { } up</c> and
    /// trust it.
    /// </summary>
    public static string? Parent(string? path)
    {
        if (string.IsNullOrEmpty(path) || IsRoot(path)) return null;

        var parent = Path.GetDirectoryName(Normalise(path));

        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <summary>
    /// The name to show for a path. A root has no file name, so it shows as
    /// itself — <c>/</c> rather than blank.
    /// </summary>
    public static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        var name = Path.GetFileName(Normalise(path));

        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Whether two paths name the same place, ignoring a trailing
    /// separator and honouring the platform's case rules.</summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Normalise(a), Normalise(b), Comparison);

    /// <summary>
    /// Every ancestor from the root down to <paramref name="path"/> itself, which
    /// is what the column strip walks.
    ///
    /// Written here rather than inline because the loop that did it inline
    /// terminated on <c>current == "/"</c> and would have spun forever on a
    /// Windows path — the one site in the application that turned a wrong
    /// assumption into a hang rather than a wrong answer.
    /// </summary>
    public static IReadOnlyList<string> Ancestors(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];

        var levels = new List<string>();

        for (var current = Normalise(path); !string.IsNullOrEmpty(current);)
        {
            levels.Add(current);

            if (IsRoot(current)) break;

            // Parent returns null at a root and for a rootless bare name, so this
            // cannot loop: every step is strictly shorter than the last.
            if (Parent(current) is not { } up) break;

            current = up;
        }

        levels.Reverse();

        // A relative or virtual path has no root to prepend, and forcing one in
        // would fabricate a place that does not exist.
        if (levels.Count > 0 && !IsRoot(levels[0])
            && Path.GetPathRoot(levels[0]) is { Length: > 0 } root)
            levels.Insert(0, root);

        return levels;
    }
}
