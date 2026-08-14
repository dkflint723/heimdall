using System.Collections.Concurrent;
namespace Vaktari.Core.FileSystem;

/// <summary>
/// The freedesktop icon theme specification, as far as a file manager needs it.
///
/// Parsed straight from index.theme and the directory tree — the same approach
/// taken for kdeglobals, the XDG trash and the thumbnail cache. No binding to
/// keep in step with a Plasma release, and it works for any theme the user
/// installs, not only Breeze.
///
/// **In Core rather than in the Linux assembly, because the format is not
/// Linux.** Nothing here is a platform call: it reads index.theme files and
/// walks directories. Windows has no icon theme system of its own, but a person
/// who downloads Papirus or Tela has a folder in exactly this layout, and there
/// is no reason they should not be able to point at it. The only part that was
/// ever Linux-specific is where to look, which is now an argument.
/// </summary>
public sealed class FreedesktopIconTheme : IIconThemeProvider
{
    private readonly string[] _roots;
    private readonly List<string> _searchOrder = [];
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _indexes =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string[]> _specialFolders;
    private readonly IIconNaming? _naming;

    /// <param name="roots">Where themes live. Null asks for the freedesktop
    /// defaults, which is what a Linux desktop wants; Windows passes the one
    /// folder the user pointed at.</param>
    /// <param name="naming">How this platform names icons for its files, or
    /// null to go by extension. A freedesktop desktop has a mime database that
    /// knows far more than any table of extensions; Windows does not.</param>
    public FreedesktopIconTheme(
        string? themeName,
        IReadOnlyList<string>? roots = null,
        IIconNaming? naming = null)
    {
        _roots = roots is { Count: > 0 } ? [.. roots] : DefaultRoots();
        _naming = naming;

        _specialFolders = BuildSpecialFolders();

        Reload(themeName);
    }

    /// <summary>The spec's own search path, in its own order.</summary>
    private static string[] DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(home, ".local", "share");

        return
        [
            Path.Combine(home, ".icons"),
            Path.Combine(dataHome, "icons"),
            "/usr/local/share/icons",
            "/usr/share/icons",
        ];
    }

    /// <summary>
    /// Reads a folder somebody downloaded and extracted, or null if it does not
    /// look like an icon theme.
    ///
    /// **The folder IS the theme, so its name is the theme's name and its
    /// parent is the root.** That is the shape of every theme archive: extract
    /// Papirus and you get a folder called Papirus with index.theme inside it,
    /// which is what a person will point at.
    /// </summary>
    public static FreedesktopIconTheme? FromFolder(string folder, IIconNaming? naming = null)
    {
        try
        {
            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!Directory.Exists(trimmed)) return null;

            // index.theme is what makes a directory a theme rather than a
            // directory of pictures. Without it there is nothing to read the
            // sizes and inheritance out of.
            if (!File.Exists(Path.Combine(trimmed, "index.theme"))) return null;

            var name = Path.GetFileName(trimmed);
            var parent = Path.GetDirectoryName(trimmed);

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent)) return null;

            return new FreedesktopIconTheme(name, [parent], naming);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Reload(string? themeName)
    {
        ThemeName = string.IsNullOrWhiteSpace(themeName) ? "hicolor" : themeName;

        _searchOrder.Clear();
        _cache.Clear();
        _indexes.Clear();

        BuildSearchOrder(ThemeName, depth: 0);

        // hicolor is the specified last resort and must always be present in
        // the chain, whether or not a theme names it.
        if (!_searchOrder.Contains("hicolor")) _searchOrder.Add("hicolor");

        Console.Error.WriteLine(
            $"[vaktari] icon theme '{ThemeName}', chain: {string.Join(" > ", _searchOrder)}");
    }

    public string ThemeName { get; private set; } = "hicolor";

    /// <summary>
    /// Themes inherit, sometimes several deep — Breeze Dark inherits Breeze,
    /// which inherits hicolor. Depth-limited because a malformed index.theme
    /// can describe a cycle.
    /// </summary>
    private void BuildSearchOrder(string theme, int depth)
    {
        if (depth > 6 || _searchOrder.Contains(theme)) return;

        _searchOrder.Add(theme);

        foreach (var root in _roots)
        {
            var index = Path.Combine(root, theme, "index.theme");
            if (!File.Exists(index)) continue;

            try
            {
                foreach (var line in File.ReadLines(index))
                {
                    if (!line.StartsWith("Inherits=", StringComparison.Ordinal)) continue;

                    foreach (var parent in line[9..].Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                StringSplitOptions.TrimEntries))
                        BuildSearchOrder(parent, depth + 1);

                    break;
                }
            }
            catch
            {
                // Unreadable index: the theme still works by directory scan.
            }

            break;
        }
    }

    public string? Resolve(IReadOnlyList<string> names, int size)
    {
        foreach (var name in names)
        {
            var key = $"{name}@{size}";

            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached is not null) return cached;
                continue;
            }

            var found = Search(name, size);
            _cache[key] = found;

            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// Walks themes in inheritance order, and within a theme prefers the
    /// closest size. "scalable" wins ties because an SVG renders correctly at
    /// any size, whereas a fixed raster upscales badly.
    /// </summary>
    /// <summary>
    /// One recursive scan per theme directory, cached. Breeze ships around
    /// thirty thousand files; enumerating it per icon name, per theme in the
    /// chain, per search root — which is what the first version did — is not
    /// slow, it is unusable.
    /// </summary>
    private Dictionary<string, List<string>> IndexOf(string themeDir)
        => _indexes.GetOrAdd(themeDir, dir =>
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*",
                             new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                             }))
                {
                    var extension = Path.GetExtension(file);
                    if (extension is not (".svg" or ".png")) continue;

                    var name = Path.GetFileNameWithoutExtension(file);

                    if (!map.TryGetValue(name, out var paths))
                        map[name] = paths = [];

                    paths.Add(file);
                }
            }
            catch
            {
                // Unreadable theme directory: it contributes nothing.
            }

            return map;
        });

    private string? Search(string name, int size)
    {
        foreach (var theme in _searchOrder)
        {
            foreach (var root in _roots)
            {
                var themeDir = Path.Combine(root, theme);
                if (!Directory.Exists(themeDir)) continue;

                if (!IndexOf(themeDir).TryGetValue(name, out var candidates)) continue;

                string? best = null;
                var bestScore = int.MaxValue;

                foreach (var candidate in candidates)
                {
                    var score = SizeDistance(candidate, size);
                    if (score >= bestScore) continue;

                    bestScore = score;
                    best = candidate;
                }

                if (best is not null) return best;
            }
        }

        return null;
    }

    /// <summary>
    /// The size a directory serves, read from its path — themes lay out as
    /// theme/context/22/icon.svg or theme/22x22/context/icon.png, and both
    /// forms put the number in a path segment.
    /// </summary>
    private static int SizeDistance(string path, int wanted)
    {
        // **Both separators.** This split on '/' alone, which was correct while
        // the reader was Linux-only and silently broke the moment a Windows
        // path reached it: the whole path came back as ONE segment, no size
        // ever parsed, every candidate scored the same, and the first file the
        // enumeration happened to return won. For Papirus that is 16x16, so a
        // 64-pixel tile was painted with 16-pixel artwork — and because every
        // score tied, the scalable-beats-raster preference never fired either.
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Equals("scalable", StringComparison.OrdinalIgnoreCase)) return 1;

            var digits = segment.Split('x')[0];
            if (int.TryParse(digits, out var found) && found > 0)
                return Math.Abs(found - wanted) * 2 + 2;
        }

        return int.MaxValue - 1;
    }

    /// <summary>
    /// Special folders get their own icon names, which is why Dolphin shows a
    /// distinct Documents, Downloads and Music folder while asking for
    /// "inode-directory" everywhere gives one generic folder for all of them.
    /// Names follow the freedesktop icon naming spec.
    /// </summary>
    private IReadOnlyList<string> FolderNames(string path)
    {
        var trimmed = Trim(path);
        if (trimmed.Length == 0) return ["drive-harddisk", "folder-root", "inode-directory", "folder"];

        if (_specialFolders.TryGetValue(trimmed, out var special))
            return [.. special, "inode-directory", "folder"];

        return ["inode-directory", "folder"];
    }

    /// <summary>
    /// The folders that get an icon of their own, by their real paths.
    ///
    /// **Read from the platform, never matched by name.** A localised setup
    /// calls Documents "Documentos", and on Windows somebody may well have
    /// moved Downloads to another drive.
    /// </summary>
    private Dictionary<string, string[]> BuildSpecialFolders()
    {
        var map = new Dictionary<string, string[]>(PathComparison);

        void Add(Environment.SpecialFolder folder, params string[] names)
        {
            var path = Environment.GetFolderPath(folder);

            if (path.Length > 0) map[Trim(path)] = names;
        }

        // Resolved by the runtime on both platforms, so these need no help.
        Add(Environment.SpecialFolder.UserProfile, "user-home");
        Add(Environment.SpecialFolder.Desktop, "user-desktop", "folder-desktop");
        Add(Environment.SpecialFolder.MyDocuments, "folder-documents");
        Add(Environment.SpecialFolder.MyMusic, "folder-music");
        Add(Environment.SpecialFolder.MyPictures, "folder-pictures");
        Add(Environment.SpecialFolder.MyVideos, "folder-videos");

        // Anything the platform knows and the runtime does not — Downloads,
        // Templates, Public — comes from the naming seam.
        foreach (var (path, names) in _naming?.SpecialFolders() ?? [])
            if (path.Length > 0) map[Trim(path)] = names;

        return map;
    }

    private static string Trim(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');

    /// <summary>
    /// Case matters on one platform and not the other, and a folder map that
    /// disagrees with its filesystem misses every special folder it holds.
    /// </summary>
    private static StringComparer PathComparison =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<string> NamesFor(string path, bool isDirectory)
    {
        if (isDirectory) return FolderNames(path);

        // The platform's own answer where it has one: on a freedesktop system
        // that is the shared mime database, which knows far more than any table
        // of extensions ever will.
        if (_naming?.NamesFor(path) is { Count: > 0 } named) return named;

        return ByExtension(path);
    }

    /// <summary>
    /// Icon names from the extension alone, for a platform with no mime
    /// database — which is Windows.
    ///
    /// **Deliberately small.** These are freedesktop names, so what matters is
    /// that the common cases land on names themes actually ship. The generic
    /// fallbacks cover the rest, and a type the theme has nothing for falls
    /// through to the drawn set anyway, which is a reasonable icon rather than
    /// a blank.
    /// </summary>
    private static IReadOnlyList<string> ByExtension(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        var mime = extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" or "gif" or "bmp" or "webp" or "tiff" or "svg" => "image/" + extension,
            "mp3" or "flac" or "ogg" or "wav" or "aac" or "opus" => "audio/" + extension,
            "mp4" or "mkv" or "webm" or "avi" or "mov" => "video/" + extension,
            "pdf" => "application/pdf",
            "zip" or "gz" or "bz2" or "xz" or "7z" or "rar" or "tar" => "application/x-archive",
            "exe" or "msi" or "com" or "scr" => "application/x-executable",
            "doc" or "docx" or "odt" or "rtf" => "application/msword",
            "xls" or "xlsx" or "ods" => "application/vnd.ms-excel",
            "ppt" or "pptx" or "odp" => "application/vnd.ms-powerpoint",
            "html" or "htm" => "text/html",
            "cs" or "js" or "ts" or "py" or "rs" or "go" or "c" or "h" or "cpp" or "java"
                or "rb" or "sh" or "ps1" or "bat" or "cmd" => "text/x-script",
            "txt" or "md" or "log" or "csv" or "xml" or "json" or "yaml" or "yml"
                or "toml" or "ini" or "cfg" or "conf" => "text/plain",
            _ => "",
        };

        if (mime.Length == 0) return ["text-x-generic", "application-x-generic"];

        var flat = mime.Replace('/', '-');
        var media = mime.Split('/')[0];

        return [flat, $"{media}-x-generic", "application-x-generic", "text-x-generic"];
    }
}
