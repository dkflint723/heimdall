using Heimdall.Core.FileSystem;
using Xunit;

namespace Heimdall.Core.Tests;

/// <summary>
/// **Six formatters once disagreed about the same number.** 500 bytes rendered
/// as "500 B" in the properties window, "0.5 KB" in the sidebar, and only the
/// size column used binary unit names for 1024-based arithmetic. They were
/// collapsed into this one function across fifteen call sites; these tests are
/// what stops them drifting apart again.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1,023 B")]
    public void Below_a_kibibyte_reads_as_whole_bytes(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1024L * 1024, "1 MiB")]
    [InlineData(1024L * 1024 * 1024, "1 GiB")]
    public void Binary_units_are_labelled_honestly(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    /// <summary>
    /// One decimal up to gibibytes, two beyond — at that scale the second decimal
    /// is still worth hundreds of megabytes.
    /// </summary>
    [Fact]
    public void Terabytes_keep_a_second_decimal()
    {
        var formatted = ByteSize.Format((long)(1024L * 1024 * 1024 * 1024 * 1.25));

        Assert.Equal("1.25 TiB", formatted);
    }

    /// <summary>A negative length is not a size; it is a failed stat.</summary>
    [Fact]
    public void Negative_is_blank_rather_than_wrong()
        => Assert.Equal("", ByteSize.Format(-1));
}

/// <summary>
/// The rule a file manager is judged on within seconds of opening it: `file2`
/// comes before `file10`.
/// </summary>
public class NaturalOrderTests
{
    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("file2.txt", "file10.txt")]
    [InlineData("a", "b")]
    [InlineData("img1", "img002")]
    public void Digits_compare_as_numbers(string smaller, string larger)
        => Assert.True(NaturalOrder.Compare(smaller, larger) < 0,
            $"expected '{smaller}' before '{larger}'");

    [Fact]
    public void Equal_names_compare_equal()
        => Assert.Equal(0, NaturalOrder.Compare("file1", "file1"));

    /// <summary>
    /// Upper-cased by construction, which is exactly why the case-sensitivity
    /// setting disables itself when natural sorting is on.
    /// </summary>
    [Fact]
    public void Case_does_not_separate_otherwise_equal_names()
        => Assert.Equal(0, NaturalOrder.Compare("File", "file"));

    /// <summary>
    /// A comparator has to be self-consistent or a sort can throw. Ordering must
    /// be the exact reverse when the arguments swap.
    /// </summary>
    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("b", "a")]
    [InlineData("x", "x")]
    public void Comparison_is_antisymmetric(string a, string b)
        => Assert.Equal(
            Math.Sign(NaturalOrder.Compare(a, b)),
            -Math.Sign(NaturalOrder.Compare(b, a)));

    /// <summary>Sorting a realistic listing puts it in the order a person
    /// expects, which is the only claim that actually matters.</summary>
    [Fact]
    public void A_listing_sorts_the_way_a_person_reads_it()
    {
        var names = new[] { "file10.txt", "file2.txt", "file1.txt", "file20.txt" };

        Array.Sort(names, NaturalOrder.Compare);

        Assert.Equal(["file1.txt", "file2.txt", "file10.txt", "file20.txt"], names);
    }
}
