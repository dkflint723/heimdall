using System.Globalization;
using Heimdall.Core;

namespace Heimdall.Linux;

/// <summary>
/// Reads the Plasma colour scheme and UI font out of <c>kdeglobals</c>.
///
/// Parsed directly rather than through a D-Bus or KConfig binding: the file is
/// a plain INI, it is what every KDE application ultimately reads, and it means
/// no extra dependency and nothing to keep in step with a Plasma version.
/// </summary>
public sealed class KdeThemeProvider : IThemeProvider, IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;

    public KdeThemeProvider()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        _path = Path.Combine(configHome, "kdeglobals");

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is null || !Directory.Exists(directory)) return;

            _watcher = new FileSystemWatcher(directory, "kdeglobals")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            // Plasma rewrites the file on every scheme change, so this is how a
            // theme switch reaches a running application without polling.
            _watcher.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _watcher.Created += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // No watcher is survivable; the theme just won't follow live changes.
        }
    }

    public event EventHandler? Changed;

    public ThemePalette? Read()
    {
        var ini = Parse();
        if (ini.Count == 0) return null;

        string? Colour(string section, string key)
        {
            if (!ini.TryGetValue(section, out var entries)) return null;
            if (!entries.TryGetValue(key, out var value)) return null;

            // Stored as "r,g,b" decimal triples.
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return null;

            return byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b)
                ? $"#{r:X2}{g:X2}{b:X2}"
                : null;
        }

        var colours = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string role, string? value)
        {
            if (value is not null) colours[role] = value;
        }

        Add(ThemeRole.WindowBackground, Colour("Colors:Window", "BackgroundNormal"));
        Add(ThemeRole.WindowText, Colour("Colors:Window", "ForegroundNormal"));
        Add(ThemeRole.ViewBackground, Colour("Colors:View", "BackgroundNormal"));
        Add(ThemeRole.ViewAlternate, Colour("Colors:View", "BackgroundAlternate"));
        Add(ThemeRole.ViewText, Colour("Colors:View", "ForegroundNormal"));
        Add(ThemeRole.ViewDimText, Colour("Colors:View", "ForegroundInactive"));
        Add(ThemeRole.SelectionBackground, Colour("Colors:Selection", "BackgroundNormal"));
        Add(ThemeRole.SelectionText, Colour("Colors:Selection", "ForegroundNormal"));
        Add(ThemeRole.Border, Colour("Colors:Window", "ForegroundInactive"));

        // Plasma 6 keeps the accent separately; the selection colour is the
        // sensible fallback because that is what it tints.
        Add(ThemeRole.Accent,
            Colour("General", "AccentColor") ?? Colour("Colors:Selection", "BackgroundNormal"));

        if (colours.Count == 0) return null;

        var (family, size) = ReadFont(ini);

        return new ThemePalette
        {
            Colours = colours,
            FontFamily = family,
            FontSize = size,
            IsDark = IsDark(colours.GetValueOrDefault(ThemeRole.ViewBackground)),
            IconTheme = ini.GetValueOrDefault("Icons")?.GetValueOrDefault("Theme"),
        };
    }

    /// <summary>
    /// Perceived luminance, not a plain average — the eye weights green far
    /// more than blue, and an average misjudges schemes near the middle.
    /// </summary>
    private static bool IsDark(string? hex)
    {
        if (hex is not { Length: 7 }) return true;

        try
        {
            var r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            var g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            var b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);

            return (0.2126 * r + 0.7152 * g + 0.0722 * b) < 128;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>font=Family,Size,... — only the first two fields matter here.</summary>
    private static (string? Family, double? Size) ReadFont(
        Dictionary<string, Dictionary<string, string>> ini)
    {
        var value = ini.GetValueOrDefault("General")?.GetValueOrDefault("font");
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        var parts = value.Split(',');
        var family = parts[0].Trim();

        double? size = parts.Length > 1 && double.TryParse(
            parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        return (family.Length > 0 ? family : null, size);
    }

    private Dictionary<string, Dictionary<string, string>> Parse()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(_path)) return result;

            var section = "";

            foreach (var raw in File.ReadLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1];
                    continue;
                }

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                if (!result.TryGetValue(section, out var entries))
                    result[section] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

                entries[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        }
        catch
        {
            // Unreadable config is the same as no config: fall back to our own.
        }

        return result;
    }

    public void Dispose() => _watcher?.Dispose();
}
