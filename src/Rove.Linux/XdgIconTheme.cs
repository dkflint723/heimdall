using System.Collections.Concurrent;
using Rove.Core.FileSystem;

namespace Rove.Linux;

/// <summary>
/// The freedesktop icon theme specification, as far as a file manager needs it.
///
/// Parsed straight from index.theme and the directory tree — the same approach
/// taken for kdeglobals, the XDG trash and the thumbnail cache. No binding to
/// keep in step with a Plasma release, and it works for any theme the user
/// installs, not only Breeze.
/// </summary>
public sealed class XdgIconTheme : IIconThemeProvider
{
    private readonly string[] _roots;
    private readonly List<string> _searchOrder = [];
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _indexes =
        new(StringComparer.Ordinal);

    public XdgIconTheme(string? themeName)
    {

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(home, ".local", "share");

        _roots =
        [
            Path.Combine(home, ".icons"),
            Path.Combine(dataHome, "icons"),
            "/usr/local/share/icons",
            "/usr/share/icons",
        ];

        Reload(themeName);
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
            $"[rove] icon theme '{ThemeName}', chain: {string.Join(" > ", _searchOrder)}");
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
        foreach (var segment in path.Split('/'))
        {
            if (segment.Equals("scalable", StringComparison.OrdinalIgnoreCase)) return 1;

            var digits = segment.Split('x')[0];
            if (int.TryParse(digits, out var found) && found > 0)
                return Math.Abs(found - wanted) * 2 + 2;
        }

        return int.MaxValue - 1;
    }

    public IReadOnlyList<string> NamesFor(string path, bool isDirectory)
    {
        if (isDirectory) return ["inode-directory", "folder"];

        // The glob database first: one parsed file rather than a process tree
        // per listing. Only a name it cannot classify — no extension, or an
        // unusual pattern — pays for a content sniff.
        var mime = SharedMimeInfo.ForPath(path);
        if (string.IsNullOrEmpty(mime)) mime = DesktopEntries.QueryMimeType(path);

        if (string.IsNullOrEmpty(mime)) return ["text-x-generic", "application-x-generic"];

        // image/png → image-png, then image-x-generic, then the catch-all.
        // Themes name icons after the mime type with the slash replaced, and
        // fall back to the media type when they have nothing more specific.
        var flat = mime.Replace('/', '-');
        var media = mime.Split('/')[0];

        return [flat, $"{media}-x-generic", "application-x-generic", "text-x-generic"];
    }
}
