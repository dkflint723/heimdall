using Heimdall.Core.FileSystem;
using Xunit;

namespace Heimdall.Core.Tests;

/// <summary>
/// `PathRules` is the foundation the Windows port stands on, and its whole
/// promise is that **Linux behaviour did not change** when fifteen inline sites
/// were routed through it. That promise was originally checked by porting both
/// the old and new logic to Python and comparing — useful once, and gone the
/// moment the terminal closed. These are the same comparisons, kept.
///
/// Everything here runs on any platform: the assertions are about POSIX paths,
/// which the rules must handle identically wherever they execute.
/// </summary>
public class PathRulesTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/home", false)]
    [InlineData("/home/flint", false)]
    [InlineData("", false)]
    [InlineData("heimdall:recent-files", false)]
    public void IsRoot_recognises_only_the_root(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsRoot(path));

    [Theory]
    [InlineData("/home/flint/", "/home/flint")]
    [InlineData("/home/flint", "/home/flint")]
    [InlineData("", "")]
    [InlineData("heimdall:trash", "heimdall:trash")]
    public void Normalise_drops_a_trailing_separator(string path, string expected)
        => Assert.Equal(expected, PathRules.Normalise(path));

    /// <summary>
    /// **A root keeps its separator.** Trimming `/` would leave an empty string,
    /// and the Windows equivalent — `C:\` becoming `C:` — names a different place
    /// entirely: the current directory on that drive.
    /// </summary>
    [Fact]
    public void Normalise_leaves_the_root_intact()
        => Assert.Equal("/", PathRules.Normalise("/"));

    /// <summary>
    /// The one deliberate behaviour change when this class was introduced. The
    /// inline code it replaced turned `//` into an empty string — a path naming
    /// nothing, which was a latent bug rather than a decision.
    /// </summary>
    [Fact]
    public void Normalise_treats_a_doubled_separator_as_the_root()
        => Assert.Equal("/", PathRules.Normalise("//"));

    /// <summary>
    /// **Null, never empty.** `Path.GetDirectoryName` returns an empty string for
    /// a bare name, and that exact difference once left the Up button enabled on
    /// a virtual path where pressing it did nothing.
    /// </summary>
    [Theory]
    [InlineData("/home/flint", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/home/flint/", "/home")]
    public void Parent_walks_up(string path, string expected)
        => Assert.Equal(expected, PathRules.Parent(path));

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("heimdall:recent-files")]
    public void Parent_is_null_where_there_is_nowhere_to_go(string path)
        => Assert.Null(PathRules.Parent(path));

    [Theory]
    [InlineData("/home/flint", "flint")]
    [InlineData("/home/flint/", "flint")]
    [InlineData("", "")]
    public void LeafName_gives_the_last_segment(string path, string expected)
        => Assert.Equal(expected, PathRules.LeafName(path));

    /// <summary>A root has no file name, so it shows as itself rather than
    /// blank — which is what the hardcoded "/" fallbacks used to do.</summary>
    [Fact]
    public void LeafName_gives_the_root_back_as_itself()
        => Assert.Equal("/", PathRules.LeafName("/"));

    [Theory]
    [InlineData("/home/flint", "/home/flint/", true)]
    [InlineData("/home/flint", "/home/flint", true)]
    [InlineData("/home/flint", "/home/other", false)]
    [InlineData(null, null, true)]
    public void Same_ignores_a_trailing_separator(string? a, string? b, bool expected)
        => Assert.Equal(expected, PathRules.Same(a, b));

    /// <summary>
    /// The walk that replaced a loop terminating on `current == "/"` — a
    /// comparison that is never true on Windows, so the old code would not have
    /// stopped where it should.
    /// </summary>
    [Fact]
    public void Ancestors_runs_from_the_root_down_to_the_path()
        => Assert.Equal(
            ["/", "/home", "/home/flint", "/home/flint/dev"],
            PathRules.Ancestors("/home/flint/dev"));

    [Fact]
    public void Ancestors_of_the_root_is_just_the_root()
        => Assert.Equal(["/"], PathRules.Ancestors("/"));

    [Fact]
    public void Ancestors_of_nothing_is_empty()
        => Assert.Empty(PathRules.Ancestors(""));

    /// <summary>
    /// Every step must be strictly shorter than the last, or a malformed path
    /// could spin forever — which is precisely how the original loop failed.
    /// </summary>
    [Fact]
    public void Ancestors_terminates_and_never_repeats()
    {
        var levels = PathRules.Ancestors("/a/b/c/d/e");

        Assert.Equal(levels.Count, levels.Distinct().Count());
        Assert.Equal("/", levels[0]);
        Assert.Equal("/a/b/c/d/e", levels[^1]);
    }
}
