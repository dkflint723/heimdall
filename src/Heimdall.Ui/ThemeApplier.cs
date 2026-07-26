using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Heimdall.Core;

namespace Heimdall.Ui;

/// <summary>
/// Turns a desktop palette into Avalonia resources.
///
/// Every colour in the markup is a DynamicResource pointing at one of these, so
/// adopting the system scheme is a lookup-table swap rather than a sweep
/// through the XAML — and the accessibility rules survive it, because none of
/// them ever depended on a particular hue. Selection is still marked by an edge
/// bar, tags still carry their names, and file age is still a lightness ramp.
/// </summary>
public static class ThemeApplier
{
    /// <summary>Our own scheme, used when no desktop theme can be read.</summary>
    private static readonly (string Key, string Dark, string Light)[] Fallback =
    [
        ("AppBackground",       "#181818", "#F0F0F0"),
        ("AppText",             "#E8E8E8", "#1C1C1C"),
        ("ViewBackground",      "#0F0F0F", "#FFFFFF"),
        ("ViewAlternate",       "#151515", "#F7F7F7"),
        ("ViewText",            "#E8E8E8", "#1C1C1C"),
        ("ViewDimText",         "#8A8A8A", "#6A6A6A"),
        ("SelectionBackground", "#1E3A4C", "#CFE6F5"),
        ("SelectionText",       "#FFFFFF", "#101010"),
        ("AccentColour",        "#56B4E9", "#0072B2"),
        ("BorderColour",        "#242424", "#D4D4D4"),
        ("SeparatorColour",     "#242424", "#DCDCDC"),
        ("DividerColour",       "#303030", "#C8C8C8"),
        ("PanelBackground",     "#1B1B1B", "#EDEDED"),
        ("HoverBackground",     "#1A2A34", "#E8F1F8"),
        ("EdgeHighlight",       "#16FFFFFF", "#96FFFFFF"),
        ("EdgeShadow",          "#5A000000", "#28000000"),
        ("ChipBackground",      "#22FFFFFF", "#18000000"),
    ];

    private static readonly (string Resource, string Role)[] Mapping =
    [
        ("AppBackground",       ThemeRole.WindowBackground),
        ("AppText",             ThemeRole.WindowText),
        ("ViewBackground",      ThemeRole.ViewBackground),
        ("ViewAlternate",       ThemeRole.ViewAlternate),
        ("ViewText",            ThemeRole.ViewText),
        ("ViewDimText",         ThemeRole.ViewDimText),
        ("SelectionBackground", ThemeRole.SelectionBackground),
        ("SelectionText",       ThemeRole.SelectionText),
        ("AccentColour",        ThemeRole.Accent),

    ];

    public static void Apply(Window window, ThemePalette? palette)
    {
        // Application-scoped so every window — including properties — resolves
        // the same palette. Window-scoped resources are invisible to siblings.
        var target = Application.Current?.Resources ?? window.Resources;

        var dark = palette?.IsDark ?? true;

        // Start from our own scheme so any role the desktop omits still has a
        // sane value, then overlay whatever it did provide.
        foreach (var (key, darkValue, lightValue) in Fallback)
            target[key] = Brush(dark ? darkValue : lightValue);

        if (palette is not null)
        {
            foreach (var (resource, role) in Mapping)
            {
                if (palette.Colours.TryGetValue(role, out var hex) && Brush(hex) is { } brush)
                    target[resource] = brush;
            }
        }

        if (target["AccentColour"] is ISolidColorBrush accent)
        {
            // A dimmed accent for selection fills. No desktop exposes "the
            // accent at 25%", and a flat accent behind text is far too loud for
            // a whole row.
            target["AccentDim"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)70 : (byte)55,
                    accent.Color.R, accent.Color.G, accent.Color.B));

            target["HoverBackground"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)28 : (byte)24,
                    accent.Color.R, accent.Color.G, accent.Color.B));
        }

        // Separators are DERIVED from the background, not taken from a text
        // role. ForegroundInactive is a foreground colour — using it draws hard
        // grey rules through the window, which is nothing like how Breeze
        // separates regions. Blending the background a little way toward the
        // text gives a line that reads as a seam at any scheme lightness.
        if (target["AppBackground"] is ISolidColorBrush back &&
            target["AppText"] is ISolidColorBrush fore)
        {
            target["SeparatorColour"] = new SolidColorBrush(
                Blend(back.Color, fore.Color, dark ? 0.10 : 0.14));

            // A slightly stronger version for the one edge that has to read as
            // a real boundary: the split divider.
            target["DividerColour"] = new SolidColorBrush(
                Blend(back.Color, fore.Color, dark ? 0.18 : 0.22));

            // The sidebar sits *under* the window chrome rather than beside it,
            // so it steps away from the light instead of toward it. A 3% blend
            // toward the text was invisible and left the whole window one sheet.
            // Breeze Dark's alternate row colour sits a couple of values away
            // from its view colour, which vanishes on a large monitor. Keep the
            // desktop's value only when it is far enough to read; otherwise
            // derive a band we know will show.
            if (target["ViewBackground"] is ISolidColorBrush view)
            {
                var alt = (target["ViewAlternate"] as ISolidColorBrush)?.Color ?? view.Color;

                var distance = Math.Abs(alt.R - view.Color.R)
                             + Math.Abs(alt.G - view.Color.G)
                             + Math.Abs(alt.B - view.Color.B);

                if (distance < 12)
                {
                    target["ViewAlternate"] = new SolidColorBrush(
                        dark ? Lighten(view.Color, 0.045) : Darken(view.Color, 0.035));
                }
            }

            target["PanelBackground"] = new SolidColorBrush(
                dark ? Darken(back.Color, 0.22) : Darken(back.Color, 0.05));

            // Bevels. One pixel lighter along a top edge and one pixel darker
            // along the bottom is what makes a band read as a raised surface —
            // it is the cheapest depth cue there is and needs no shadows.
            target["EdgeHighlight"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)22 : (byte)150, 255, 255, 255));

            target["EdgeShadow"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)90 : (byte)40, 0, 0, 0));

            // A shallow vertical gradient on the chrome bands. Flat fills are
            // what read as "flat"; two stops a few percent apart are enough to
            // suggest a surface catching light without looking glossy.
            target["ChromeBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Lighten(back.Color, dark ? 0.06 : 0.03), 0),
                    new GradientStop(Darken(back.Color, dark ? 0.05 : 0.02), 1),
                },
            };
        }

        // The file-age ramp is derived here rather than hardcoded: fixed pale
        // blues disappear on a light scheme. Fresh files get full text colour,
        // ancient ones fade past the dim colour — a lightness ramp that holds
        // under any desktop theme.
        if (target["ViewText"] is ISolidColorBrush text &&
            target["ViewDimText"] is ISolidColorBrush dim)
        {
            var ramp = new IBrush[6];
            for (var i = 0; i < 6; i++)
            {
                // Past the dim colour at the far end, so "ancient" recedes
                // further than ordinary secondary text.
                var t = i / 5.0 * 1.25;
                ramp[i] = new SolidColorBrush(Blend(text.Color, dim.Color, Math.Min(t, 1.0)));
            }

            ViewModels.AgeConverters.SetRamp(ramp);
        }

        // A tag chip background that works on both light and dark: a wash of
        // the view text colour rather than a fixed translucent white, which is
        // invisible on a pale scheme.
        if (target["ViewText"] is ISolidColorBrush chipText)
        {
            target["ChipBackground"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)26 : (byte)20,
                    chipText.Color.R, chipText.Color.G, chipText.Color.B));
        }

        // Always set, so the markup can bind unconditionally.
        target["AppFontFamily"] = palette?.FontFamily is { Length: > 0 } family
            ? new FontFamily(family)
            : FontFamily.Default;
    }

    private static Color Lighten(Color c, double amount) => Blend(c, Colors.White, amount);
    private static Color Darken(Color c, double amount) => Blend(c, Colors.Black, amount);

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static IBrush? Brush(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return null; }
    }
}
