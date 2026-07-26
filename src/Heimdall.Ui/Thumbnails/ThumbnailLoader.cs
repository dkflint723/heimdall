using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.Thumbnails;

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

    /// <summary>
    /// Local paths of everything currently mounted from elsewhere, refreshed by
    /// the sidebar whenever it rediscovers them. Held here rather than asking
    /// IRemoteMounts per row: Discover() reads directories, and this is called
    /// once per visible row.
    /// </summary>
    public static IReadOnlyList<string> RemoteRoots { get; set; } = [];

    public static bool CanThumbnail(string path)
    {
        if (Provider is null) return false;

        var general = Settings.AppSettings.Current.General;
        if (!general.ShowPreviews) return false;

        if (!Provider.CanThumbnail(path)) return false;

        var limit = IsRemote(path)
            ? general.MaxRemotePreviewMegabytes
            : general.MaxLocalPreviewMegabytes;

        // 0 means no limit, which is the default — and it matters that the
        // stat below is skipped entirely in that case, because this runs once
        // per visible row and the listing is deliberately stat-free.
        if (limit <= 0) return true;

        try
        {
            return new FileInfo(path).Length <= (long)limit * 1024 * 1024;
        }
        catch
        {
            // Gone or unreadable between listing and here — no thumbnail.
            return false;
        }
    }

    /// <summary>
    /// A remote file costs network to read, which is the entire reason the two
    /// limits are separate: a 50 MB photo on an SMB share is a very different
    /// proposition from the same file on the local disk.
    /// </summary>
    private static bool IsRemote(string path)
    {
        foreach (var root in RemoteRoots)
            if (path.StartsWith(root, StringComparison.Ordinal)) return true;

        return false;
    }

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
