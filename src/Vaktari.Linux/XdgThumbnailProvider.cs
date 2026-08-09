using System.Security.Cryptography;
using System.Text;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// The freedesktop thumbnail spec — the same cache Dolphin has been filling for
/// months. Most files on this machine already have a thumbnail; generating our
/// own would be duplicated work and a second copy on disk.
///
/// Only images are decoded directly when the cache misses. Video and PDF
/// thumbnails are left to the desktop's own thumbnailers rather than reimplemented.
/// </summary>
public sealed class XdgThumbnailProvider : IThumbnailProvider
{
    private static readonly string[] DecodableExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".avif",
    ];

    private static string CacheRoot
    {
        get
        {
            var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome))
                cacheHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

            return Path.Combine(cacheHome, "thumbnails");
        }
    }

    public bool CanThumbnail(string path)
    {
        if (Directory.Exists(path)) return false;

        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return false;

        // Either we can decode it, or the desktop may already have cached one
        // for a format we cannot read ourselves.
        return DecodableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
               || CachedPath(path, 128) is not null
               || CachedPath(path, 256) is not null;
    }

    public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
    {
        // Prefer a cached thumbnail at or above the requested size; falling
        // back to the original means decoding a 40 megapixel photo to draw a
        // 16 pixel icon, which is worth avoiding.
        var cached = size > 128
            ? CachedPath(path, 256) ?? CachedPath(path, 128)
            : CachedPath(path, 128) ?? CachedPath(path, 256);

        if (cached is not null) return ValueTask.FromResult<string?>(cached);

        var ext = Path.GetExtension(path);
        if (DecodableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && File.Exists(path))
            return ValueTask.FromResult<string?>(path);

        return ValueTask.FromResult<string?>(null);
    }

    /// <summary>
    /// The spec names files by the MD5 of the canonical file URI. Not a
    /// security choice — it is simply what every other thumbnailer uses, and
    /// matching it is the whole point of reading their cache.
    /// </summary>
    private static string? CachedPath(string path, int size)
    {
        var folder = size switch
        {
            <= 128 => "normal",
            <= 256 => "large",
            <= 512 => "x-large",
            _ => "xx-large",
        };

        try
        {
            var uri = "file://" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
            var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(uri)));

            var candidate = Path.Combine(CacheRoot, folder, hash + ".png");
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
