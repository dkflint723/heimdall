using Avalonia.Headless.XUnit;
using Heimdall.Ui.Thumbnails;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Heimdall.Ui.Tests;

/// <summary>
/// Every icon the places providers can ask for resolves to real geometry.
///
/// **A broken icon is silent.** SidebarIcon draws nothing for a token it does
/// not recognise — deliberately, so an unmapped name cannot render something
/// wrong — which means a typo in a path, or a token renamed on one side only,
/// produces a blank space rather than an error. Nothing in a build or a test
/// run would have said so.
///
/// That matters more than usual here: this set was redrawn wholesale, and while
/// redrawing it one path was written with a duplicated segment
/// (<c>V20 H17.5 V20 V6.75</c>) that happened to still parse. The next one might
/// not.
/// </summary>
public class SidebarIconTests
{
    /// <summary>
    /// Every token emitted by LinuxPlacesProvider or WindowsPlacesProvider, plus
    /// the two the virtual listings use. Hard-coded rather than read back from
    /// the dictionary on purpose: reading it would only prove the set is
    /// self-consistent, where the risk is that a provider asks for a name the
    /// set no longer has.
    /// </summary>
    public static TheoryData<string> Tokens =>
    [
        "home", "desktop", "download", "file-text", "photo", "music", "video",
        "trash", "bookmark", "server", "usb", "device-desktop",
        "recent-files", "recent-locations",
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Tokens))]
    public void Every_token_a_provider_emits_draws_something(string token)
    {
        var shape = new Path();

        SidebarIcon.SetToken(shape, token);

        Assert.NotNull(shape.Data);
        Assert.True(shape.Data!.Bounds.Width > 0 && shape.Data.Bounds.Height > 0,
            $"'{token}' parsed to an empty geometry");
    }

    // **There was an optical-box test here, measuring that every icon fills the
    // same vertical extent. It is gone because it could not measure that.**
    //
    // Avalonia's Geometry.Bounds ignores the extent of ARC segments. The proof
    // came from its own failure output: `recent-files` is a circle drawn from
    // two arcs spanning y 3.5-20.5, with clock hands from y 7-14.5, and Bounds
    // reported a height of exactly 7.5 — the hands alone. `server` passed only
    // because its lens is drawn with cubic beziers, which DO contribute.
    //
    // So the test reported a real fault in `usb` and an invented one in
    // `recent-files`, and would have silently passed any arc-heavy icon drawn at
    // the wrong scale. A measurement that is wrong in both directions is worse
    // than none: it would have been trusted.
    //
    // Measuring this properly means flattening the geometry to a polyline and
    // taking the extent of that, which is real work for a rule better enforced
    // by looking at the icons side by side — which is how the `usb` fault was
    // confirmed and fixed.

    /// <summary>An unmapped name draws nothing rather than something wrong —
    /// the same rule the SVG renderer follows for shapes it declines.</summary>
    [AvaloniaTheory]
    [InlineData("no-such-icon")]
    [InlineData("")]
    [InlineData("HOME")]
    public void An_unknown_token_draws_nothing(string token)
    {
        var shape = new Path();

        SidebarIcon.SetToken(shape, token);

        Assert.Null(shape.Data);
    }
}
