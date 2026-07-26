using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Rove.Core.FileSystem;

namespace Rove.Ui.Thumbnails;

/// <summary>
/// Decodes thumbnails and keeps a bounded in-memory cache of them.
///
/// Bounded because a bitmap per row across a few large directories will happily
/// consume hundreds of megabytes, and a file manager that grows without limit
/// while you browse is worse than one with no thumbnails at all.
/// </summary>
public static class ThumbnailLoader
{
    private const int MaxCached = 600;

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private static readonly ConcurrentQueue<string> Order = new();

    public static IThumbnailProvider? Provider { get; set; }

    public static bool CanThumbnail(string path)
        => Provider?.CanThumbnail(path) ?? false;

    public static async Task<Bitmap?> LoadAsync(string path, int size, CancellationToken ct)
    {
        if (Provider is null) return null;

        var key = $"{path}|{size}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var source = await Provider.GetThumbnailPathAsync(path, size, ct).ConfigureAwait(false);
        if (source is null || ct.IsCancellationRequested) return null;

        try
        {
            // DecodeToWidth so a huge original is never fully materialised —
            // decoding a 40 megapixel photo to draw a 16 pixel icon is exactly
            // the kind of work that makes scrolling stutter.
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(source);
                return Bitmap.DecodeToWidth(stream, size);
            }, ct).ConfigureAwait(false);

            Remember(key, bitmap);
            return bitmap;
        }
        catch
        {
            // Corrupt, unreadable, or an unsupported format — no thumbnail.
            return null;
        }
    }

    private static void Remember(string key, Bitmap bitmap)
    {
        if (!Cache.TryAdd(key, bitmap)) return;

        Order.Enqueue(key);

        // Crude FIFO rather than true LRU: tracking access order would need a
        // lock on the read path, which is the path that has to stay fast.
        while (Order.Count > MaxCached && Order.TryDequeue(out var oldest))
        {
            if (Cache.TryRemove(oldest, out var evicted)) evicted.Dispose();
        }
    }
}
