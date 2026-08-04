using Heimdall.Core;

namespace Heimdall.Windows;

/// <summary>
/// **Deliberately inert, pending a decision that is not an implementation
/// detail.** WINDOWS.md §4 states it outright: "This is a design decision, not
/// an implementation detail — decide before writing code." So this stores
/// nothing rather than quietly picking one.
///
/// The two candidates both cost something real:
///
/// **NTFS alternate data streams** (`file.txt:tags`) are the closest thing to
/// the Linux extended attributes, and travel with the file the same way — but
/// they are silently destroyed by a copy to FAT or exFAT, by most archivers,
/// and by anything that round-trips a file through a service. Silently: the
/// copy succeeds and the tags are simply gone.
///
/// **A sidecar store keyed by path** survives all of that and goes stale the
/// moment a file is renamed or moved by anything other than this application —
/// which for a file manager is most of the time.
///
/// The Linux promise, from the README, is that tags "live on the file itself as
/// extended attributes, so they travel with it and other tools can read them".
/// Neither option keeps that promise, so the choice is which half to break.
///
/// **Inert is not free either**, and it is the reason this is documented here
/// rather than left as a stub: tagging appears to work and then does nothing.
/// The sidebar's tag section will simply stay empty. That is a worse experience
/// than the feature being absent, and it is why this should be decided rather
/// than left.
/// </summary>
public sealed class WindowsTagStore : ITagStore
{
    /// <summary>Never raised — nothing here ever changes.</summary>
    public event EventHandler? TagsChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<string> KnownTags => [];

    public ValueTask<IReadOnlyList<string>> GetAsync(string path, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask SetAsync(string path, IReadOnlyList<string> tags, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask ToggleAsync(
        IReadOnlyList<string> paths, string tag, bool add, CancellationToken ct)
        => ValueTask.CompletedTask;

    public void ForgetKnown(string tag) { }
}
