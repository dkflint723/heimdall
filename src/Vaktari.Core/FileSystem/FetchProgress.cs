namespace Vaktari.Core.FileSystem;

/// <summary>
/// How far along a download is.
///
/// **Bytes are always known; the fraction is the extra.** Reporting only a
/// fraction meant reporting nothing at all when a server does not say how large
/// the file is — and that is not an edge case, it is the theme Vaktari ships:
/// GitHub generates these archives on the fly and sends them chunked with no
/// Content-Length, so the bar sat at zero for the whole hundred and ten
/// megabytes and read as a hung download.
///
/// pling.com, which serves the KDE Store's files, is the other half of the
/// argument for keeping both: it does send a length, so a percentage is real
/// there and worth showing.
/// </summary>
/// <param name="Bytes">How much has arrived.</param>
/// <param name="Total">How much there is, or null where the server did not say.</param>
public readonly record struct FetchProgress(long Bytes, long? Total)
{
    /// <summary>Null when there is no total to divide by, which the interface
    /// shows as an indeterminate bar rather than as no progress.</summary>
    public double? Fraction =>
        Total is > 0 ? Math.Clamp((double)Bytes / Total.Value, 0, 1) : null;

    public double Megabytes => Bytes / 1024d / 1024d;
}
