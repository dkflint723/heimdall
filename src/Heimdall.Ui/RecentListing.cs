using Heimdall.Core.FileSystem;

namespace Heimdall.Ui;

/// <summary>
/// The two virtual listings, and everything that knows they are not folders.
///
/// **Why a path at all.** Dolphin's Recent Files and Recent Locations open in
/// the main view with columns, sorting, view modes and selection — not in a side
/// panel like search. Giving them a path means the entire pane works unchanged:
/// the result is still <see cref="FileEntry"/> in <c>Entries</c>, so grouping,
/// filtering, the three layouts and the selection machinery need to know nothing
/// about where the list came from. The cost is that the handful of places which
/// assume a path is on disk have to be taught otherwise, and they are listed on
/// <see cref="IsRecent"/>.
///
/// **The scheme cannot collide.** Every real path here begins with '/', so a
/// prefix of "heimdall:" is unambiguous without a lookup.
/// </summary>
public static class RecentPaths
{
    public const string Files = "heimdall:recent-files";
    public const string Locations = "heimdall:recent-locations";

    /// <summary>
    /// True for a virtual listing. Callers that must check this:
    /// <c>LoadListingAsync</c> (enumerates the store, not the disk, and does not
    /// start a watcher), <c>RebuildBreadcrumbs</c> (one crumb, not a '/' split),
    /// and <c>NavigateAsync</c> (recording "recent" as a recently visited folder
    /// would be circular).
    /// </summary>
    public static bool IsRecent(string? path)
        => path is Files or Locations;

    public static RecentKind KindOf(string path)
        => path == Files ? RecentKind.File : RecentKind.Folder;

    /// <summary>What the breadcrumb and the tab title show.</summary>
    public static string Label(string path)
        => path == Files ? "Recent files" : "Recent locations";
}

/// <summary>
/// Turns the recency store into a listing.
/// </summary>
public static class RecentListing
{
    /// <summary>
    /// How many entries a listing shows. The store keeps more, so forgetting a
    /// few does not shorten the list. Dolphin shows about thirty; this is
    /// higher because the day bands make a longer list navigable rather than
    /// overwhelming. It is a constant precisely so it is easy to argue about.
    /// </summary>
    private const int Show = 100;

    /// <summary>
    /// One batch, because there are at most <see cref="Show"/> entries and the
    /// streaming machinery exists for folders with hundreds of thousands.
    ///
    /// Shaped as the same <c>IAsyncEnumerable</c> the filesystem provider
    /// returns so the caller can pick a source and change nothing else.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        IRecentStore? store,
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        if (store is null) yield break;

        var kind = RecentPaths.KindOf(path);
        var entries = new List<FileEntry>(Show);

        foreach (var recent in store.Recent(kind, Show))
        {
            ct.ThrowIfCancellationRequested();

            if (Build(recent) is { } entry) entries.Add(entry);
        }

        yield return entries;

        // Required, not vestigial: an `async IAsyncEnumerable` method with no
        // await is CS1998, and this project builds with warnings as errors. The
        // work here is synchronous stat calls, which is fine — the caller
        // consumes this with ConfigureAwait(false) on a pool thread. Deleting
        // this line breaks the build.
        await Task.CompletedTask;
    }

    /// <summary>
    /// One store record as a listing row, or null if it is gone.
    ///
    /// **Entries that no longer exist are DROPPED, not shown greyed out.** A
    /// file manager that offers you a row which cannot be opened is worse than
    /// one that quietly forgets — and the store is not authoritative about the
    /// filesystem, it only remembers what you asked for.
    ///
    /// **`LastWriteTime` carries the ACCESS time here, not the modification
    /// time.** That is deliberate and it is the whole reason this listing needs
    /// no new machinery: `GroupMode.Modified` then bands it into Today /
    /// Yesterday exactly like Dolphin, sorting by time works, and the existing
    /// timestamp column shows the right value. The cost is that one field means
    /// something different in these two listings than everywhere else — which is
    /// why it is written down here rather than left to be discovered.
    /// </summary>
    private static FileEntry? Build(RecentEntry recent)
    {
        try
        {
            var flags = EntryFlags.None;
            long length = 0;

            if (Directory.Exists(recent.Path))
            {
                flags |= EntryFlags.Directory;
            }
            else if (File.Exists(recent.Path))
            {
                length = new FileInfo(recent.Path).Length;
            }
            else
            {
                return null;
            }

            var name = Path.GetFileName(recent.Path);

            // A root has no name of its own.
            if (string.IsNullOrEmpty(name)) name = recent.Path;

            if (name.StartsWith('.')) flags |= EntryFlags.Hidden;

            return new FileEntry(name, recent.Path, length, recent.When, flags);
        }
        catch
        {
            // An unreadable entry is one we cannot honestly list.
            return null;
        }
    }
}
