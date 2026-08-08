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

        // A sane value for every role, so nothing downstream is ever unset.
        foreach (var (key, darkValue, lightValue) in Fallback)
            target[key] = Brush(dark ? darkValue : lightValue);

        // **The reference scheme is the BASE now, not the last word.**
        //
        // It used to run last and overwrite everything: of the seventeen
        // resources derived from the desktop palette below, thirteen were
        // replaced outright and the four survivors are used by no markup in the
        // window. So this file read the desktop's colours, spent a hundred lines
        // deriving separators, bands, bevels and an age ramp from them, and threw
        // all of it away — leaving IThemeProvider, KdeThemeProvider and
        // WindowsThemeProvider, some five hundred lines between them, delivering
        // exactly one live value: palette.SingleClick.
        //
        // Applying it first inverts that without changing how anything looks.
        // The default is unchanged, because the default is still this scheme.
        // What changes is that the desktop can now be layered ON TOP when asked,
        // which is what all that derivation was written for.
        ApplyDesignScheme(target);

        // The desktop gets a say only when the user asks for one. Off by
        // default: the scheme above is a considered look, and a file manager
        // that repaints itself to match Plasma the first time it is launched is
        // a surprise, not a feature.
        if (!Settings.AppSettings.Current.Views.FollowDesktopColours || palette is null)
        {
            Finish(target, palette);
            return;
        }

        foreach (var (resource, role) in Mapping)
        {
            if (palette.Colours.TryGetValue(role, out var hex) && Brush(hex) is { } brush)
                target[resource] = brush;
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

            // Bevels. Kept in the table because nothing else derives from them
            // and a future bevel may want them, but **no longer used as region
            // borders** — every band boundary is a SeparatorColour hairline now.
            // With both present each band had two edges, a light one from here
            // and a dark one from EdgeShadow, which is one more than a boundary
            // needs.
            target["EdgeHighlight"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)22 : (byte)150, 255, 255, 255));

            target["EdgeShadow"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)90 : (byte)40, 0, 0, 0));

            // Flat, where this used to be a two-stop vertical gradient. The
            // gradient and the bevel pair were doing the same job as the seams,
            // and a flat fill survives an arbitrary desktop scheme better than a
            // derived ±5% ramp does — on a scheme already near black or white
            // the ramp clips and the "surface" reads as a smudge.
            target["ChromeBrush"] = new SolidColorBrush(back.Color);
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

        Finish(target, palette);
    }

    /// <summary>
    /// The parts that must run whichever scheme won, in the order they depend
    /// on each other.
    ///
    /// Both paths through <see cref="Apply"/> end here, which is the point: the
    /// age ramp has to be derived from the colours that FINALLY landed, and the
    /// trace has to report the font that finally landed. Getting either from a
    /// value written earlier is exactly the bug that let a broken font setting
    /// ship — the log named the chosen font while the window rendered another.
    /// </summary>
    private static void Finish(IResourceDictionary target, ThemePalette? palette)
    {
        // The file-age ramp is derived rather than hardcoded: fixed pale blues
        // disappear on a light scheme. Fresh files get full text colour,
        // ancient ones fade past the dim colour — a lightness ramp that holds
        // under any scheme, this one or a desktop's.
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

        // Always set, so the markup can bind unconditionally. A configured font
        // wins over the desktop's, which is the whole point of configuring one;
        // blank means follow Plasma, which stays the default.
        // Published here rather than at each call site: Apply is the one place
        // every palette read funnels through — startup, a Plasma change, and a
        // settings save all reach it — so this cannot fall out of step.
        MainWindow.SystemSingleClick = palette?.SingleClick;

        // Precedence, most specific first. ApplyDesignScheme has already put the
        // reference typeface in, so the last arm is "leave it alone" rather than
        // a value — which is why this reads as two overrides and not a chain.
        var chosen = Settings.AppSettings.Current.Views.CustomFontFamily;

        if (chosen is { Length: > 0 })
        {
            target["AppFontFamily"] = new FontFamily(chosen);
        }
        else if (Settings.AppSettings.Current.Views.FollowDesktopColours
                 && palette?.FontFamily is { Length: > 0 } family)
        {
            target["AppFontFamily"] = new FontFamily(family);
        }

        // Logged from the dictionary, after everything that writes to it. This
        // line used to run before the design scheme and report the value it had
        // just written — which was then replaced — so it printed
        // applied='Segoe UI' while the window was unmistakably in JetBrains
        // Mono. A trace added to make a font problem visible spent its life
        // describing a value nothing ever rendered.
        //
        // A diagnostic that reports an intention rather than an outcome is
        // worse than none: it answers the question convincingly and wrongly.
        Console.Error.WriteLine(
            $"[heimdall] font: configured='{chosen ?? "(none)"}' "
            + $"desktop='{palette?.FontFamily ?? "(none)"}' "
            + $"applied='{target["AppFontFamily"]}'");
    }

    /// <summary>
    /// **The design reference's own palette and typeface, applied verbatim,
    /// last, over everything the desktop said.**
    ///
    /// This is a deliberate reversal of the rule the rest of this file exists
    /// to enforce. Everything above derives its colours from the desktop scheme
    /// so the window looks like part of it; the handoff says the mock's hex
    /// values are "reference values only" for exactly that reason. Requested
    /// anyway, and requested twice: a 1:1 match with
    /// `Heimdall Window.dc.html`, which cannot be had while the desktop still
    /// gets a vote.
    ///
    /// **What this costs**, so it is not discovered later: the window no longer
    /// follows the desktop's colour scheme, accent or font, and it does not
    /// follow a light scheme at all — these values are the mock's dark one. The
    /// live re-theming still runs, it just gets overwritten here.
    ///
    /// **To revert**, delete the call above. Nothing else references this.
    /// </summary>
    private static void ApplyDesignScheme(IResourceDictionary target)
    {
        static SolidColorBrush B(string hex) => new(Color.Parse(hex));

        // Surfaces, from the mock's own markup: window and chrome #2b2b32,
        // sidebar #26262d, listing and the active tab #23232b.
        target["AppBackground"] = B("#2b2b32");
        target["ChromeBrush"] = B("#2b2b32");
        target["PanelBackground"] = B("#26262d");
        target["ViewBackground"] = B("#23232b");
        target["ViewAlternate"] = B("#26262d");

        target["WindowText"] = B("#e7e7ec");
        target["ViewText"] = B("#e7e7ec");
        // #8b8b95 measured 4.45:1 against PanelBackground — five hundredths
        // under WCAG AA for body text, and this role carries the sidebar's group
        // headings and drive sizes. #909099 is 4.75:1 and is not a colour
        // anybody can tell apart from the old one. Against ViewBackground the
        // original already passed at 4.62:1, so this is the panel case only.
        target["ViewDimText"] = B("#909099");

        target["SeparatorColour"] = B("#34343c");
        target["BorderColour"] = B("#34343c");

        // The checked segment is rgba(109,109,240,.22) in the mock, which is
        // the tint AccentDim is bound to everywhere it is used.
        target["AccentColour"] = B("#6d6df0");
        target["AccentDim"] = B("#386d6df0");

        target["ChipBackground"] = B("#31313a");
        target["HoverBackground"] = B("#14ffffff");

        target["SelectionBackground"] = B("#4d6d6df0");
        target["SelectionText"] = B("#e7e7ec");

        // The mock sets 'JetBrains Mono'. What is installed here is the Nerd
        // Font packaging of the same typeface, so it is named first and the
        // plain name kept behind it for a machine that has that instead.
        //
        // **Skipped when the user picked a font, and that exception is the
        // reason this method takes an argument it otherwise would not need.**
        // Everything else here deliberately overrides the desktop — that is
        // what applying the reference verbatim means, and the desktop's colours
        // are a default rather than a decision. A font chosen in Settings is
        // not a default. It was being computed, logged, and then overwritten
        // three lines later, so the setting appeared to do nothing at all: the
        // list offered every installed family, accepted the choice, saved it,
        // and the window carried on in JetBrains Mono.
        //
        // The ordering that makes the rest of this correct is exactly what made
        // the font wrong, which is why it reads as an exception rather than a
        // reordering. Moving the whole block earlier would hand the desktop's
        // palette back its win over the reference.
        target["AppFontFamily"] =
            new FontFamily("JetBrainsMono NF, JetBrains Mono, Cascadia Mono, Consolas");

        // Re-derived from the new text colours. The ramp above was built from
        // the desktop's and would otherwise be left pointing at a palette that
        // is no longer on screen — it goes through AgeConverters rather than the
        // resource dictionary, so overwriting the brushes does not reach it.
        if (target["ViewText"] is ISolidColorBrush t && target["ViewDimText"] is ISolidColorBrush d)
        {
            var ramp = new IBrush[6];
            for (var i = 0; i < 6; i++)
                ramp[i] = new SolidColorBrush(
                    Blend(t.Color, d.Color, Math.Min(i / 5.0 * 1.25, 1.0)));

            ViewModels.AgeConverters.SetRamp(ramp);
        }
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
