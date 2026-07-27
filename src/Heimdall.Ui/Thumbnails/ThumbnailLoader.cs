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
    /// <summary>
    /// Counted in path+size pairs, not files. The layouts request 64, 256 and
    /// 512, so a folder can occupy three entries per file — 600 was only ~200
    /// files, which is why ordinary folders hit the cap at all.
    /// </summary>
    private const int MaxCached = 2400;

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
        //
        // EVICTED BITMAPS ARE **NOT** DISPOSED, and that is the whole point.
        // This cache does not own them exclusively — every realized row holds
        // one as its Image.Source, and all three layouts stay alive when
        // hidden. Disposing on eviction destroyed bitmaps that were still on
        // screen, so cycling list → grid → compact made icons vanish: the key
        // is path|size and the layouts ask for 64, 256 and 512, so ~300 files
        // is already at the cap and one more switch evicts something visible.
        //
        // Dropping the reference is enough to bound what the cache retains; the
        // GC frees each bitmap once no row still points at it. That trades
        // prompt native-memory release for not corrupting the display, which is
        // the right way round.
        while (Order.Count > MaxCached && Order.TryDequeue(out var oldest))
        {
            Cache.TryRemove(oldest, out _);
        }
    }
}
