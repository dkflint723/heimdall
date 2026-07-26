namespace Heimdall.Core.FileSystem;

/// <summary>
/// Finds an image to represent a file. Deliberately returns a *path* rather
/// than pixels: decoding belongs in the UI layer where the toolkit's own
/// decoder lives, and this way a cached thumbnail costs no decode at all until
/// something actually asks to see it.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>Cheap extension test, so the list can skip files that will never have one.</summary>
    bool CanThumbnail(string path);

    /// <summary>
    /// A cached thumbnail, or the original file when it can be decoded directly.
    /// Null when there is nothing to show.
    /// </summary>
    ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct);
}
