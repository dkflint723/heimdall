namespace Vaktari.Linux;

/// <summary>
/// Reads the shared-mime-info glob database directly.
///
/// This is the same data <c>xdg-mime query filetype</c> consults, but that is a
/// shell script which spawns several processes per call — and it was being
/// called once per file to pick an icon name. A listing of a few thousand files
/// meant a few thousand process trees. The database is one file, parsed once.
///
/// Format is documented by shared-mime-info: <c>weight:mimetype:glob[:flags]</c>,
/// one per line, weight defaulting to 50 and higher winning.
/// </summary>
public static class SharedMimeInfo
{
    private static readonly Lazy<Database> Loaded = new(Load, isThreadSafe: true);

    private sealed record Database(
        Dictionary<string, string> ByExtension,
        Dictionary<string, string> ByName);

    /// <summary>Later roots override earlier ones, per the spec's precedence.</summary>
    private static IEnumerable<string> Roots()
    {
        yield return "/usr/share/mime/globs2";
        yield return "/usr/local/share/mime/globs2";

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        yield return Path.Combine(dataHome, "mime", "globs2");
    }

    private static Database Load()
    {
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Roots())
        {
            if (!File.Exists(file)) continue;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#') continue;

                    var parts = line.Split(':', 3);
                    if (parts.Length < 3) continue;

                    if (!int.TryParse(parts[0], out var weight)) weight = 50;

                    var mime = parts[1];
                    var glob = parts[2];

                    // "*.tar.gz" style — the common case by a wide margin.
                    if (glob.StartsWith("*.", StringComparison.Ordinal)
                        && !glob.AsSpan(2).ContainsAny('*', '?', '['))
                    {
                        var extension = glob[2..];

                        if (weights.TryGetValue(extension, out var existing) && existing > weight)
                            continue;

                        extensions[extension] = mime;
                        weights[extension] = weight;
                    }
                    // A literal filename, like "Makefile" or ".bashrc".
                    else if (!glob.AsSpan().ContainsAny('*', '?', '['))
                    {
                        names[glob] = mime;
                    }

                    // Anything with a wildcard in the middle is rare enough that
                    // it falls through to xdg-mime rather than being reimplemented.
                }
            }
            catch
            {
                // An unreadable database just means fewer known types.
            }
        }

        return new Database(extensions, names);
    }

    /// <summary>
    /// The mime type for a filename, or empty when the database has no answer
    /// and the caller should fall back to a content sniff.
    /// </summary>
    public static string ForPath(string path)
    {
        var database = Loaded.Value;
        var name = Path.GetFileName(path);

        if (name.Length == 0) return "";
        if (database.ByName.TryGetValue(name, out var exact)) return exact;

        // Longest suffix first, so "archive.tar.gz" resolves as tar.gz rather
        // than gz — which is the difference between an archive icon and a
        // generic compressed-file one.
        var start = 0;

        while (true)
        {
            var dot = name.IndexOf('.', start);
            if (dot < 0 || dot == name.Length - 1) return "";

            var suffix = name[(dot + 1)..];
            if (database.ByExtension.TryGetValue(suffix, out var mime)) return mime;

            start = dot + 1;
        }
    }
}
