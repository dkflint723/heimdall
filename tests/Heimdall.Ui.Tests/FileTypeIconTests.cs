using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Heimdall.Ui.Thumbnails;
using Xunit;

namespace Heimdall.Ui.Tests;

/// <summary>
/// The empty and full folder drawings, and the cache that keeps them apart.
///
/// **The cache is the part that can silently break.** These drawings are built
/// once and handed to every row that wants one, so the key has to carry the
/// full/empty state as well as the category. Keyed on category alone, whichever
/// variant happened to be drawn first would be served to every folder for the
/// rest of the session — and the bug would depend on which folder the pane
/// realised first, so it would appear and vanish between runs.
/// </summary>
public class FileTypeIconTests
{
    /// <summary>The drawings inside the clipping group For() wraps everything in.</summary>
    private static IReadOnlyList<Drawing> PartsOf(IImage icon)
    {
        var outer = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingImage>(icon).Drawing);
        var inner = Assert.IsType<DrawingGroup>(Assert.Single(outer.Children));

        return inner.Children;
    }

    [AvaloniaFact]
    public void A_full_folder_carries_the_page_an_empty_one_does_not()
    {
        var empty = PartsOf(FileTypeIcon.For("Projects", isDirectory: true));
        var full = PartsOf(FileTypeIcon.For("Projects", isDirectory: true, hasContents: true));

        // Back panel, pocket, seam — plus the page and its turned corner.
        Assert.Equal(3, empty.Count);
        Assert.Equal(5, full.Count);
    }

    [AvaloniaFact]
    public void The_two_folder_states_are_cached_apart()
    {
        var empty = FileTypeIcon.For("Projects", isDirectory: true);
        var full = FileTypeIcon.For("Projects", isDirectory: true, hasContents: true);

        Assert.NotSame(empty, full);

        // And asking again gives the same drawing back rather than building a
        // second one, which is the reason the cache exists at all.
        Assert.Same(empty, FileTypeIcon.For("Anything else", isDirectory: true));
        Assert.Same(full, FileTypeIcon.For("Anything else", isDirectory: true, hasContents: true));
    }

    /// <summary>
    /// Only a folder can be full, and nothing should ever ask otherwise — but if
    /// something does, it must not quietly get a different drawing from the one
    /// every other row with that extension has.
    /// </summary>
    [AvaloniaFact]
    public void The_flag_does_not_change_a_file()
    {
        var parts = PartsOf(FileTypeIcon.For("notes.txt", isDirectory: false, hasContents: true));

        // Body, fold, mark — the ordinary page, with nothing added.
        Assert.Equal(3, parts.Count);
    }

    [AvaloniaFact]
    public void Clearing_drops_the_drawings_so_a_palette_change_takes_effect()
    {
        var before = FileTypeIcon.For("Projects", isDirectory: true, hasContents: true);

        FileTypeIcon.Clear();

        Assert.NotSame(before, FileTypeIcon.For("Projects", isDirectory: true, hasContents: true));
    }
}
