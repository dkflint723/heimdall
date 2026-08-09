namespace Vaktari.Core.FileSystem;

/// <summary>
/// The kinds of file the application draws an icon for.
///
/// Deliberately coarse. This is not a MIME database and must not become one:
/// every value here costs a drawing, and a drawing only earns its place if a
/// person can tell it from its neighbours at 18 pixels. Two hundred categories
/// would be two hundred pages differing by a squint.
/// </summary>
public enum FileCategory
{
    Generic,
    Folder,
    Text,
    Code,
    Image,
    Audio,
    Video,
    Archive,
    Document,
    Spreadsheet,
    Presentation,
    Executable,
    Font,
    DiskImage,
    Config,
    Key,
    Database,
}

/// <summary>
/// Extension to category, shared by both platforms.
///
/// **In Core rather than in the UI, because the mapping is not a drawing
/// decision.** Whether `.mkv` is a video is a fact about the file; how a video
/// is drawn is a fact about the theme. Keeping them apart means the table can be
/// tested without a Window, which is the whole reason the headless harness
/// exists.
///
/// The desktop's own type database is deliberately not consulted. It would be
/// more thorough and it would answer differently on each machine, which is the
/// one thing this is trying to avoid — the same file should carry the same icon
/// on a KDE laptop and a Windows desktop.
/// </summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Plain text and prose.
            [".txt"] = FileCategory.Text, [".md"] = FileCategory.Text,
            [".rst"] = FileCategory.Text, [".log"] = FileCategory.Text,
            [".nfo"] = FileCategory.Text, [".rtf"] = FileCategory.Text,

            // Source and markup. `.json`, `.xml` and `.yaml` are here rather
            // than under Config: they are far more often something a person is
            // editing than something a program is reading.
            [".cs"] = FileCategory.Code, [".fs"] = FileCategory.Code,
            [".c"] = FileCategory.Code, [".h"] = FileCategory.Code,
            [".cpp"] = FileCategory.Code, [".hpp"] = FileCategory.Code,
            [".rs"] = FileCategory.Code, [".go"] = FileCategory.Code,
            [".py"] = FileCategory.Code, [".rb"] = FileCategory.Code,
            [".js"] = FileCategory.Code, [".ts"] = FileCategory.Code,
            [".jsx"] = FileCategory.Code, [".tsx"] = FileCategory.Code,
            [".java"] = FileCategory.Code, [".kt"] = FileCategory.Code,
            [".swift"] = FileCategory.Code, [".php"] = FileCategory.Code,
            [".lua"] = FileCategory.Code, [".pl"] = FileCategory.Code,
            [".sh"] = FileCategory.Code, [".bash"] = FileCategory.Code,
            [".zsh"] = FileCategory.Code, [".ps1"] = FileCategory.Code,
            [".sql"] = FileCategory.Code, [".r"] = FileCategory.Code,
            [".html"] = FileCategory.Code, [".htm"] = FileCategory.Code,
            [".css"] = FileCategory.Code, [".scss"] = FileCategory.Code,
            [".xml"] = FileCategory.Code, [".json"] = FileCategory.Code,
            [".yaml"] = FileCategory.Code, [".yml"] = FileCategory.Code,
            [".axaml"] = FileCategory.Code, [".xaml"] = FileCategory.Code,
            [".vue"] = FileCategory.Code, [".svelte"] = FileCategory.Code,

            [".png"] = FileCategory.Image, [".jpg"] = FileCategory.Image,
            [".jpeg"] = FileCategory.Image, [".gif"] = FileCategory.Image,
            [".bmp"] = FileCategory.Image, [".webp"] = FileCategory.Image,
            [".svg"] = FileCategory.Image, [".ico"] = FileCategory.Image,
            [".tif"] = FileCategory.Image, [".tiff"] = FileCategory.Image,
            [".heic"] = FileCategory.Image, [".avif"] = FileCategory.Image,
            [".psd"] = FileCategory.Image, [".xcf"] = FileCategory.Image,
            [".raw"] = FileCategory.Image, [".cr2"] = FileCategory.Image,
            [".nef"] = FileCategory.Image, [".dng"] = FileCategory.Image,

            [".mp3"] = FileCategory.Audio, [".flac"] = FileCategory.Audio,
            [".wav"] = FileCategory.Audio, [".ogg"] = FileCategory.Audio,
            [".opus"] = FileCategory.Audio, [".m4a"] = FileCategory.Audio,
            [".aac"] = FileCategory.Audio, [".wma"] = FileCategory.Audio,
            [".mid"] = FileCategory.Audio, [".midi"] = FileCategory.Audio,

            [".mp4"] = FileCategory.Video, [".mkv"] = FileCategory.Video,
            [".avi"] = FileCategory.Video, [".mov"] = FileCategory.Video,
            [".wmv"] = FileCategory.Video, [".webm"] = FileCategory.Video,
            [".flv"] = FileCategory.Video, [".m4v"] = FileCategory.Video,
            [".mpg"] = FileCategory.Video, [".mpeg"] = FileCategory.Video,

            [".zip"] = FileCategory.Archive, [".7z"] = FileCategory.Archive,
            [".rar"] = FileCategory.Archive, [".tar"] = FileCategory.Archive,
            [".gz"] = FileCategory.Archive, [".bz2"] = FileCategory.Archive,
            [".xz"] = FileCategory.Archive, [".zst"] = FileCategory.Archive,
            [".cab"] = FileCategory.Archive, [".tgz"] = FileCategory.Archive,

            [".pdf"] = FileCategory.Document, [".doc"] = FileCategory.Document,
            [".docx"] = FileCategory.Document, [".odt"] = FileCategory.Document,
            [".epub"] = FileCategory.Document, [".mobi"] = FileCategory.Document,
            [".djvu"] = FileCategory.Document, [".pages"] = FileCategory.Document,

            [".xls"] = FileCategory.Spreadsheet, [".xlsx"] = FileCategory.Spreadsheet,
            [".ods"] = FileCategory.Spreadsheet, [".csv"] = FileCategory.Spreadsheet,
            [".tsv"] = FileCategory.Spreadsheet, [".numbers"] = FileCategory.Spreadsheet,

            [".ppt"] = FileCategory.Presentation, [".pptx"] = FileCategory.Presentation,
            [".odp"] = FileCategory.Presentation, [".key"] = FileCategory.Presentation,

            [".exe"] = FileCategory.Executable, [".msi"] = FileCategory.Executable,
            [".appx"] = FileCategory.Executable, [".msix"] = FileCategory.Executable,
            [".bat"] = FileCategory.Executable, [".cmd"] = FileCategory.Executable,
            [".com"] = FileCategory.Executable, [".appimage"] = FileCategory.Executable,
            [".deb"] = FileCategory.Executable, [".rpm"] = FileCategory.Executable,
            [".dll"] = FileCategory.Executable, [".so"] = FileCategory.Executable,
            [".dylib"] = FileCategory.Executable, [".lnk"] = FileCategory.Executable,
            [".desktop"] = FileCategory.Executable,

            [".ttf"] = FileCategory.Font, [".otf"] = FileCategory.Font,
            [".woff"] = FileCategory.Font, [".woff2"] = FileCategory.Font,

            [".iso"] = FileCategory.DiskImage, [".img"] = FileCategory.DiskImage,
            [".vhd"] = FileCategory.DiskImage, [".vhdx"] = FileCategory.DiskImage,
            [".dmg"] = FileCategory.DiskImage, [".qcow2"] = FileCategory.DiskImage,

            [".ini"] = FileCategory.Config, [".conf"] = FileCategory.Config,
            [".cfg"] = FileCategory.Config, [".toml"] = FileCategory.Config,
            [".env"] = FileCategory.Config, [".properties"] = FileCategory.Config,
            [".reg"] = FileCategory.Config,

            [".pem"] = FileCategory.Key, [".crt"] = FileCategory.Key,
            [".cer"] = FileCategory.Key, [".pfx"] = FileCategory.Key,
            [".pub"] = FileCategory.Key, [".gpg"] = FileCategory.Key,
            [".asc"] = FileCategory.Key, [".kdbx"] = FileCategory.Key,

            [".db"] = FileCategory.Database, [".sqlite"] = FileCategory.Database,
            [".sqlite3"] = FileCategory.Database, [".mdb"] = FileCategory.Database,
            [".parquet"] = FileCategory.Database,
        };

    /// <summary>
    /// **Names, not extensions.** A dotfile has no extension by
    /// <see cref="Path.GetExtension"/>'s reckoning — it reads ".gitignore" as
    /// the extension of a file with no stem — and these are among the files a
    /// person most wants to pick out of a listing.
    /// </summary>
    private static readonly Dictionary<string, FileCategory> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["makefile"] = FileCategory.Code,
            ["dockerfile"] = FileCategory.Code,
            ["cmakelists.txt"] = FileCategory.Code,
            [".gitignore"] = FileCategory.Config,
            [".gitattributes"] = FileCategory.Config,
            [".editorconfig"] = FileCategory.Config,
            [".gitmodules"] = FileCategory.Config,
            ["readme"] = FileCategory.Text,
            ["license"] = FileCategory.Text,
            ["licence"] = FileCategory.Text,
            ["copying"] = FileCategory.Text,
            ["changelog"] = FileCategory.Text,
        };

    /// <summary>
    /// The category for a file name. Takes a NAME rather than a path so it
    /// cannot be tempted into touching the disk: this runs once per visible row
    /// while scrolling, and a stat there would be felt.
    /// </summary>
    public static FileCategory For(string name, bool isDirectory)
    {
        if (isDirectory) return FileCategory.Folder;
        if (string.IsNullOrEmpty(name)) return FileCategory.Generic;

        if (ByName.TryGetValue(name, out var byName)) return byName;

        var extension = Path.GetExtension(name);

        // A compound archive extension: .tar.gz should read as an archive, and
        // GetExtension only ever returns the last one.
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zst", StringComparison.OrdinalIgnoreCase))
            return FileCategory.Archive;

        return extension.Length > 0 && ByExtension.TryGetValue(extension, out var found)
            ? found
            : FileCategory.Generic;
    }
}
