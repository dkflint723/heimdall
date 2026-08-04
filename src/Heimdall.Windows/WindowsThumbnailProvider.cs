using Heimdall.Core.FileSystem;

namespace Heimdall.Windows;

/// <summary>
/// Thumbnails, first pass: images are handed back as themselves for the UI to
/// decode, and everything else returns null.
///
/// **The freedesktop thumbnail cache has no Windows counterpart**, so
/// XdgThumbnailProvider's whole caching strategy is moot here rather than
/// portable. Windows caches thumbnails too, in thumbcache_*.db, but reading it
/// means IShellItemImageFactory — COM, and the thing that would also give video
/// and document thumbnails. That is a decision of its own; this is what works
/// without it.
///
/// **Video files return null**, so they keep their drawn icon rather than a
/// broken image. The Linux side gets video thumbnails from the shared cache
/// that something else populated, which is exactly the thing that does not
/// exist here.
/// </summary>
public sealed class WindowsThumbnailProvider : IThumbnailProvider
{
    /// <summary>
    /// Only what Avalonia's own decoder handles. Deliberately not the full list
    /// ImageSize can parse — BMP is in there for header reading, and it is
    /// decodable, but the point of this set is "will the UI be able to show it".
    /// </summary>
    private static readonly HashSet<string> Decodable =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public bool CanThumbnail(string path) =>
        Decodable.Contains(Path.GetExtension(path));

    public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
    {
        if (!CanThumbnail(path)) return ValueTask.FromResult<string?>(null);

        try
        {
            // A file too small to enlarge cleanly keeps its icon rather than
            // being blown up into a blur — the behaviour the README promises.
            // ImageSize reads the header only, so this costs a few bytes and
            // never decodes a 40 megapixel photo to find out it is large.
            if (ImageSize.TryRead(path) is { } dimensions
                && dimensions.Width < size && dimensions.Height < size)
                return ValueTask.FromResult<string?>(null);

            // Null from TryRead means "unknown", never "small" — an unparsed
            // header is not a reason to suppress a thumbnail, so it falls
            // through to returning the file.
            return ValueTask.FromResult<string?>(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }
}
