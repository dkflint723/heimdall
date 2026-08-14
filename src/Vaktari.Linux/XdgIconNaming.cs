using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// Freedesktop naming: the shared mime database, and the user's own folder
/// names.
///
/// **Split out of the theme reader when that moved to Core, because this is the
/// part that was genuinely Linux.** The reader itself only parses index.theme
/// files and walks directories — nothing platform-specific — which is what lets
/// somebody on Windows point it at a downloaded Papirus and have it work. What
/// could not travel is here: a mime database that classifies by glob and by
/// content sniff, and folder names read from user-dirs.dirs.
/// </summary>
public sealed class XdgIconNaming : IIconNaming
{
    /// <summary>
    /// The user's own names for these come from user-dirs.dirs — a localised
    /// setup has "Documentos", so matching on the folder name would fail.
    ///
    /// Home, Desktop, Documents, Music, Pictures and Videos are handled by the
    /// reader through Environment.SpecialFolder, which resolves them on both
    /// platforms. These are the ones with no such equivalent.
    /// </summary>
    public IReadOnlyList<(string Path, string[] Names)> SpecialFolders()
    {
        var found = new List<(string, string[])>();

        foreach (var (key, names) in new (string, string[])[]
        {
            ("XDG_DOCUMENTS_DIR", ["folder-documents"]),
            ("XDG_DOWNLOAD_DIR",  ["folder-download", "folder-downloads"]),
            ("XDG_MUSIC_DIR",     ["folder-music"]),
            ("XDG_PICTURES_DIR",  ["folder-pictures"]),
            ("XDG_VIDEOS_DIR",    ["folder-videos"]),
            ("XDG_PUBLICSHARE_DIR", ["folder-publicshare", "folder-public"]),
            ("XDG_TEMPLATES_DIR", ["folder-templates"]),
        })
        {
            if (XdgUserDirs.Read(key) is { Length: > 0 } dir) found.Add((dir.TrimEnd('/'), names));
        }

        return found;
    }

    public IReadOnlyList<string> NamesFor(string path)
    {
        // The glob database first: one parsed file rather than a process tree
        // per listing. Only a name it cannot classify — no extension, or an
        // unusual pattern — pays for a content sniff.
        var mime = SharedMimeInfo.ForPath(path);

        if (string.IsNullOrEmpty(mime))
        {
            // A dangling symlink lists but does not resolve, so there is nothing
            // to sniff. Spawning a process to be told that, once per entry, is
            // pure waste — and it is worth showing as a broken link rather than
            // as a generic file.
            if (!File.Exists(path) && !Directory.Exists(path))
                return ["inode-symlink", "emblem-symbolic-link", "text-x-generic"];

            mime = DesktopEntries.QueryMimeType(path);
        }

        // Empty rather than a guess: the reader's own extension table is a
        // better answer than "text-x-generic" for everything unclassified.
        if (string.IsNullOrEmpty(mime)) return [];

        // image/png → image-png, then image-x-generic, then the catch-all.
        // Themes name icons after the mime type with the slash replaced, and
        // fall back to the media type when they have nothing more specific.
        var flat = mime.Replace('/', '-');
        var media = mime.Split('/')[0];

        return [flat, $"{media}-x-generic", "application-x-generic", "text-x-generic"];
    }
}
