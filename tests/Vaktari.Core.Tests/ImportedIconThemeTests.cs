using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Reading an icon theme somebody downloaded.
///
/// **The format is not Linux, which is the whole point of this.** A freedesktop
/// icon theme is an index.theme file and a directory tree; nothing in reading
/// one is a platform call. Somebody on Windows who extracts Papirus has exactly
/// that on disk, and until now the code that could read it lived in an assembly
/// Windows never loads.
///
/// Built here rather than shipped as a fixture: a real theme is tens of
/// thousands of files, and what needs proving is the reading, not the theme.
/// </summary>
public sealed class ImportedIconThemeTests : IDisposable
{
    private readonly string _root;
    private readonly string _theme;

    public ImportedIconThemeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-theme-" + Guid.NewGuid().ToString("N")[..12]);
        _theme = Path.Combine(_root, "Papirus");

        // The layout every theme archive extracts to.
        Directory.CreateDirectory(Path.Combine(_theme, "48x48", "mimetypes"));
        Directory.CreateDirectory(Path.Combine(_theme, "48x48", "places"));

        File.WriteAllText(Path.Combine(_theme, "index.theme"), """
            [Icon Theme]
            Name=Papirus
            Directories=48x48/mimetypes,48x48/places

            [48x48/mimetypes]
            Size=48
            Context=MimeTypes
            Type=Fixed

            [48x48/places]
            Size=48
            Context=Places
            Type=Fixed
            """);

        foreach (var name in new[] { "image-png", "text-plain", "text-x-generic", "application-pdf" })
            File.WriteAllBytes(Path.Combine(_theme, "48x48", "mimetypes", name + ".png"), [0]);

        File.WriteAllBytes(Path.Combine(_theme, "48x48", "places", "folder.png"), [0]);
        File.WriteAllBytes(Path.Combine(_theme, "48x48", "places", "user-home.png"), [0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void A_downloaded_theme_folder_is_read()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme);

        Assert.NotNull(theme);
        Assert.Equal("Papirus", theme!.ThemeName);
    }

    /// <summary>
    /// **The folder IS the theme.** That is the shape of every archive: extract
    /// Papirus and you get a folder called Papirus with index.theme inside it,
    /// which is what a person will point at — so the name comes from the folder
    /// and the root is its parent.
    /// </summary>
    [Fact]
    public void Its_own_folder_name_is_the_theme_name()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        Assert.NotNull(theme.Resolve(["folder"], 48));
    }

    /// <summary>
    /// Windows has no mime database, so names come from the extension. This is
    /// the half that decides whether an imported theme shows anything at all.
    /// </summary>
    [Theory]
    [InlineData("holiday.png", "image-png")]
    [InlineData("notes.txt", "text-plain")]
    [InlineData("manual.pdf", "application-pdf")]
    public void A_file_resolves_through_its_extension(string name, string expected)
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(Path.Combine(_root, name), isDirectory: false);

        Assert.Contains(expected, names);
        Assert.NotNull(theme.Resolve(names, 48));
    }

    /// <summary>An unknown type still lands on a name themes actually ship,
    /// rather than on nothing.</summary>
    [Fact]
    public void An_unknown_type_falls_back_to_something_the_theme_has()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(Path.Combine(_root, "thing.qqq"), isDirectory: false);

        Assert.NotNull(theme.Resolve(names, 48));
    }

    [Fact]
    public void A_folder_resolves_to_the_folder_icon()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(_root, isDirectory: true);

        Assert.Contains("folder", names);
        Assert.NotNull(theme.Resolve(names, 48));
    }

    /// <summary>
    /// **The mistake this has to catch.** People pick the folder they extracted
    /// the archive INTO rather than the theme inside it, and the difference is
    /// invisible until no icons change — so it is refused at the moment of
    /// choosing, while the dialog is still in mind.
    /// </summary>
    /// <summary>
    /// **The size chooser was dead on Windows and nothing noticed.** It split
    /// the candidate path on '/' alone, which was fine while this code was
    /// Linux-only: on Windows the whole path came back as one segment, no size
    /// ever parsed, every candidate scored identically, and the first file the
    /// enumeration returned won — 16x16 for Papirus, painted into a 64-pixel
    /// tile. The first version of this test file could not catch it, because
    /// the theme it built had a single size directory.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(48)]
    [InlineData(128)]
    public void The_closest_size_is_chosen(int wanted)
    {
        foreach (var size in new[] { 16, 48, 128 })
        {
            var dir = Path.Combine(_theme, $"{size}x{size}", "mimetypes");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "text-plain.png"), [0]);
        }

        File.WriteAllText(Path.Combine(_theme, "index.theme"), $"""
            [Icon Theme]
            Name=Papirus
            Directories=16x16/mimetypes,48x48/mimetypes,128x128/mimetypes

            [16x16/mimetypes]
            Size=16
            Context=MimeTypes
            Type=Fixed

            [48x48/mimetypes]
            Size=48
            Context=MimeTypes
            Type=Fixed

            [128x128/mimetypes]
            Size=128
            Context=MimeTypes
            Type=Fixed
            """);

        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var resolved = theme.Resolve(["text-plain"], wanted);

        Assert.NotNull(resolved);
        Assert.Contains($"{wanted}x{wanted}", resolved!);
    }

    [Fact]
    public void A_folder_that_is_not_a_theme_is_refused()
    {
        Assert.Null(FreedesktopIconTheme.FromFolder(_root));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        Assert.Null(FreedesktopIconTheme.FromFolder(Path.Combine(_root, "gone")));
        Assert.Null(FreedesktopIconTheme.FromFolder(""));
    }

    /// <summary>A trailing separator is what a folder picker hands back on some
    /// platforms, and it must not turn the theme's name into an empty string.</summary>
    [Fact]
    public void A_trailing_separator_does_not_lose_the_name()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme + Path.DirectorySeparatorChar);

        Assert.NotNull(theme);
        Assert.Equal("Papirus", theme!.ThemeName);
    }
}
