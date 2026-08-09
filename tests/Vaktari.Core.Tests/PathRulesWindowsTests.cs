using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The Windows half of the `PathRules` contract. New on 3 August 2026, when the
/// rules were first run on Windows and `Same` turned out to be wrong there.
///
/// **Windows accepts both separators.** `C:\Users` and `C:/Users` name one
/// folder, and every rule here is ultimately a string comparison, so anything
/// that does not reduce them to one string calls them two. That is not a corner
/// case: `C:/Users` is what a pasted path or a URL-ish string puts into the path
/// bar, and `Same` is what drives place highlighting and duplicate-tab
/// detection.
/// </summary>
public class PathRulesWindowsTests
{
    [WindowsTheory]
    [InlineData(@"C:\", true)]
    [InlineData("C:/", true)]
    [InlineData(@"\\srv\share", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Users\flint", false)]
    public void IsRoot_recognises_a_drive_and_a_share_root(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsRoot(path));

    /// <summary>
    /// **A root keeps its trailing separator.** Trimming `C:\` to `C:` names a
    /// different place entirely — the current directory on drive C — which is the
    /// Windows half of the reason `Normalise` special-cases a root at all.
    /// </summary>
    [WindowsFact]
    public void Normalise_leaves_a_drive_root_intact()
        => Assert.Equal(@"C:\", PathRules.Normalise(@"C:\"));

    [WindowsTheory]
    [InlineData(@"C:\Users\", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\Users")]
    [InlineData("C:/Users/", @"C:\Users")]
    [InlineData("C:/Users", @"C:\Users")]
    public void Normalise_drops_the_separator_and_settles_on_one_spelling(
        string path, string expected)
        => Assert.Equal(expected, PathRules.Normalise(path));

    [WindowsTheory]
    [InlineData(@"C:\Users\flint", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData("C:/Users/flint", @"C:\Users")]
    public void Parent_walks_up(string path, string expected)
        => Assert.Equal(expected, PathRules.Parent(path));

    [WindowsTheory]
    [InlineData(@"C:\")]
    [InlineData(@"\\srv\share")]
    public void Parent_of_a_root_is_null(string path)
        => Assert.Null(PathRules.Parent(path));

    [WindowsTheory]
    [InlineData(@"C:\Users\flint", "flint")]
    [InlineData(@"C:\Users\flint\", "flint")]
    [InlineData("C:/Users/flint", "flint")]
    public void LeafName_gives_the_last_segment(string path, string expected)
        => Assert.Equal(expected, PathRules.LeafName(path));

    /// <summary>A root has no file name, so it shows as itself rather than blank.</summary>
    [WindowsFact]
    public void LeafName_gives_a_drive_root_back_as_itself()
        => Assert.Equal(@"C:\", PathRules.LeafName(@"C:\"));

    /// <summary>
    /// The regression this file was written for. All three of these are one
    /// folder on Windows, and `Same` used to answer False to the first.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"C:\Users", "C:/Users", true)]
    [InlineData(@"C:\Users\flint", "C:/Users/flint", true)]
    [InlineData(@"C:\Users", @"C:\Users\", true)]
    [InlineData(@"C:\Users", @"c:\users", true)]
    [InlineData(@"C:\Users", @"C:\Windows", false)]
    public void Same_sees_through_separator_case_and_trailing_slash(
        string a, string b, bool expected)
        => Assert.Equal(expected, PathRules.Same(a, b));

    /// <summary>
    /// Two paths differing only in case are one folder on NTFS and two on ext4,
    /// which is why `Comparison` is platform-dependent rather than a style choice.
    /// </summary>
    [WindowsFact]
    public void Case_does_not_distinguish_two_folders()
        => Assert.True(PathRules.Same(@"C:\Program Files", @"c:\PROGRAM FILES"));

    [WindowsFact]
    public void Ancestors_runs_from_the_drive_root_down_to_the_path()
        => Assert.Equal(
            [@"C:\", @"C:\Users", @"C:\Users\flint"],
            PathRules.Ancestors(@"C:\Users\flint"));

    /// <summary>
    /// The other face of the `Same` bug. `Ancestors` used to return
    /// `["C:\", "C:\Users", "C:/Users/flint"]` — one list, two separator
    /// conventions, because the ancestors came from `Path.GetDirectoryName` and
    /// the last element was the caller's own spelling. The column strip then
    /// compared them with `Same` and did not match the folder it was showing.
    /// </summary>
    [WindowsFact]
    public void Ancestors_does_not_depend_on_how_the_path_was_spelled()
        => Assert.Equal(
            PathRules.Ancestors(@"C:\Users\flint"),
            PathRules.Ancestors("C:/Users/flint"));

    [WindowsFact]
    public void Ancestors_of_a_drive_root_is_just_the_root()
        => Assert.Equal([@"C:\"], PathRules.Ancestors(@"C:\"));

    /// <summary>
    /// Every step must be strictly shorter than the last. The loop this replaced
    /// terminated on `current == "/"`, which is never true of a Windows path —
    /// the one site in the application where the POSIX assumption caused a hang
    /// rather than a wrong answer.
    /// </summary>
    [WindowsFact]
    public void Ancestors_terminates_and_never_repeats()
    {
        var levels = PathRules.Ancestors(@"C:\a\b\c\d\e");

        Assert.Equal(levels.Count, levels.Distinct().Count());
        Assert.Equal(@"C:\", levels[0]);
        Assert.Equal(@"C:\a\b\c\d\e", levels[^1]);
    }
}
