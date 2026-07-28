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
            "M6.5 3.5 H14 L17.5 7 V20.5 H6.5 Z M14 3.5 V7 H17.5 M9.5 12 H14.5 M9.5 15.5 H14.5",

        ["photo"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M3.5 15 L8.5 10 L13 14.5 M13.5 13 L16.5 10 L20.5 14",

        ["music"] =
            "M11.5 17.5 V5.5 L20 4 V15.5 "
            + "M7.5 17.5 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 "
            + "M16 15.5 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0",

        ["video"] =
            "M3.5 5.5 H20.5 V18.5 H3.5 Z M7.5 5.5 V18.5 M16.5 5.5 V18.5 "
            + "M3.5 12 H7.5 M16.5 12 H20.5",

        ["trash"] =
            "M4.5 6.5 H19.5 M9.5 6.5 V4.5 H14.5 V6.5 M6.5 6.5 V20 H17.5 V6.5 "
            + "M10 10 V16.5 M14 10 V16.5",

        ["bookmark"] =
            "M7 3.5 H17 V20.5 L12 16.5 L7 20.5 Z",

        ["server"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M3.5 12 H20.5 "
            + "M12 3.5 c-3 3 -3 14 0 17 M12 3.5 c3 3 3 14 0 17",

        ["usb"] =
            "M9.5 4.5 H14.5 V9 H9.5 Z M12 9 V20.5 M8.5 13.5 H15.5",

        ["device-desktop"] =
            "M3.5 8.5 H20.5 V15.5 H3.5 Z M6.5 12 H9 M16.5 12 H18",

        ["recent-files"] =
            "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 M12 7 V12 L15.5 14.5",

        ["recent-locations"] =
            "M3.5 6 H9.5 L11 8 H20.5 V18.5 H3.5 Z "
            + "M13 14 a3.2 3.2 0 1 0 6.4 0 a3.2 3.2 0 1 0 -6.4 0 M16.2 12.2 V14 L17.6 15.2",
    };
}
