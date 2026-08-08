using Avalonia;
using Avalonia.Media;

// `Path` is ambiguous in this project: implicit usings pull in System.IO, and
// the shape type has the same name. An alias rather than fully qualifying every
// use — there are five, and the file has nothing to do with file paths.
// Any new file that draws a shape will hit this.
using Path = Avalonia.Controls.Shapes.Path;

namespace Heimdall.Ui.Thumbnails;

/// <summary>
/// The sidebar's own outline icons, drawn rather than looked up.
///
/// **Deliberately NOT the desktop icon theme.** [stated] the user's requirement
/// is "simple icons like I showed you in the screenshot of dolphin" and
/// explicitly "I don't want to use built in icons from the OS". Resolving
/// `Place.Icon` through <c>IIconThemeProvider</c> was tried and produced
/// Tela-circle's filled blue discs — correct machinery, wrong look, and the
/// look was the requirement.
///
/// This also removes a dependency the sidebar had no business having: the same
/// twelve rows now render identically on a machine with no icon theme at all,
/// which is one less thing for the Windows port to solve.
///
/// **Stroke, not fill.** These are outline icons, so the geometry is open paths
/// and the colour comes from the caller's <c>Stroke</c> — bound to a theme brush
/// in markup, so it follows light/dark and the Plasma colour scheme without this
/// class knowing anything about colour. That is also why they are
/// <see cref="Path"/> and not a <c>DrawingImage</c>: a drawing built once would
/// hold whatever brush it was born with.
///
/// Drawn on a 24×24 grid and scaled by <c>Stretch="Uniform"</c>, so one set of
/// coordinates serves every icon scale.
/// </summary>
public static class SidebarIcon
{
    public static readonly AttachedProperty<string?> TokenProperty =
        AvaloniaProperty.RegisterAttached<Path, string?>("Token", typeof(SidebarIcon));

    static SidebarIcon()
    {
        TokenProperty.Changed.AddClassHandler<Path>((shape, _) =>
        {
            var token = shape.GetValue(TokenProperty);

            // An unmapped token draws nothing rather than something wrong —
            // the same rule the SVG renderer follows for shapes it declines.
            shape.Data = token is null || !Paths.TryGetValue(token, out var data)
                ? null
                : Geometry.Parse(data);
        });
    }

    public static void SetToken(Path shape, string? value) => shape.SetValue(TokenProperty, value);
    public static string? GetToken(Path shape) => shape.GetValue(TokenProperty);

    /// <summary>
    /// Keyed by the tokens <c>LinuxPlacesProvider</c> already emits, plus two
    /// for the virtual listings. A circle is two half arcs — the single-arc
    /// shorthand does not close.
    /// </summary>
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        // **Minimal: the fewest strokes that still name the thing.**
        //
        // Two rules the previous set did not hold, and both were visible in a
        // column rather than in any single icon.
        //
        // ONE OPTICAL BOX. Ink spans y 4-20 in every glyph here. Before, `photo`
        // filled 13 units and `bookmark` filled 17, so stacked in the sidebar
        // some read heavier than others for no reason anyone had chosen.
        //
        // NOTHING FLOATS. Interior strokes meet the shape they belong to.
        // `photo` used to draw its second ridge from (13.5,13) to (20.5,14) —
        // beginning and ending in mid-air, so it read as two loose marks rather
        // than a landscape.
        //
        // And the reason this set is the minimal one: at 16px, which is the size
        // the sidebar actually renders, a fourth interior stroke is a smudge.
        // That is what retires the beamed quaver, the film strip's four rules,
        // and the 3.2-unit clock that used to sit inside a folder.

        ["home"] =
            "M3.5 11.25 L12 4.5 L20.5 11.25 M6 10 V19.5 H18 V10",

        ["desktop"] =
            "M3.5 5 H20.5 V16.5 H3.5 Z M12 16.5 V19.5 M8.5 19.5 H15.5",

        ["download"] =
            "M12 4 V14.25 M7.75 10 L12 14.25 L16.25 10 M4 19.25 H20",

        // The fold stays. Without it a document is a rectangle, and there are
        // three other rectangles in this set.
        ["file-text"] =
            "M6 3.75 H14 L18.5 8.25 V20.25 H6 Z M14 3.75 V8.25 H18.5 M9 14 H15.5",

        ["photo"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M3.5 15.75 L9.25 10 L20.5 18",

        // One quaver. The stem and the short beam are what say "note" — a head
        // alone is a lollipop, and the second note added ink without meaning.
        ["music"] =
            "M10.5 17 V4.5 L17.5 6.5 "
            + "M4 17 a3.25 3.25 0 1 0 6.5 0 a3.25 3.25 0 1 0 -6.5 0",

        ["video"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M10 9.75 L15.5 12 L10 14.25 Z",

        // The two ribs are gone. A bin is a lid, a body and a handle.
        ["trash"] =
            "M4 6.75 H20 M9.75 6.75 V4.25 H14.25 V6.75 M6.5 6.75 V20 H17.5 V6.75",

        ["bookmark"] =
            "M6 4 H18 V20 L12 15.25 L6 20 Z",

        // Both meridians as ONE closed lens rather than two open curves: the
        // old pair met at the poles and doubled the stroke there, which at 16px
        // showed as a dark node top and bottom.
        ["server"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M3.5 12 H20.5 "
            + "M12 3.5 c3.6 2.9 3.6 14.1 0 17 c-3.6 -2.9 -3.6 -14.1 0 -17 Z",

        // Was a box sitting on a cross, which named nothing. Body and connector.
        ["usb"] =
            "M7.5 8.5 H16.5 V20.25 H7.5 Z M10 8.5 V4 H14 V8.5",

        // The lamp is the third element, and it earns its place: without it a
        // drive is a rectangle with a slot, and `desktop` two rows above is a
        // rectangle with a stand. Seen live in the sidebar the two silhouettes
        // read as the same object. Minimal is the fewest strokes that still name
        // the thing, and at two strokes this one had stopped naming it.
        ["device-desktop"] =
            "M3.5 7 H20.5 V17 H3.5 Z M6.5 14 H10 "
            + "M16.75 14 a1.25 1.25 0 1 0 2.5 0 a1.25 1.25 0 1 0 -2.5 0",

        ["recent-files"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M12 7 V12.25 L15.75 14.5",

        // The busiest glyph in the set, and unavoidably so — it has to say both
        // "folder" and "recently". The clock moves OUT of the folder and becomes
        // a badge on its corner at 1.4x its old size, which is the only way it
        // survives 16px. The folder is drawn open on the right so the badge sits
        // in a gap rather than on top of a line.
        ["recent-locations"] =
            "M3 6 H9 L10.75 8.25 H18.75 V11.75 M3 6 V18.75 H11.75 "
            + "M12.75 16.25 a4.5 4.5 0 1 0 9 0 a4.5 4.5 0 1 0 -9 0 "
            + "M17.25 13.9 V16.25 L19 17.4",
    };
}
