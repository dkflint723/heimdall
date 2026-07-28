namespace Heimdall.Core;

/// <summary>
/// User tags on a file.
///
/// Tags are **names**, not colours — colour is derived from the name and is
/// pure decoration. That is not only an accessibility requirement but how the
/// freedesktop convention already works, so the two agree.
/// </summary>
public interface ITagStore
{
    /// <summary>Tags seen before, for offering in a menu. Not exhaustive.</summary>
    IReadOnlyList<string> KnownTags { get; }

    ValueTask<IReadOnlyList<string>> GetAsync(string path, CancellationToken ct);

    ValueTask SetAsync(string path, IReadOnlyList<string> tags, CancellationToken ct);

    /// <summary>Adds or removes one tag across several files in one go.</summary>
    ValueTask ToggleAsync(
        IReadOnlyList<string> paths, string tag, bool add, CancellationToken ct);

    /// <summary>
    /// Stops offering a tag, WITHOUT touching any file that carries it.
    ///
    /// Deliberately not "delete this tag everywhere": that would rewrite an
    /// extended attribute on files the user cannot see from here, possibly
    /// across mounts, with no undo. Forgetting is reversible by tagging
    /// something again; deleting is not.
    /// </summary>
    void ForgetKnown(string tag);

    /// <summary>Raised when tags change, so listings can repaint.</summary>
    event EventHandler? TagsChanged;
}
