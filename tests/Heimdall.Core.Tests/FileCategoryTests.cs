using Heimdall.Core.FileSystem;
using Xunit;

namespace Heimdall.Core.Tests;

/// <summary>
/// Which icon a file gets, decided by its name alone.
///
/// **In Core, and tested without a Window, because the mapping is not a drawing
/// decision.** Whether `.mkv` is a video is a fact about the file; how a video
/// is drawn is a fact about the theme. Splitting them is what lets the table be
/// checked here rather than by looking at a screenshot.
/// </summary>
public class FileCategoryTests
{
    [Theory]
    [InlineData("notes.txt", FileCategory.Text)]
    [InlineData("PaneViewModel.cs", FileCategory.Code)]
    [InlineData("screenshot.png", FileCategory.Image)]
    [InlineData("track.flac", FileCategory.Audio)]
    [InlineData("clip.mkv", FileCategory.Video)]
    [InlineData("release.zip", FileCategory.Archive)]
    [InlineData("manual.pdf", FileCategory.Document)]
    [InlineData("budget.xlsx", FileCategory.Spreadsheet)]
    [InlineData("deck.pptx", FileCategory.Presentation)]
    [InlineData("setup.exe", FileCategory.Executable)]
    [InlineData("Inter.ttf", FileCategory.Font)]
    [InlineData("disk.iso", FileCategory.DiskImage)]
    [InlineData("settings.toml", FileCategory.Config)]
    [InlineData("server.pem", FileCategory.Key)]
    [InlineData("library.sqlite", FileCategory.Database)]
    public void A_known_extension_gets_its_category(string name, FileCategory expected)
        => Assert.Equal(expected, FileCategories.For(name, isDirectory: false));

    /// <summary>Windows is case-insensitive about extensions and so are users:
    /// a camera writing IMG_0001.JPG must not fall through to generic.</summary>
    [Theory]
    [InlineData("IMG_0001.JPG")]
    [InlineData("Photo.PNG")]
    [InlineData("SCAN.Jpeg")]
    public void Case_does_not_decide_the_category(string name)
        => Assert.Equal(FileCategory.Image, FileCategories.For(name, isDirectory: false));

    /// <summary>
    /// **A dotfile has no extension**, as far as Path.GetExtension is concerned:
    /// it reads ".gitignore" as the extension of a file with no stem. These are
    /// among the files a person most wants to pick out of a listing, so they are
    /// matched by name.
    /// </summary>
    [Theory]
    [InlineData("Makefile", FileCategory.Code)]
    [InlineData("Dockerfile", FileCategory.Code)]
    [InlineData(".gitignore", FileCategory.Config)]
    [InlineData(".editorconfig", FileCategory.Config)]
    [InlineData("LICENSE", FileCategory.Text)]
    [InlineData("README", FileCategory.Text)]
    public void A_file_with_no_extension_can_still_be_recognised(string name, FileCategory expected)
        => Assert.Equal(expected, FileCategories.For(name, isDirectory: false));

    /// <summary>
    /// GetExtension only ever returns the last one, so a compound archive would
    /// otherwise be categorised by its compressor alone — which happens to give
    /// the right answer for .tar.gz and the wrong one for anything else
    /// compressed the same way.
    /// </summary>
    [Theory]
    [InlineData("source.tar.gz")]
    [InlineData("backup.tar.xz")]
    [InlineData("dump.sql.bz2")]
    [InlineData("data.tar.zst")]
    public void A_compound_archive_reads_as_an_archive(string name)
        => Assert.Equal(FileCategory.Archive, FileCategories.For(name, isDirectory: false));

    [Fact]
    public void A_directory_is_a_folder_whatever_it_is_called()
    {
        Assert.Equal(FileCategory.Folder, FileCategories.For("Pictures", isDirectory: true));

        // Including one named like a file, which is legal and does happen.
        Assert.Equal(FileCategory.Folder, FileCategories.For("archive.zip", isDirectory: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("mystery")]
    [InlineData("data.qqq")]
    [InlineData("trailing.")]
    public void Anything_unrecognised_is_generic(string name)
        => Assert.Equal(FileCategory.Generic, FileCategories.For(name, isDirectory: false));

    /// <summary>
    /// Every category has to be reachable, or it is a drawing nothing can ever
    /// show. Folder and Generic are reached by their own rules rather than by
    /// the extension table, so they are excluded here.
    /// </summary>
    [Fact]
    public void Every_category_is_reachable_from_some_file_name()
    {
        var samples = new[]
        {
            "a.txt", "a.cs", "a.png", "a.mp3", "a.mp4", "a.zip", "a.pdf",
            "a.xlsx", "a.pptx", "a.exe", "a.ttf", "a.iso", "a.ini", "a.pem", "a.db",
        };

        var reached = samples.Select(s => FileCategories.For(s, false)).ToHashSet();

        foreach (var category in Enum.GetValues<FileCategory>())
        {
            if (category is FileCategory.Folder or FileCategory.Generic) continue;

            Assert.True(reached.Contains(category),
                $"nothing maps to {category}, so its drawing can never appear");
        }
    }
}
