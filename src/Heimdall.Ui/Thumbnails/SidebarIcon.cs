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
        ["home"] =
            "M3 10.5 L12 3.5 L21 10.5 M5.5 9.5 V20.5 H18.5 V9.5 M10 20.5 V14.5 H14 V20.5",

        ["desktop"] =
            "M3.5 5 H20.5 V16 H3.5 Z M9 20 H15 M12 16 V20",

        ["download"] =
            "M12 4 V14.5 M8 10.5 L12 14.5 L16 10.5 M4.5 18.5 H19.5",

        ["file-text"] =
            "M4 3 H14.5 L20 8.5 V21 H4 Z M14.5 3 V8.5 H20 M7.5 13 H16.5 M7.5 16.5 H16.5",

        ["photo"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M3.5 15 L8.5 10 L13 14.5 M13.5 13 L16.5 10 L20.5 14",

        ["music"] =
            "M9.5 17 V4.5 L21 2.5 V14.5 "
            + "M3 17 a3.25 3.25 0 1 0 6.5 0 a3.25 3.25 0 1 0 -6.5 0 "
            + "M14.5 14.5 a3.25 3.25 0 1 0 6.5 0 a3.25 3.25 0 1 0 -6.5 0",

        ["video"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M7.5 5.5 V18.5 M16.5 5.5 V18.5 "
            + "M3.5 12 H7.5 M16.5 12 H20.5",

        ["trash"] =
            "M4.5 6.5 H19.5 M9.5 6.5 V4.5 H14.5 V6.5 M6.5 6.5 V20 H17.5 V6.5 "
            + "M10 10 V16.5 M14 10 V16.5",

        ["bookmark"] =
            "M4.5 3.5 H19.5 V20.5 L12 15 L4.5 20.5 Z",

        ["server"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M3.5 12 H20.5 "
            + "M12 3.5 c-3 3 -3 14 0 17 M12 3.5 c3 3 3 14 0 17",

        ["usb"] =
            "M9 3 H15 V8 H9 Z M12 8 V21 M4 13 H20",

        // Drawn to FILL the canvas vertically, not just wide enough to read.
        // The first version was 17 x 7 — 41% of the box — and because
        // Stretch="Uniform" centres the ink, bottom-aligning the box left five
        // empty pixels underneath and the icon appeared to float above its row.
        // Ink height is what aligns, not the control's height.
        ["device-desktop"] =
            "M3.5 4.5 H20.5 V19.5 H3.5 Z "
            + "M13 12 a3.2 3.2 0 1 0 6.4 0 a3.2 3.2 0 1 0 -6.4 0 "
            + "M6.5 8 H9.5 M6.5 16 H9.5",

        ["recent-files"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M12 7 V12 L15.5 14.5",

        ["recent-locations"] =
            "M3.5 6 H9.5 L11 8 H20.5 V18.5 H3.5 Z "
            + "M13 14 a3.2 3.2 0 1 0 6.4 0 a3.2 3.2 0 1 0 -6.4 0 M16.2 12.2 V14 L17.6 15.2",
    };
}
