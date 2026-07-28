namespace Heimdall.Core.Vcs;

/// <summary>
/// What version control says about one entry. Ordered by PRECEDENCE, worst
/// first after <see cref="None"/> — a folder takes the strongest state of
/// anything beneath it, and <c>Math.Max</c> over this enum is that rule.
/// </summary>
public enum VcsState
{
    /// <summary>Not in a repository, or nothing is known about it.</summary>
    None = 0,

    /// <summary>Tracked and matching the index.</summary>
    Unmodified = 1,

    /// <summary>Not tracked, and not ignored.</summary>
    Untracked = 2,

    Added = 3,
    Modified = 4,
    Deleted = 5,

    /// <summary>A merge left this needing a human.</summary>
    Conflicted = 6,
}

/// <summary>
/// Version-control status for the entries of one folder.
///
/// Keyed by ABSOLUTE path, and only for entries directly in the folder that was
/// asked about — a change deep inside a subdirectory is rolled up onto that
/// subdirectory, because that is the only row the user can see.
/// </summary>
public sealed record VcsSnapshot(string Root, IReadOnlyDictionary<string, VcsState> States);

/// <summary>
/// Version-control decorations.
///
/// **Deliberately in Core, unlike the trash or the icon theme.** This shells out
/// to a program that behaves identically on both target platforms, so putting it
/// in `Heimdall.Linux` would mean writing it twice. `Checksums` and
/// `PathCompleter` live here for the same reason.
/// </summary>
public interface IVersionControl
{
    /// <summary>Name shown in diagnostics and, eventually, the UI.</summary>
    string Name { get; }

    /// <summary>
    /// False when the backing tool is not installed. The feature then does
    /// nothing at all rather than failing per folder — but it says so once,
    /// because a decoration that never appears is indistinguishable from one
    /// that is switched off.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The work-tree root containing <paramref name="folder"/>, or null.
    ///
    /// Must be CHEAP — it runs on every folder open, including on network
    /// mounts. Walking up for a marker file is cheap; asking the tool is not.
    /// </summary>
    string? FindRoot(string folder);

    /// <summary>
    /// Status for the entries of one folder. Null when the folder is not in a
    /// repository or the query failed — the caller draws nothing either way,
    /// and must never treat "failed" as "everything is clean".
    /// </summary>
    Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct);
}
