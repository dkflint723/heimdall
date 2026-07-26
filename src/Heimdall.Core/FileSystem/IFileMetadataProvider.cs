namespace Heimdall.Core.FileSystem;

/// <summary>
/// A short, type-specific fact to show inline in the listing — image
/// dimensions, an item count for a folder.
///
/// Inline rather than behind a properties dialog because most of the time you
/// want one fact about a file, and opening a window to get it is a heavy way
/// to ask. Deliberately returns a formatted string: the set of interesting
/// facts differs per file type, and a typed model would have to enumerate them
/// all in advance.
/// </summary>
public interface IFileMetadataProvider
{
    /// <summary>Cheap test so the list can skip types that will never have one.</summary>
    bool CanDescribe(string path, bool isDirectory);

    ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct);

    /// <summary>
    /// A short access summary — the POSIX mode on Linux, file attributes on
    /// Windows. A string rather than a typed model because the two have almost
    /// nothing in common beyond "how you may use this file".
    /// </summary>
    ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct);
}
