using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the thumbnail cache is allowed to hold.
///
/// **Counting entries bounded nothing that mattered.** The layouts ask for 64,
/// 256 and 512 pixel thumbnails, and those are not comparable things: a 512
/// pixel tile is a megabyte of decoded pixels where a 64 pixel row icon is
/// sixteen kilobytes. A cap of 2400 entries therefore permitted somewhere
/// between 40 MB and 2.4 GB depending only on which layout somebody happened to
/// be using — and the stated purpose of the cache is that a file manager must
/// not grow without limit while you browse.
///
/// Bitmaps are made here rather than decoded from files: the subject is the
/// eviction policy, and decoding real images in a headless test would be
/// exercising Skia instead.
/// </summary>
public sealed class ThumbnailCacheBoundTests
{
    /// <summary>
    /// A megabyte of pixels — the size of one grid tile, which is the case that
    /// made the count meaningless.
    /// </summary>
    private static Bitmap Tile() =>
        new WriteableBitmap(new PixelSize(512, 512), new Vector(96, 96));

    [AvaloniaFact]
    public void A_run_of_tiles_stops_at_the_byte_limit_not_the_count()
    {
        ThumbnailLoader.Forget();

        // Well inside the 2400-entry cap and far past 192 MB: at four bytes a
        // pixel this is 800 MB asked for, which is what the old bound allowed.
        for (var i = 0; i < 800; i++) ThumbnailLoader.Remember($"tile-{i}|512", Tile());

        Assert.True(ThumbnailLoader.CachedBytes <= 192L * 1024 * 1024,
            $"held {ThumbnailLoader.CachedBytes / (1024 * 1024)} MB");

        ThumbnailLoader.Forget();
    }

    /// <summary>
    /// The accounting has to come back down as entries leave, or the cache
    /// converges on holding nothing: an eviction that forgets to subtract
    /// leaves the running total permanently over the limit, and every
    /// subsequent insert immediately evicts itself.
    /// </summary>
    [AvaloniaFact]
    public void Evicting_returns_the_bytes()
    {
        ThumbnailLoader.Forget();

        for (var i = 0; i < 400; i++) ThumbnailLoader.Remember($"a-{i}|512", Tile());
        var afterFirstRun = ThumbnailLoader.CachedBytes;

        for (var i = 0; i < 400; i++) ThumbnailLoader.Remember($"b-{i}|512", Tile());

        // Steady state, not a ratchet: the same load twice holds the same
        // amount, and a cache that had stopped keeping anything would read zero.
        Assert.Equal(afterFirstRun, ThumbnailLoader.CachedBytes);
        Assert.True(ThumbnailLoader.CachedBytes > 0);

        ThumbnailLoader.Forget();
    }

    /// <summary>
    /// Small thumbnails still fill the cache — the byte bound must not have
    /// quietly removed the entry cap, since a folder of favicons would then
    /// keep every one it ever drew.
    /// </summary>
    [AvaloniaFact]
    public void The_entry_cap_still_applies_to_small_icons()
    {
        ThumbnailLoader.Forget();

        var small = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96));

        // 3000 at 16 KB each is 48 MB — under the byte limit, over the count.
        for (var i = 0; i < 3000; i++) ThumbnailLoader.Remember($"small-{i}|64", small);

        Assert.True(ThumbnailLoader.CachedBytes < 3000L * 64 * 64 * 4,
            "nothing was evicted, so the entry cap is gone");

        ThumbnailLoader.Forget();
    }
}
