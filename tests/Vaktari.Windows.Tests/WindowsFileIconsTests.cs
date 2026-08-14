using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The shell's own per-file icons.
///
/// **This exists because a review pass claimed the feature could not work** —
/// that the GDI import naming GetObject would fail to bind, since gdi32
/// exports GetObjectA and GetObjectW rather than a plain GetObject, so every
/// icon would come back null and fall through to the drawn set. Icons had been
/// watched changing on screen, which is the opposite conclusion, and an
/// argument between a reading and a screenshot is worth one assertion.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileIconsTests : IDisposable
{
    private readonly string _folder;
    private readonly string _file;

    public WindowsFileIconsTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-icons-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);

        _file = Path.Combine(_folder, "sample.txt");
        File.WriteAllText(_file, "sample");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void A_file_gets_pixels_from_the_shell()
    {
        var icon = new WindowsFileIcons().IconFor(_file, isDirectory: false, size: 48);

        Assert.NotNull(icon);
        Assert.True(icon!.Width > 0 && icon.Height > 0);
        Assert.Equal(icon.Width * icon.Height * 4, icon.Bgra.Length);
    }

    [Fact]
    public void A_folder_gets_pixels_too()
    {
        var icon = new WindowsFileIcons().IconFor(_folder, isDirectory: true, size: 48);

        Assert.NotNull(icon);
    }

    /// <summary>
    /// Not every pixel transparent. The shell returns some 32-bit bitmaps whose
    /// alpha channel is entirely zero, which drawn literally is an invisible
    /// icon — the provider treats that as opaque, and this is the assertion
    /// that would notice if it stopped.
    /// </summary>
    /// <summary>
    /// **The per-path half of the key has no natural ceiling.** Extensions are
    /// a small fixed set, but folders, shortcuts and executables are cached
    /// individually — so walking a drive would hold a bitmap per folder for the
    /// life of the process.
    /// </summary>
    [Fact]
    public void The_cache_does_not_grow_without_limit()
    {
        var icons = new WindowsFileIcons();

        // Folders are the per-path case, so each of these is a distinct entry.
        for (var i = 0; i < 3200; i++)
        {
            var dir = Path.Combine(_folder, "f" + i);
            icons.IconFor(dir, isDirectory: true, size: 48);
        }

        Assert.True(WindowsFileIcons.Cached < 3200,
            $"held {WindowsFileIcons.Cached} entries");
    }

    [Fact]
    public void The_icon_is_not_entirely_transparent()
    {
        var icon = new WindowsFileIcons().IconFor(_file, isDirectory: false, size: 48)!;

        var visible = false;

        for (var i = 3; i < icon.Bgra.Length; i += 4)
        {
            if (icon.Bgra[i] == 0) continue;

            visible = true;
            break;
        }

        Assert.True(visible, "every pixel was fully transparent");
    }
}
