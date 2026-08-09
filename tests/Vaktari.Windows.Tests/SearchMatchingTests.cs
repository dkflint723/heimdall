using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a search query means, which has to be the same sentence on both
/// systems.
///
/// **Windows had only the substring half.** `LinuxSearchProvider` treats a
/// query containing <c>*</c> or <c>?</c> as a pattern and anything else as a
/// substring; the Windows walk matched `entry.Name.Contains(text)` and nothing
/// else. So `*.cs` found every C# file on Linux and nothing whatsoever on
/// Windows — no filename contains those three characters in that order — and
/// the failure looked exactly like an empty result set rather than like an
/// unsupported syntax. A glob is the one search syntax a person tries without
/// being told it exists.
/// </summary>
[SupportedOSPlatform("windows")]
public class SearchMatchingTests
{
    private static bool Match(string name, string query, bool caseSensitive = false)
        => WindowsSearchProvider.Matches(
            name,
            query,
            glob: query.Contains('*') || query.Contains('?'),
            comparison: caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            caseSensitive: caseSensitive);

    [WindowsTheory]
    [InlineData("Program.cs", "*.cs", true)]
    [InlineData("Program.csproj", "*.cs", false)]
    [InlineData("notes.txt", "*.cs", false)]
    [InlineData("WindowsSearchProvider.cs", "*Search*", true)]
    [InlineData("WindowsSearchProvider.cs", "*Search*.cs", true)]
    [InlineData("note1.txt", "note?.txt", true)]
    [InlineData("note12.txt", "note?.txt", false)]
    public void A_pattern_is_matched_as_a_pattern(string name, string query, bool expected)
        => Assert.Equal(expected, Match(name, query));

    /// <summary>
    /// The half that already worked, kept so adding globs cannot quietly turn
    /// every plain word into a pattern that matches only whole names.
    /// </summary>
    [WindowsTheory]
    [InlineData("WindowsSearchProvider.cs", "Search", true)]
    [InlineData("WindowsSearchProvider.cs", "search", true)]
    [InlineData("notes.txt", "note", true)]
    [InlineData("notes.txt", "zzz", false)]
    public void A_plain_word_is_matched_as_a_substring(string name, string query, bool expected)
        => Assert.Equal(expected, Match(name, query));

    /// <summary>
    /// Case follows the query's own setting in both modes, rather than the
    /// pattern arm quietly ignoring it.
    /// </summary>
    [WindowsTheory]
    [InlineData("Program.CS", "*.cs", false, true)]
    [InlineData("Program.CS", "*.cs", true, false)]
    [InlineData("Program.cs", "*.cs", true, true)]
    [InlineData("Notes.txt", "notes", false, true)]
    [InlineData("Notes.txt", "notes", true, false)]
    public void Case_sensitivity_applies_to_both_arms(
        string name, string query, bool caseSensitive, bool expected)
        => Assert.Equal(expected, Match(name, query, caseSensitive));

    /// <summary>
    /// A bare <c>*</c> is a legitimate "everything here" query, and the pattern
    /// arm has to answer it rather than falling through to a substring test for
    /// a literal asterisk.
    /// </summary>
    [WindowsTheory]
    [InlineData("anything.txt")]
    [InlineData("a")]
    public void A_bare_star_matches_everything(string name)
        => Assert.True(Match(name, "*"));
}
