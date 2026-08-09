using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The `PathRules` assertions that hold **on every platform**, which turns out
/// to be a much smaller set than this file once claimed.
///
/// It used to say the POSIX assertions "run on any platform: the assertions are
/// about POSIX paths, which the rules must handle identically wherever they
/// execute." Ten of the fifty-six failed the first time they were run on
/// Windows. They were not finding a bug — a POSIX literal simply names
/// something else there. Those moved to <see cref="PathRulesPosixTests"/>, with
/// Windows equivalents in <see cref="PathRulesWindowsTests"/>.
///
/// What is left here is genuinely separator-independent: the virtual paths, the
/// empty string, and null. They are also the cases with the most history —
/// every one of them was a live bug before `PathRules` existed.
/// </summary>
public class PathRulesTests
{
    /// <summary>
    /// A `vaktari:` path is not a filesystem path and has no root. Getting this
    /// wrong is what enabled the Up button on the Recent Files view, where
    /// pressing it did nothing.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("vaktari:recent-files", false)]
    [InlineData("vaktari:trash", false)]
    public void IsRoot_says_no_to_what_is_not_a_path(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsRoot(path));

    [Theory]
    [InlineData("", "")]
    [InlineData("vaktari:trash", "vaktari:trash")]
    public void Normalise_leaves_a_virtual_path_alone(string path, string expected)
        => Assert.Equal(expected, PathRules.Normalise(path));

    /// <summary>
    /// **Null, never empty.** `Path.GetDirectoryName` returns an empty string for
    /// a bare name, and that exact difference once left the Up button enabled on
    /// a virtual path where pressing it did nothing. Callers should be able to
    /// write `Parent(p) is { } up` and trust it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("vaktari:recent-files")]
    public void Parent_is_null_where_there_is_nowhere_to_go(string path)
        => Assert.Null(PathRules.Parent(path));

    [Fact]
    public void LeafName_of_nothing_is_nothing()
        => Assert.Equal("", PathRules.LeafName(""));

    [Fact]
    public void Same_treats_two_nulls_as_one_place()
        => Assert.True(PathRules.Same(null, null));

    [Fact]
    public void Ancestors_of_nothing_is_empty()
        => Assert.Empty(PathRules.Ancestors(""));

    /// <summary>
    /// A virtual path has no root to prepend, and fabricating one would name a
    /// place that does not exist.
    /// </summary>
    [Fact]
    public void Ancestors_of_a_virtual_path_is_just_itself()
        => Assert.Equal(["vaktari:trash"], PathRules.Ancestors("vaktari:trash"));
}
