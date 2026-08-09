using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The Linux half of the `PathRules` contract, and the reason the class exists:
/// its whole promise is that **Linux behaviour did not change** when fifteen
/// inline sites were routed through it. That promise was originally checked by
/// porting both the old and new logic to Python and comparing — useful once, and
/// gone the moment the terminal closed. These are the same comparisons, kept.
///
/// Every assertion below is preserved verbatim from when this file claimed to be
/// platform-neutral. Nothing was weakened to make Windows pass; the Windows
/// cases are asserted separately, in <see cref="PathRulesWindowsTests"/>.
/// </summary>
public class PathRulesPosixTests
{
    [PosixTheory]
    [InlineData("/", true)]
    [InlineData("/home", false)]
    [InlineData("/home/flint", false)]
    public void IsRoot_recognises_only_the_root(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsRoot(path));

    [PosixTheory]
    [InlineData("/home/flint/", "/home/flint")]
    [InlineData("/home/flint", "/home/flint")]
    public void Normalise_drops_a_trailing_separator(string path, string expected)
        => Assert.Equal(expected, PathRules.Normalise(path));

    /// <summary>
    /// **A root keeps its separator.** Trimming `/` would leave an empty string,
    /// and the Windows equivalent — `C:\` becoming `C:` — names a different place
    /// entirely: the current directory on that drive.
    /// </summary>
    [PosixFact]
    public void Normalise_leaves_the_root_intact()
        => Assert.Equal("/", PathRules.Normalise("/"));

    /// <summary>
    /// The one deliberate behaviour change when this class was introduced. The
    /// inline code it replaced turned `//` into an empty string — a path naming
    /// nothing, which was a latent bug rather than a decision.
    /// </summary>
    [PosixFact]
    public void Normalise_treats_a_doubled_separator_as_the_root()
        => Assert.Equal("/", PathRules.Normalise("//"));

    [PosixTheory]
    [InlineData("/home/flint", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/home/flint/", "/home")]
    public void Parent_walks_up(string path, string expected)
        => Assert.Equal(expected, PathRules.Parent(path));

    [PosixFact]
    public void Parent_of_the_root_is_null()
        => Assert.Null(PathRules.Parent("/"));

    [PosixTheory]
    [InlineData("/home/flint", "flint")]
    [InlineData("/home/flint/", "flint")]
    public void LeafName_gives_the_last_segment(string path, string expected)
        => Assert.Equal(expected, PathRules.LeafName(path));

    /// <summary>A root has no file name, so it shows as itself rather than
    /// blank — which is what the hardcoded "/" fallbacks used to do.</summary>
    [PosixFact]
    public void LeafName_gives_the_root_back_as_itself()
        => Assert.Equal("/", PathRules.LeafName("/"));

    [PosixTheory]
    [InlineData("/home/flint", "/home/flint/", true)]
    [InlineData("/home/flint", "/home/flint", true)]
    [InlineData("/home/flint", "/home/other", false)]
    public void Same_ignores_a_trailing_separator(string? a, string? b, bool expected)
        => Assert.Equal(expected, PathRules.Same(a, b));

    /// <summary>
    /// `\` is an ordinary filename character on Linux, not a separator. The
    /// Windows fix for `Same` unifies the two separator spellings, and it must
    /// not reach across and rename a file here.
    /// </summary>
    [PosixFact]
    public void Backslash_is_an_ordinary_character()
    {
        Assert.Equal(@"/home/flint/a\b", PathRules.Normalise(@"/home/flint/a\b"));
        Assert.Equal(@"a\b", PathRules.LeafName(@"/home/flint/a\b"));
        Assert.False(PathRules.Same(@"/home/a\b", "/home/a/b"));
    }

    /// <summary>
    /// The walk that replaced a loop terminating on `current == "/"` — a
    /// comparison that is never true on Windows, so the old code would not have
    /// stopped where it should.
    /// </summary>
    [PosixFact]
    public void Ancestors_runs_from_the_root_down_to_the_path()
        => Assert.Equal(
            ["/", "/home", "/home/flint", "/home/flint/dev"],
            PathRules.Ancestors("/home/flint/dev"));

    [PosixFact]
    public void Ancestors_of_the_root_is_just_the_root()
        => Assert.Equal(["/"], PathRules.Ancestors("/"));

    /// <summary>
    /// Every step must be strictly shorter than the last, or a malformed path
    /// could spin forever — which is precisely how the original loop failed.
    /// </summary>
    [PosixFact]
    public void Ancestors_terminates_and_never_repeats()
    {
        var levels = PathRules.Ancestors("/a/b/c/d/e");

        Assert.Equal(levels.Count, levels.Distinct().Count());
        Assert.Equal("/", levels[0]);
        Assert.Equal("/a/b/c/d/e", levels[^1]);
    }
}
