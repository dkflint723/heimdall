namespace Rove.Core.FileSystem;

/// <summary>
/// Resolves a freedesktop icon name to a file on disk, following the desktop's
/// configured icon theme and its inheritance chain.
///
/// Returns a path rather than pixels for the same reason the thumbnail provider
/// does: decoding belongs in the UI layer, and a miss should cost nothing.
/// </summary>
public interface IIconThemeProvider
{
    /// <summary>The active theme's name, for display and cache keying.</summary>
    string ThemeName { get; }

    /// <summary>
    /// Best match for an icon name at a requested size, or null. Callers pass
    /// several candidate names in preference order — icon naming is a
    /// convention, not a guarantee, and every theme covers a different subset.
    /// </summary>
    string? Resolve(IReadOnlyList<string> names, int size);

    /// <summary>Candidate icon names for a file, most specific first.</summary>
    IReadOnlyList<string> NamesFor(string path, bool isDirectory);

    /// <summary>
    /// Adopt a different icon theme and drop everything cached for the old one.
    /// The desktop can change theme while the app runs, and the colour scheme
    /// already follows it — icons have to as well.
    /// </summary>
    void Reload(string? themeName);
}
