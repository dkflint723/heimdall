namespace Heimdall.Linux;

/// <summary>
/// Reads <c>user-dirs.dirs</c>, which records where the user actually keeps
/// Documents, Downloads and the rest.
///
/// Matching on folder *names* would be wrong: a localised setup has
/// "Documentos" or "Téléchargements", and the user is free to point these
/// anywhere. This file is the only authority.
/// </summary>
public static class XdgUserDirs
{
    private static readonly Lazy<Dictionary<string, string>> Entries = new(Load, isThreadSafe: true);

    /// <summary>The path for a key such as <c>XDG_DOWNLOAD_DIR</c>, or null.</summary>
    public static string? Read(string key)
        => Entries.Value.TryGetValue(key, out var path) ? path : null;

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome)) configHome = Path.Combine(home, ".config");

        var file = Path.Combine(configHome, "user-dirs.dirs");

        try
        {
            if (!File.Exists(file)) return result;

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim().Trim('"');

                if (!key.StartsWith("XDG_", StringComparison.Ordinal)) continue;

                result[key] = value.Replace("$HOME", home, StringComparison.Ordinal);
            }
        }
        catch
        {
            // No file, or unreadable: callers fall back to conventional names.
        }

        return result;
    }
}
