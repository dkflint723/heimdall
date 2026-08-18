using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Tidying a name somebody typed.
///
/// **Explorer strips leading and trailing spaces silently**, and Windows drops
/// trailing spaces and dots at the API level — so a name typed with one asks
/// for a file and gets a different one, and what it leaves behind can be
/// awkward for other tools to open or delete.
/// </summary>
public class FileNameTests
{
    [Theory]
    [InlineData("  notes.txt  ", "notes.txt")]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Space_around_a_name_is_not_part_of_it(string? typed, string expected)
    {
        Assert.Equal(expected, FileNames.Clean(typed));
    }

    /// <summary>
    /// Windows discards these, so keeping them means asking for one name and
    /// getting another.
    /// </summary>
    [WindowsTheory]
    [InlineData("report.", "report")]
    [InlineData("report...", "report")]
    [InlineData("report ", "report")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    [InlineData("...", "")]
    public void A_trailing_dot_or_space_is_dropped_on_windows(string typed, string expected)
    {
        Assert.Equal(expected, FileNames.Clean(typed));
    }

    /// <summary>
    /// **A leading dot begins a name rather than ending one**, so a dotfile
    /// survives — trimming from both ends would turn .gitignore into gitignore.
    /// </summary>
    [Fact]
    public void A_dotfile_keeps_its_dot()
    {
        Assert.Equal(".gitignore", FileNames.Clean(".gitignore"));
        Assert.Equal(".gitignore", FileNames.Clean("  .gitignore  "));
    }

    /// <summary>
    /// **A space inside a name is somebody's business.** This is not a
    /// tidy-up-everything: it removes what the platform would remove anyway,
    /// and nothing else.
    /// </summary>
    [Fact]
    public void A_space_within_a_name_is_left_alone()
    {
        Assert.Equal("Ember Setup 0.1.0.exe", FileNames.Clean("Ember Setup 0.1.0.exe"));

        // Including one before the extension, which is legal and is how two
        // names can differ by something invisible in a listing.
        Assert.Equal("Ember Setup 0.1.0 .exe", FileNames.Clean("Ember Setup 0.1.0 .exe"));
    }
}
