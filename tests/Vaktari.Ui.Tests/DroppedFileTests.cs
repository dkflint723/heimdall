using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Reading what a drop is carrying.
///
/// **A drop that cannot be taken used to do nothing at all.** That is
/// indistinguishable from one that missed the pane, or from a bug — and
/// dragging out of a zip opened in Explorer does exactly that, which reads as
/// the application being unreliable rather than as a limit of what it is given.
///
/// The limit is real and worth stating: Windows offers the contents of a zip as
/// a descriptor plus one stream per item, retrieved by index from the native
/// data object. Avalonia hands a drop handler formats and bytes and does not
/// expose that object, so there is no route to the contents from here. What
/// there is a route to is recognising the case and saying so.
///
/// Against the decision rather than the whole reader: Avalonia's storage items
/// are explicitly not implementable outside the framework, so a test that needs
/// one cannot exist. Splitting the two lines that produce paths from the
/// reasoning about them is what keeps the reasoning testable at all.
/// </summary>
public sealed class DroppedFileTests
{
    private static readonly string Destination =
        OperatingSystem.IsWindows() ? @"C:\dest" : "/dest";

    private static readonly string Elsewhere =
        OperatingSystem.IsWindows() ? @"C:\from" : "/from";

    private static string At(string folder, string name) => Path.Combine(folder, name);

    private static DroppedFiles Read(string[] paths, params string[] formats) =>
        DroppedFileReader.Decide(paths, formats, Destination);

    [Fact]
    public void Ordinary_files_come_through()
    {
        var dropped = Read([At(Elsewhere, "a.txt"), At(Elsewhere, "b.txt")], "File");

        Assert.True(dropped.Any);
        Assert.Equal(2, dropped.Paths.Count);
        Assert.Empty(dropped.Refusal);
    }

    /// <summary>
    /// **The zip case, which is what "unreliable" actually was.** Explorer
    /// offers the contents of an archive as virtual files: real names, no
    /// paths. The drop carried something, so "there are no files in that" would
    /// be wrong; what it carried cannot be copied, so pretending otherwise
    /// would be worse.
    /// </summary>
    [Theory]
    [InlineData("FileGroupDescriptorW")]
    [InlineData("FileGroupDescriptor")]
    [InlineData("FileContents")]
    public void Files_inside_an_archive_are_refused_with_a_reason(string format)
    {
        var dropped = Read([], format);

        Assert.False(dropped.Any);
        Assert.Contains("inside an archive", dropped.Refusal, StringComparison.Ordinal);
        Assert.Contains("extract them first", dropped.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file dropped into the folder it already lives in achieves nothing
    /// whether copying or moving — but it is not a failure, and saying "there
    /// are no files in that" about a perfectly good file would be a lie.
    /// </summary>
    [Fact]
    public void A_file_dropped_where_it_already_is_says_so()
    {
        var dropped = Read([At(Destination, "a.txt")], "File");

        Assert.False(dropped.Any);
        Assert.Equal("that is already here", dropped.Refusal);
    }

    /// <summary>The folder itself, dragged onto its own listing.</summary>
    [Fact]
    public void A_folder_dropped_onto_itself_says_so()
    {
        Assert.Equal("that is already here", Read([Destination], "File").Refusal);
    }

    /// <summary>Some of them usable is a drop worth taking, and the ones
    /// already here are quietly left out rather than duplicated.</summary>
    [Fact]
    public void A_mixed_drop_takes_the_ones_that_would_move()
    {
        var dropped = Read([At(Destination, "here.txt"), At(Elsewhere, "new.txt")], "File");

        Assert.True(dropped.Any);
        Assert.Equal(At(Elsewhere, "new.txt"), Assert.Single(dropped.Paths));
    }

    [Fact]
    public void A_drop_with_no_files_at_all_says_that_instead()
    {
        Assert.Equal("there are no files in that", Read([], "Text").Refusal);
        Assert.Equal("that drop carried nothing", Read([]).Refusal);
    }

    /// <summary>Every refusal says something. A silent one is the bug.</summary>
    [Fact]
    public void Every_refusal_carries_a_reason()
    {
        foreach (var dropped in new[]
                 {
                     Read([]),
                     Read([], "Text"),
                     Read([], "FileGroupDescriptorW"),
                     Read([At(Destination, "a.txt")], "File"),
                 })
        {
            Assert.False(dropped.Any);
            Assert.NotEmpty(dropped.Refusal);
        }
    }
}
