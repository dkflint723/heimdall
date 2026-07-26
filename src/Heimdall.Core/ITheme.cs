namespace Rove.Core;

/// <summary>
/// Named colour roles the UI paints with. Roles rather than literal colours so
/// the same markup works under any desktop theme — and so a colour scheme
/// change is one lookup table, not a sweep through the markup.
/// </summary>
public static class ThemeRole
{
    public const string WindowBackground = "window.bg";
    public const string WindowText = "window.fg";
    public const string ViewBackground = "view.bg";
    public const string ViewAlternate = "view.alt";
    public const string ViewText = "view.fg";
    public const string ViewDimText = "view.fg.dim";
    public const string SelectionBackground = "selection.bg";
    public const string SelectionText = "selection.fg";
    public const string Accent = "accent";
    public const string Border = "border";
}

/// <summary>
/// Colours as hex strings and the desktop's UI font. Strings keep Core free of
/// any toolkit type — the UI layer parses them.
/// </summary>
public sealed record ThemePalette
{
    public required IReadOnlyDictionary<string, string> Colours { get; init; }

    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }

    /// <summary>Drives which of our own derived shades read correctly.</summary>
    public bool IsDark { get; init; } = true;

    /// <summary>The desktop's icon theme name, for when icons are wired up.</summary>
    public string? IconTheme { get; init; }
}

public interface IThemeProvider
{
    /// <summary>Null when the desktop exposes no scheme we can read.</summary>
    ThemePalette? Read();

    /// <summary>Raised when the desktop's scheme changes, so the UI can repaint.</summary>
    event EventHandler? Changed;
}
