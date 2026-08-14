namespace Vaktari.Core.FileSystem;

/// <summary>
/// How a platform names icons for its own files and folders.
///
/// **The theme FORMAT is shared; the naming is not.** A freedesktop desktop has
/// a mime database that classifies by glob and by content sniff, and knows the
/// user's own localised folder names from user-dirs.dirs. Windows has neither,
/// so it goes by extension — which is worse, and is the only thing available.
///
/// Separating the two is what lets the theme reader itself live in Core: it
/// parses index.theme files and walks directories, and nothing about that is
/// Linux. A person on Windows who downloads Papirus has a folder in exactly
/// that layout, and there is no reason they should not be able to use it.
/// </summary>
public interface IIconNaming
{
    /// <summary>
    /// Candidate icon names for a file, most specific first. Empty means "no
    /// opinion", and the theme falls back to its own extension table.
    /// </summary>
    IReadOnlyList<string> NamesFor(string path);

    /// <summary>
    /// Folders that get an icon of their own, as real paths — read from the
    /// platform rather than matched by name, because a localised setup calls
    /// Documents "Documentos".
    /// </summary>
    IReadOnlyList<(string Path, string[] Names)> SpecialFolders();
}
