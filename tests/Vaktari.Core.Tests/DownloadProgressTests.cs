using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Reporting how far along a download is.
///
/// **The case that mattered was the one that reported nothing.** Progress was
/// only ever a fraction, so a server that does not say how large the file is
/// produced no reports at all — and that is not a rare server, it is the theme
/// Vaktari ships. GitHub builds those archives on the fly and sends them
/// chunked with no Content-Length, measured against the exact URL in the
/// catalogue, so the bar sat at zero for a hundred and ten megabytes.
///
/// Tested here rather than through the view model: progress is marshalled with
/// Progress&lt;T&gt;, which posts, so a test at that level races the completion
/// message and asserts on whichever arrives first.
/// </summary>
public sealed class DownloadProgressTests
{
    private sealed class Collector : IProgress<FetchProgress>
    {
        public List<FetchProgress> Reports { get; } = [];

        public void Report(FetchProgress value) => Reports.Add(value);
    }

    private static void Drain(Stream stream)
    {
        var buffer = new byte[64 * 1024];

        while (stream.Read(buffer, 0, buffer.Length) > 0) { }
    }

    [Fact]
    public void A_download_with_no_declared_size_still_reports_bytes()
    {
        var collector = new Collector();

        using var counted = new IconThemeInstaller.CountingStream(
            new MemoryStream(new byte[5 * 1024 * 1024]), expected: null, collector);

        Drain(counted);

        Assert.NotEmpty(collector.Reports);

        // Nothing to take a percentage of, which the interface shows as an
        // indeterminate bar rather than as no progress at all.
        Assert.All(collector.Reports, r => Assert.Null(r.Fraction));

        // Throttled to whole megabytes, plus one the moment anything at all
        // arrives — which is what turns the bar from idle into moving, and the
        // difference between "working" and "hung" to somebody watching it.
        Assert.Equal(0, collector.Reports[0].Megabytes, 0);
        Assert.Equal(6, collector.Reports.Count);

        Assert.Equal(5 * 1024 * 1024, collector.Reports[^1].Bytes);
        Assert.Equal(5, collector.Reports[^1].Megabytes, 3);
    }

    /// <summary>
    /// Where a length IS sent — pling.com, which serves the KDE Store's files,
    /// does — the percentage is real and is what gets shown.
    /// </summary>
    [Fact]
    public void A_download_with_a_declared_size_reports_a_fraction()
    {
        var collector = new Collector();
        var size = 400 * 1024;

        using var counted = new IconThemeInstaller.CountingStream(
            new MemoryStream(new byte[size]), size, collector);

        Drain(counted);

        Assert.NotEmpty(collector.Reports);
        Assert.All(collector.Reports, r => Assert.NotNull(r.Fraction));

        Assert.Equal(1d, collector.Reports[^1].Fraction!.Value, 3);
        Assert.Equal(size, collector.Reports[^1].Bytes);
    }

    /// <summary>
    /// **Throttled, for the reason it always was.** A bar told a hundred
    /// thousand times is a hundred thousand dispatcher posts and a slower
    /// download. Whole percents where there is a total, so a hundred at most
    /// however many reads it took.
    /// </summary>
    [Fact]
    public void Reporting_is_throttled_rather_than_told_every_read()
    {
        var collector = new Collector();
        var size = 8 * 1024 * 1024;

        using var counted = new IconThemeInstaller.CountingStream(
            new MemoryStream(new byte[size]), size, collector);

        var buffer = new byte[4096];
        var reads = 0;

        while (counted.Read(buffer, 0, buffer.Length) > 0) reads++;

        Assert.True(reads > 1000, $"expected many reads, got {reads}");
        Assert.True(collector.Reports.Count <= 101,
            $"expected at most one report per whole percent, got {collector.Reports.Count}");
    }

    [Theory]
    [InlineData(0, 100, 0d)]
    [InlineData(50, 100, 0.5)]
    [InlineData(100, 100, 1d)]
    [InlineData(150, 100, 1d)]   // clamped: a server may understate the length
    public void A_fraction_is_bytes_over_total_clamped(long bytes, long total, double expected)
    {
        Assert.Equal(expected, new FetchProgress(bytes, total).Fraction!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void A_fraction_needs_a_total_worth_dividing_by(long? total)
    {
        Assert.Null(new FetchProgress(1024, total).Fraction);
    }
}
