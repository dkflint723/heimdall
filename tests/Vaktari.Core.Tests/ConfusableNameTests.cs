using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Names in one folder that the eye cannot tell apart.
///
/// **Taken from a real folder.** "Ember Setup 0.1.0 .exe" and
/// "Ember Setup 0.1.0.exe" sat side by side, differing by one space before the
/// extension — legal, distinct to the filesystem, and invisible in any listing
/// including Explorer's. The question it prompted was "how do I have two files
/// with the same name", which a listing had no way of answering.
/// </summary>
public class ConfusableNameTests
{
    private static IReadOnlySet<string> Among(params string[] names) =>
        ConfusableNames.Among(names.Select(n => ($"/f/{n}", n)));

    [Fact]
    public void A_space_before_the_extension_is_caught()
    {
        var found = Among("Ember Setup 0.1.0 .exe", "Ember Setup 0.1.0.exe", "other.exe");

        Assert.Equal(2, found.Count);
        Assert.Contains("/f/Ember Setup 0.1.0 .exe", found);
        Assert.Contains("/f/Ember Setup 0.1.0.exe", found);
        Assert.DoesNotContain("/f/other.exe", found);
    }

    [Theory]
    [InlineData("notes.txt", "notes .txt")]
    [InlineData("notes.txt", " notes.txt")]
    [InlineData("notes.txt", "notes.txt ")]
    [InlineData("notes.txt", "NOTES.TXT")]
    [InlineData("my file.txt", "myfile.txt")]
    public void Names_differing_only_invisibly_are_marked(string a, string b)
    {
        Assert.Equal(2, Among(a, b).Count);
    }

    /// <summary>
    /// **Ordinary names are left alone.** A marker on every row would say
    /// nothing, and a marker that cries wolf is worse than none.
    /// </summary>
    [Theory]
    [InlineData("one.txt", "two.txt")]
    [InlineData("report.doc", "report.pdf")]
    [InlineData("a.txt", "ab.txt")]
    public void Names_anybody_can_tell_apart_are_not(string a, string b)
    {
        Assert.Empty(Among(a, b));
    }

    [Fact]
    public void One_file_is_never_confusable_with_itself()
    {
        Assert.Empty(Among("notes.txt"));
        Assert.Empty(Among());
    }

    /// <summary>All of them, when three collide — not just the pair that
    /// happened to be compared first.</summary>
    [Fact]
    public void Every_member_of_a_group_is_marked()
    {
        var found = Among("a b.txt", "ab.txt", "A B.txt", "different.txt");

        Assert.Equal(3, found.Count);
        Assert.DoesNotContain("/f/different.txt", found);
    }

    /// <summary>
    /// **Whitespace and case, and nothing cleverer.** Unicode has whole
    /// families of characters that resemble each other, and chasing those would
    /// flag names that are merely unusual — a Cyrillic folder name is not a
    /// mistake, and telling somebody it is would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void Merely_unusual_names_are_not_accused()
    {
        Assert.Empty(Among("отчет.txt", "otchet.txt"));
        Assert.Empty(Among("naïve.txt", "naive.txt"));
    }
}
