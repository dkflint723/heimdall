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
    /// The other half of the rule, and the reason this is an exception inside
    /// ApplyDesignScheme rather than a reordering: with nothing configured, the
    /// reference typeface must still win over the desktop's. Deleting the
    /// override entirely would pass the test above and change how the
    /// application looks for everyone who never opened Settings.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_configured_the_reference_typeface_wins()
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        var applied = Applied(window)?.Name;

        Assert.NotEqual("Segoe UI", applied);
        Assert.Contains("JetBrains", applied ?? "", StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("JetBrains", Applied(window)?.Name ?? "", StringComparison.OrdinalIgnoreCase);
    }

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
