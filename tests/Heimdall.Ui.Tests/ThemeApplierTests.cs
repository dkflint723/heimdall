using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Heimdall.Core;
using Heimdall.Core.Settings;
using Heimdall.Ui.Settings;
using Xunit;

namespace Heimdall.Ui.Tests;

/// <summary>
/// Which font actually reaches the screen.
///
/// **This is the test that did not exist when the font setting shipped
/// broken.** Choosing a font in Settings did nothing for a release: Apply
/// resolved the configured family and then ApplyDesignScheme overwrote it three
/// lines later, so the list offered every installed family, accepted a choice,
/// saved it, and the window carried on in the reference typeface.
///
/// It went unnoticed because the diagnostic built to catch exactly this ran
/// BEFORE the overwrite and reported the value it had just written. Broken and
/// fixed builds printed identical logs; only a screenshot could separate them.
/// A test reads the dictionary, which is the thing the markup binds to, so it
/// cannot be fooled the same way.
/// </summary>
public class ThemeApplierTests : IDisposable
{
    private readonly SettingsState _original = AppSettings.Current;

    public void Dispose()
    {
        AppSettings.Apply(_original);
        GC.SuppressFinalize(this);
    }

    private static void Configure(string? font)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { CustomFontFamily = font },
        });

    /// <summary>A desktop that asks for something distinctive, so "followed the
    /// desktop" and "used the reference" cannot be confused.</summary>
    private static ThemePalette Desktop(string? font) => new()
    {
        Colours = new Dictionary<string, string>(),
        FontFamily = font,
        IsDark = true,
    };

    private static FontFamily? Applied(Window window)
        => (Avalonia.Application.Current?.Resources ?? window.Resources)["AppFontFamily"] as FontFamily;

    [AvaloniaFact]
    public void A_configured_font_survives_the_design_scheme()
    {
        Configure("Georgia");

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        Assert.Equal("Georgia", Applied(window)?.Name);
    }

    /// <summary>
    /// **The interface face is proportional; the mono face still exists for the
    /// columns that need it.** This assertion used to demand JetBrains Mono here
    /// and was right to, until the split: monospace everywhere cost roughly
    /// 15-20% of the room every label had, and three clipping faults traced back
    /// to it. Chrome is read, so it is proportional; sizes and dates are compared
    /// down a column, so they keep the mono face.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_configured_the_interface_face_is_proportional()
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        var applied = Applied(window)?.Name ?? "";

        Assert.DoesNotContain("JetBrains", applied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mono", applied, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the face the numeric columns bind to, which is deliberately NOT
    /// touched by the font setting: choosing a typeface for the interface is a
    /// preference, whereas a column of figures that stops lining up is a defect.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("Georgia")]
    public void The_column_face_is_monospaced_whatever_the_setting_says(string? configured)
    {
        Configure(configured);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        var mono = ((Avalonia.Application.Current?.Resources ?? window.Resources)["AppMonoFamily"]
            as FontFamily)?.Name ?? "";

        Assert.Contains("JetBrains", mono, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blank is not a choice. The settings list uses an empty string
    /// for "follow the desktop", and treating that as a family name would ask
    /// Avalonia to resolve a font called "".</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_choice_is_not_a_font(string? configured)
    {
        Configure(configured);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        // Blank means "no choice", which lands on the bundled interface face —
        // not on the desktop's, and not on a family literally named "".
        var applied = Applied(window)?.Name ?? "";

        Assert.NotEqual("Segoe UI", applied);
        Assert.Contains("Segoe UI Variable", applied, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **The desktop is ignored until it is asked for.** The bundled scheme is
    /// the base, so a machine with a bright Plasma theme still opens looking
    /// like Heimdall. This is the half that keeps the inversion from being a
    /// visible change for anyone who never opens Settings.
    /// </summary>
    [AvaloniaFact]
    public void The_desktop_palette_is_ignored_by_default()
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, Loud());

        Assert.Equal("#ff23232b", Colour(window, "ViewBackground"));
    }

    /// <summary>
    /// And the half that makes it worth doing: with the flag on, the desktop's
    /// colours reach the screen. Every one of these was computed and thrown
    /// away before — the reader ran, the derivations ran, and the reference
    /// scheme overwrote all of it.
    /// </summary>
    [AvaloniaFact]
    public void With_the_flag_on_the_desktop_palette_wins()
    {
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { FollowDesktopColours = true },
        });

        var window = new Window();
        ThemeApplier.Apply(window, Loud());

        Assert.Equal("#ff102030", Colour(window, "ViewBackground"));
        Assert.Equal("#ffddeeff", Colour(window, "ViewText"));
    }

    /// <summary>A palette nothing could be mistaken for.</summary>
    private static ThemePalette Loud() => new()
    {
        Colours = new Dictionary<string, string>
        {
            [ThemeRole.ViewBackground] = "#102030",
            [ThemeRole.ViewText] = "#DDEEFF",
            [ThemeRole.WindowBackground] = "#203040",
            [ThemeRole.WindowText] = "#DDEEFF",
        },
        FontFamily = "Segoe UI",
        IsDark = true,
    };

    private static string? Colour(Window window, string key)
        => ((Avalonia.Application.Current?.Resources ?? window.Resources)[key] as ISolidColorBrush)
            ?.Color.ToString()?.ToLowerInvariant();

    /// <summary>
    /// Apply runs on every palette read — startup, a desktop theme change and a
    /// settings save all reach it — so it has to be idempotent. A previous
    /// ordering bug in this method was only visible on the second call.
    /// </summary>
    [AvaloniaFact]
    public void Applying_twice_gives_the_same_answer()
    {
        Configure("Georgia");

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));
        var first = Applied(window)?.Name;

        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        Assert.Equal(first, Applied(window)?.Name);
        Assert.Equal("Georgia", Applied(window)?.Name);
    }
}
