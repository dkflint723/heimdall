using Avalonia;
using Avalonia.Media;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// The drawn file-type icons: a page or a folder in the category's colour, with
/// light falling across its face.
///
/// **Drawn rather than resolved, and the same on both platforms.** Windows used
/// to draw exactly two glyphs — a folder and a generic page — so three unrelated
/// files were three identical grey rectangles, while Linux resolved through
/// KDE's icon theme and looked like something else entirely. Neither is wrong on
/// its own; together they meant the application had no consistent appearance.
///
/// The construction is the folder's, generalised. That folder was already three
/// tones — a body, a lit flap edge and a seam — which is what makes it read as
/// an object rather than a symbol, and every icon here is built the same way:
/// a body carrying a soft vertical gradient, a brighter facet where the corner
/// turns back, and the mark knocked through in that same light.
///
/// **The tones have to be far apart.** A fold two shades from its body is
/// invisible at the eighteen pixels a listing row actually gives it. That is why
/// these read bolder at large sizes than a flat set would — the large size is
/// paying for the small one.
/// </summary>
public static class FileTypeIcon
{
    /// <summary>
    /// One hue per category, and the reason they are literals rather than
    /// derived from the accent: a palette generated from one colour lands
    /// everything in the same narrow band, which is the opposite of what a
    /// category colour is for. These are spread deliberately, and chosen to stay
    /// distinguishable for the commonest forms of colour blindness — the same
    /// concern the VCS marks answer by putting the meaning in a letter.
    /// </summary>
    private static readonly Dictionary<FileCategory, Color> Hues = new()
    {
        [FileCategory.Folder] = Color.Parse("#5457dd"),
        [FileCategory.Generic] = Color.Parse("#8b8b95"),
        [FileCategory.Text] = Color.Parse("#8b8b95"),
        [FileCategory.Code] = Color.Parse("#6d6df0"),
        [FileCategory.Image] = Color.Parse("#5bb98c"),
        [FileCategory.Audio] = Color.Parse("#d2a24c"),
        [FileCategory.Video] = Color.Parse("#e07a5f"),
        [FileCategory.Archive] = Color.Parse("#b07fd9"),
        [FileCategory.Document] = Color.Parse("#e05f5f"),
        [FileCategory.Spreadsheet] = Color.Parse("#4fa86f"),
        [FileCategory.Presentation] = Color.Parse("#e08a3c"),
        [FileCategory.Executable] = Color.Parse("#7fb8d9"),
        [FileCategory.Font] = Color.Parse("#c77fb0"),
        [FileCategory.DiskImage] = Color.Parse("#6fa8b8"),
        [FileCategory.Config] = Color.Parse("#9a9aa3"),
        [FileCategory.Key] = Color.Parse("#c9a227"),
        [FileCategory.Database] = Color.Parse("#7f8fd9"),
    };

    // A page whose corner is genuinely turned back: the body stops short of the
    // corner and the fold is a separate facet over it. Drawn as an L-shape with
    // the notch cut out, it reads as a line rather than as paper.
    private const string PageBody = "M5.25 2.5 H13.75 L18.75 7.5 V21.5 H5.25 Z";
    private const string PageFold = "M13.75 2.5 L18.75 7.5 H13.75 Z";

    // A back panel with a front pocket over it, so the folder has a mouth
    // rather than a lid.
    private const string FolderBack = "M2.5 5.5 H9 L11 8 H21.5 V19.75 H2.5 Z";
    private const string FolderFront = "M2.5 10 H21.5 V19.75 H2.5 Z";

    /// <summary>
    /// The interior mark per category, drawn in the light tone over the body.
    /// Three strokes at most: a fourth is a smudge at listing size, which is the
    /// same limit the sidebar set works to.
    /// </summary>
    private static readonly Dictionary<FileCategory, string> Marks = new()
    {
        [FileCategory.Text] = "M8 12.5 H16 M8 15.5 H16 M8 18.5 H13",
        [FileCategory.Code] = "M10 12.75 L7.75 15.75 L10 18.75 M14 12.75 L16.25 15.75 L14 18.75",
        [FileCategory.Image] = "M7.25 19 L10.75 14.5 L13 17 L15.25 14.75 L16.75 19 Z",
        [FileCategory.Audio] = "M11.25 18.25 V12 L16.25 10.75 V17",
        [FileCategory.Video] = "M9.75 11.75 L16.25 15.5 L9.75 19.25 Z",
        [FileCategory.Archive] = "M11 10 H13 M11 12.5 H13 M11 15 H13 M10.75 17.5 H13.25 V20 H10.75 Z",
        [FileCategory.Document] = "M8.25 12.5 H15.5 M8.25 15.5 H15.5 M8.25 18.5 H12",
        [FileCategory.Spreadsheet] = "M7.75 12.5 H16.25 M7.75 16 H16.25 M12 12.5 V19.5",
        [FileCategory.Presentation] = "M7.75 12 H16.25 V17 H7.75 Z M12 17 V19.75",
        [FileCategory.Executable] = "M8.75 12.75 L11.5 15.75 L8.75 18.75 M12.75 18.75 H16",
        [FileCategory.Font] = "M9 19 L12 11.5 L15 19 M10.25 16.5 H13.75",
        [FileCategory.DiskImage] = "M8.5 15.75 a3.5 3.5 0 1 0 7 0 a3.5 3.5 0 1 0 -7 0 M11.4 15.75 a0.6 0.6 0 1 0 1.2 0 a0.6 0.6 0 1 0 -1.2 0",
        [FileCategory.Config] = "M8.25 13.5 H15.75 M8.25 16.5 H15.75 M10.5 11.75 V19.5 M13.5 11.75 V19.5",
        [FileCategory.Key] = "M9.5 17.5 a2.4 2.4 0 1 0 4.8 0 a2.4 2.4 0 1 0 -4.8 0 M13.4 15.9 L17 12.25 M15.4 13.9 L16.6 15.1",
        [FileCategory.Database] = "M8.5 12.75 a3.5 1.5 0 1 0 7 0 a3.5 1.5 0 1 0 -7 0 M8.5 12.75 V18.5 a3.5 1.5 0 0 0 7 0 V12.75 M8.5 15.6 a3.5 1.5 0 0 0 7 0",
    };

    /// <summary>Marks that are a shape rather than a line, and want filling.</summary>
    private static readonly HashSet<FileCategory> FilledMarks =
        [FileCategory.Video, FileCategory.Image];

    /// <summary>
    /// **A folder with something in it has a page standing out of the top.**
    /// The plain folder stays exactly as it was and now means empty, because
    /// the ordinary case should be the ordinary drawing — the variant is the
    /// one that earns the extra ink.
    ///
    /// It is <see cref="PageBody"/> and <see cref="PageFold"/> again, shorter
    /// and lifted, which is the point: a folder should be drawn holding the
    /// same object the file icons are, not a generic rectangle. The turned
    /// corner is what makes it a page rather than a card, and it is the one
    /// detail that still resolves when the whole icon is eighteen pixels tall —
    /// so the sheet is deliberately wide enough to give the fold room.
    ///
    /// The bottom runs past the pocket line to y=11 so the front panel, drawn
    /// after it, covers the join.
    /// </summary>
    private const string FolderSheet = "M6.5 4.25 H15 L19 8.25 V11 H6.5 Z";
    private const string FolderSheetFold = "M15 4.25 L19 8.25 H15 Z";

    private static readonly Dictionary<(FileCategory, bool), IImage> Cache = new();

    /// <summary>Drops every drawing. Called when the palette changes.</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>
    /// The icon for one file. **UI thread only** — this builds Avalonia
    /// drawings and brushes, which is the same constraint IconLoader.Load
    /// carries and for the same reason.
    /// </summary>
    public static IImage For(string name, bool isDirectory, bool hasContents = false)
        => Draw(FileCategories.For(name, isDirectory), hasContents);

    private static IImage Draw(FileCategory category, bool full)
    {
        if (Cache.TryGetValue((category, full), out var cached)) return cached;

        var hue = Hues.TryGetValue(category, out var found) ? found : Hues[FileCategory.Generic];
        var light = Lighten(hue, 0.55);

        var group = new DrawingGroup();

        if (category == FileCategory.Folder)
        {
            // The back panel is the shaded one, so the pocket in front of it
            // reads as nearer rather than as a second flat rectangle. The page
            // goes between the two, which is what makes it look held rather
            // than stuck on.
            group.Children.Add(Fill(FolderBack, new SolidColorBrush(Darken(hue, 0.42))));

            if (full)
            {
                group.Children.Add(Fill(FolderSheet, new SolidColorBrush(Lighten(hue, 0.74))));
                group.Children.Add(Fill(FolderSheetFold, new SolidColorBrush(Lighten(hue, 0.84))));
            }

            group.Children.Add(Fill(FolderFront, Graded(hue, 10, 19.75)));
            group.Children.Add(Stroke("M2.5 10 H21.5", light, 1.2));
        }
        else
        {
            group.Children.Add(Fill(PageBody, Graded(hue, 2.5, 21.5)));
            group.Children.Add(Fill(PageFold, new SolidColorBrush(light)));

            if (Marks.TryGetValue(category, out var mark))
            {
                group.Children.Add(FilledMarks.Contains(category)
                    ? Fill(mark, new SolidColorBrush(light))
                    : Stroke(mark, light, 1.6));
            }
        }

        var image = new DrawingImage
        {
            Drawing = new DrawingGroup
            {
                Children = { group },

                // Clipped to the grid the paths are drawn on, so every icon
                // occupies the same box whatever its ink happens to span —
                // otherwise a narrow glyph is scaled up beside a wide one and
                // the listing looks ragged.
                ClipGeometry = new RectangleGeometry(new Rect(0, 0, 24, 24)),
            },
        };

        Cache[(category, full)] = image;
        return image;
    }

    /// <summary>
    /// Light falling down the face. The stops are close together on purpose:
    /// a gradient wide enough to admire at 48px is a muddy average at 18, and
    /// the listing is where most of these are seen.
    /// </summary>
    private static IBrush Graded(Color hue, double top, double bottom) =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, top / 24.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, bottom / 24.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Lighten(hue, 0.22), 0),
                new GradientStop(Darken(hue, 0.22), 1),
            },
        };

    private static GeometryDrawing Fill(string path, IBrush brush) =>
        new() { Geometry = Geometry.Parse(path), Brush = brush };

    private static GeometryDrawing Stroke(string path, Color colour, double width) =>
        new()
        {
            Geometry = Geometry.Parse(path),
            Pen = new Pen(new SolidColorBrush(colour), width)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            },
        };

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}
