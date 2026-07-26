using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rove.Core.FileSystem;

namespace Rove.Ui.Thumbnails;

/// <summary>
/// Turns a themed icon file into something drawable.
///
/// PNGs decode normally. SVGs go through a deliberately small subset renderer:
/// Avalonia's Geometry.Parse already understands SVG path data, and it has real
/// gradient brushes, so shapes, flat fills and gradients need no SVG library.
/// Anything genuinely beyond that — filters, masks, clip paths, text, embedded
/// rasters — is declined rather than approximated, and the caller falls back to
/// the drawn glyph. A wrong icon is worse than a generic one.
/// </summary>
public static class IconLoader
{
    private const int MaxResolved = 4000;

    private static readonly ConcurrentDictionary<string, string?> Resolved = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IImage?> Drawn = new(StringComparer.Ordinal);

    public static IIconThemeProvider? Provider { get; set; }

    /// <summary>
    /// Paint we cannot reproduce faithfully. Gradients are NOT here: icon themes
    /// use them heavily — Tela and Papirus are almost entirely gradient — and
    /// declining them meant declining whole themes.
    /// </summary>
    private static readonly string[] Unsupported =
        ["filter", "mask", "clipPath", "text", "image", "pattern"];

    /// <summary>
    /// Which file represents this entry. Pure filesystem work and safe from any
    /// thread — deliberately separate from <see cref="Load"/>, which is not.
    /// </summary>
    public static string? ResolveFile(string path, bool isDirectory, int size)
    {
        if (Provider is null) return null;

        // Keyed by *type*, not by path. Every .png shares an icon, so a
        // path-keyed cache grew one entry per file ever browsed and never
        // evicted — and it re-resolved for each one.
        var key = CacheKey(path, isDirectory, size);

        if (Resolved.TryGetValue(key, out var cached)) return cached;

        string? file = null;

        try { file = Provider.Resolve(Provider.NamesFor(path, isDirectory), size); }
        catch { /* an unreadable theme means no icon, not a failure */ }

        // Extensionless files fall back to a per-path key because their type
        // depends on content, so bound the dictionary rather than let those
        // accumulate forever.
        if (Resolved.Count > MaxResolved) Resolved.Clear();

        Resolved[key] = file;
        return file;
    }

    private static string CacheKey(string path, bool isDirectory, int size)
    {
        if (isDirectory) return $"dir|{size}";

        var extension = Path.GetExtension(path);

        return extension.Length > 1
            ? $"ext{extension.ToLowerInvariant()}|{size}"
            : $"path{path}|{size}";
    }

    /// <summary>
    /// Drops every cached icon. Called when the desktop theme changes: the
    /// resolved paths belong to the old icon theme, and the drawables were
    /// built with the old text colour baked into every currentColor.
    /// </summary>
    public static void Invalidate()
    {
        Resolved.Clear();
        Drawn.Clear();
        Fallbacks.Clear();
    }

    /// <summary>
    /// Builds the drawable. **UI thread only.** Everything here creates Avalonia
    /// objects — DrawingImage, GeometryDrawing, brushes, GradientStops — and
    /// CurrentColour reads Application.Current.Resources. Doing this on a pool
    /// thread is what crashed the process: thumbnails get away with Task.Run
    /// because Bitmap is a plain object, and none of these are.
    /// </summary>
    public static IImage? Load(string file)
    {
        if (Drawn.TryGetValue(file, out var cached)) return cached;

        IImage? image = null;

        try
        {
            image = file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? LoadSvg(file)
                : new Bitmap(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rove] icon load failed: {Path.GetFileName(file)} — {ex.Message}");
        }

        Drawn[file] = image;
        return image;
    }

    private static IImage? LoadSvg(string file)
    {
        var root = XDocument.Load(file).Root;
        if (root is null) return null;

        foreach (var name in Unsupported)
        {
            if (!root.Descendants().Any(e => e.Name.LocalName == name)) continue;

            Console.Error.WriteLine($"[rove] icon declined ({name}): {Path.GetFileName(file)}");
            return null;
        }

        var bounds = ReadViewBox(root);
        var gradients = ReadGradients(root, bounds);
        var group = new DrawingGroup();

        Walk(root, group, inheritedFill: null, gradients);

        if (group.Children.Count == 0) return null;

        // NOTE: in Avalonia, DrawingGroup.ClipGeometry does NOT affect GetBounds
        // (AvaloniaUI/Avalonia#18512 — deliberately different from WPF), and
        // DrawingImage.Size is exactly Drawing.GetBounds().Size. So a clip can
        // never correct a bad size; it only hides pixels. Sizing therefore has
        // to come from the geometry we actually add.
        var ink = group.GetBounds();

        if (Diagnose)
        {
            Console.Error.WriteLine(
                $"[rove] icon {Path.GetFileName(file)}: viewBox {bounds}, ink {ink}");

            foreach (var child in group.Children)
            {
                if (child is not GeometryDrawing shape) continue;

                Console.Error.WriteLine(
                    $"[rove]   shape {shape.Geometry?.Bounds} " +
                    $"brush={Describe(shape.Brush)} pen={Describe(shape.Pen?.Brush)}");
            }
        }

        // No clip: it cannot fix the size, and it can crop real artwork when an
        // icon draws outside the viewBox it declares.
        return new DrawingImage { Drawing = group };
    }

    /// <summary>Set ROVE_ICON_DEBUG=1 to dump per-shape bounds and paint.</summary>
    private static readonly bool Diagnose =
        Environment.GetEnvironmentVariable("ROVE_ICON_DEBUG") == "1";

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "none",
        ISolidColorBrush solid => solid.Color.ToString(),
        IGradientBrush gradient => $"gradient({gradient.GradientStops.Count} stops)",
        _ => brush.GetType().Name,
    };

    // ---- gradients ------------------------------------------------------

    /// <summary>
    /// Builds every gradient in the document up front, keyed by id, so a
    /// fill="url(#x)" is a lookup. Two passes because gradients commonly carry
    /// only geometry and inherit their stops from another via href.
    /// </summary>
    private static Dictionary<string, IBrush> ReadGradients(XElement root, Rect bounds)
    {
        var elements = root.Descendants()
            .Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
            .Where(e => (string?)e.Attribute("id") is { Length: > 0 })
            .ToDictionary(e => (string)e.Attribute("id")!, e => e);

        var result = new Dictionary<string, IBrush>(StringComparer.Ordinal);

        foreach (var (id, element) in elements)
        {
            var stops = StopsFor(element, elements, depth: 0);
            if (stops.Count == 0) continue;

            // objectBoundingBox is the SVG default and maps directly onto
            // Avalonia's relative units. userSpaceOnUse is in viewBox
            // coordinates, so it is converted rather than declined.
            var absolute = (string?)element.Attribute("gradientUnits") == "userSpaceOnUse";

            double X(double v) => absolute && bounds.Width > 0 ? (v - bounds.X) / bounds.Width : v;
            double Y(double v) => absolute && bounds.Height > 0 ? (v - bounds.Y) / bounds.Height : v;

            if (element.Name.LocalName == "linearGradient")
            {
                result[id] = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(
                        X(Number(element, "x1", absolute ? bounds.X : 0)),
                        Y(Number(element, "y1", absolute ? bounds.Y : 0)),
                        RelativeUnit.Relative),
                    EndPoint = new RelativePoint(
                        X(Number(element, "x2", absolute ? bounds.Right : 1)),
                        Y(Number(element, "y2", absolute ? bounds.Y : 0)),
                        RelativeUnit.Relative),
                    GradientStops = stops,
                };
            }
            else
            {
                result[id] = new RadialGradientBrush
                {
                    Center = new RelativePoint(
                        X(Number(element, "cx", absolute ? bounds.Center.X : 0.5)),
                        Y(Number(element, "cy", absolute ? bounds.Center.Y : 0.5)),
                        RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(
                        X(Number(element, "fx", absolute ? bounds.Center.X : 0.5)),
                        Y(Number(element, "fy", absolute ? bounds.Center.Y : 0.5)),
                        RelativeUnit.Relative),
                    // RadiusX/RadiusY as RelativeScalar, not a single Radius
                    // double — an SVG radial gradient is circular, so both take
                    // the same value.
                    RadiusX = new RelativeScalar(
                        absolute && bounds.Width > 0
                            ? Number(element, "r", bounds.Width / 2) / bounds.Width
                            : Number(element, "r", 0.5),
                        RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(
                        absolute && bounds.Height > 0
                            ? Number(element, "r", bounds.Height / 2) / bounds.Height
                            : Number(element, "r", 0.5),
                        RelativeUnit.Relative),
                    GradientStops = stops,
                };
            }
        }

        return result;
    }

    private static GradientStops StopsFor(
        XElement element, Dictionary<string, XElement> all, int depth)
    {
        var stops = new GradientStops();

        foreach (var stop in element.Elements().Where(e => e.Name.LocalName == "stop"))
        {
            var offset = Percentage((string?)stop.Attribute("offset"));
            var colour = StopColour(stop);
            stops.Add(new GradientStop(colour, offset));
        }

        if (stops.Count > 0 || depth > 4) return stops;

        // href/xlink:href — the stops live on another gradient.
        var reference = (string?)stop_href(element);
        if (reference is { Length: > 1 } && reference[0] == '#'
            && all.TryGetValue(reference[1..], out var parent))
            return StopsFor(parent, all, depth + 1);

        return stops;

        static XAttribute? stop_href(XElement e)
            => e.Attribute("href")
               ?? e.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href");
    }

    private static Color StopColour(XElement stop)
    {
        var colour = (string?)stop.Attribute("stop-color");
        var opacity = Number(stop, "stop-opacity", 1.0);

        // Inline style wins, which is how most editors write stops out.
        if ((string?)stop.Attribute("style") is { Length: > 0 } style)
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(':', 2);
                if (pair.Length != 2) continue;

                if (pair[0].Trim() == "stop-color") colour = pair[1].Trim();
                else if (pair[0].Trim() == "stop-opacity"
                         && double.TryParse(pair[1].Trim(), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var parsed)) opacity = parsed;
            }
        }

        var parsedColour = Colors.Black;
        try { if (colour is { Length: > 0 }) parsedColour = Color.Parse(colour); }
        catch { /* leave black */ }

        return Color.FromArgb((byte)(parsedColour.A * Math.Clamp(opacity, 0, 1)),
            parsedColour.R, parsedColour.G, parsedColour.B);
    }

    private static double Percentage(string? raw)
    {
        if (raw is null) return 0;

        var text = raw.Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(percent ? value / 100 : value, 0, 1)
            : 0;
    }

    // ---- shapes ---------------------------------------------------------

    private static Rect ReadViewBox(XElement root)
    {
        var raw = (string?)root.Attribute("viewBox");

        if (raw is not null)
        {
            var parts = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                return new Rect(x, y, w, h);
        }

        return new Rect(0, 0, 16, 16);
    }

    /// <summary>
    /// translate/scale/matrix. Ignoring transforms meant any icon that placed
    /// its parts by transform drew them in the wrong place or not at all.
    /// </summary>
    private static Transform? ReadTransform(XElement element)
    {
        var raw = (string?)element.Attribute("transform");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var group = new TransformGroup();

        foreach (Match match in Regex.Matches(raw, @"(\w+)\s*\(([^)]*)\)"))
        {
            var numbers = match.Groups[2].Value
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : 0)
                .ToArray();

            switch (match.Groups[1].Value)
            {
                case "translate" when numbers.Length >= 1:
                    group.Children.Add(new TranslateTransform(
                        numbers[0], numbers.Length > 1 ? numbers[1] : 0));
                    break;

                case "scale" when numbers.Length >= 1:
                    group.Children.Add(new ScaleTransform(
                        numbers[0], numbers.Length > 1 ? numbers[1] : numbers[0]));
                    break;

                case "matrix" when numbers.Length >= 6:
                    group.Children.Add(new MatrixTransform(new Matrix(
                        numbers[0], numbers[1], numbers[2],
                        numbers[3], numbers[4], numbers[5])));
                    break;

                case "rotate" when numbers.Length >= 1:
                    group.Children.Add(new RotateTransform(numbers[0]));
                    break;
            }
        }

        return group.Children.Count > 0 ? group : null;
    }

    private static void Walk(
        XElement element, DrawingGroup group, IBrush? inheritedFill,
        Dictionary<string, IBrush> gradients)
    {
        var root = element.AncestorsAndSelf().Last();

        foreach (var child in element.Elements())
        {
            // defs holds paint definitions, not drawable content.
            if (child.Name.LocalName is "defs" or "linearGradient" or "radialGradient") continue;

            var fill = ReadFill(child, gradients) ?? inheritedFill;

            // A transform puts its subtree into its own group.
            var target = group;
            if (ReadTransform(child) is { } transform)
            {
                target = new DrawingGroup { Transform = transform };
                group.Children.Add(target);
            }

            switch (child.Name.LocalName)
            {
                case "g":
                    Walk(child, target, fill, gradients);
                    break;

                // <use> draws another element by id. Tela builds its file icons
                // from a shared page shape plus a coloured badge, so skipping
                // this left only the badge — a tiny mark instead of an icon.
                case "use":
                    var reference = (string?)child.Attribute("href")
                        ?? (string?)child.Attribute(
                            XNamespace.Get("http://www.w3.org/1999/xlink") + "href");

                    if (reference is not { Length: > 1 } || reference[0] != '#') break;

                    var referenced = root.Descendants()
                        .FirstOrDefault(e => (string?)e.Attribute("id") == reference[1..]);

                    if (referenced is null) break;

                    var placed = new DrawingGroup
                    {
                        Transform = new TranslateTransform(
                            Number(child, "x"), Number(child, "y")),
                    };

                    target.Children.Add(placed);
                    DrawOne(referenced, placed, fill, gradients);
                    break;

                case "path" when (string?)child.Attribute("d") is { Length: > 0 } data:
                    Add(target, Geometry.Parse(data), fill, child);
                    break;

                case "rect":
                    Add(target, new RectangleGeometry(new Rect(
                        Number(child, "x"), Number(child, "y"),
                        Number(child, "width"), Number(child, "height"))), fill, child);
                    break;

                case "circle":
                    Add(target, new EllipseGeometry(new Rect(
                        Number(child, "cx") - Number(child, "r"),
                        Number(child, "cy") - Number(child, "r"),
                        Number(child, "r") * 2, Number(child, "r") * 2)), fill, child);
                    break;

                case "ellipse":
                    Add(target, new EllipseGeometry(new Rect(
                        Number(child, "cx") - Number(child, "rx"),
                        Number(child, "cy") - Number(child, "ry"),
                        Number(child, "rx") * 2, Number(child, "ry") * 2)), fill, child);
                    break;

                case "polygon" when PolyGeometry(child, close: true) is { } polygon:
                    Add(target, polygon, fill, child);
                    break;

                case "polyline" when PolyGeometry(child, close: false) is { } polyline:
                    Add(target, polyline, fill, child);
                    break;
            }
        }
    }

    /// <summary>Draws one element, used by &lt;use&gt; to render its referent.</summary>
    private static void DrawOne(
        XElement element, DrawingGroup group, IBrush? fill,
        Dictionary<string, IBrush> gradients)
    {
        var own = ReadFill(element, gradients) ?? fill;

        switch (element.Name.LocalName)
        {
            case "g":
                Walk(element, group, own, gradients);
                break;

            case "path" when (string?)element.Attribute("d") is { Length: > 0 } data:
                Add(group, Geometry.Parse(data), own, element);
                break;

            case "rect":
                Add(group, new RectangleGeometry(new Rect(
                    Number(element, "x"), Number(element, "y"),
                    Number(element, "width"), Number(element, "height"))), own, element);
                break;

            case "circle":
                Add(group, new EllipseGeometry(new Rect(
                    Number(element, "cx") - Number(element, "r"),
                    Number(element, "cy") - Number(element, "r"),
                    Number(element, "r") * 2, Number(element, "r") * 2)), own, element);
                break;
        }
    }

    private static void Add(DrawingGroup group, Geometry geometry, IBrush? fill, XElement source)
    {
        var opacity = Number(source, "opacity", 1.0);
        if (opacity <= 0.01) return;

        // fill="none" is explicit and must not fall back to the text colour —
        // an outline-only shape has no fill by design.
        var declared = Declared(source, "fill");
        var filled = declared != "none";

        IBrush? brush = null;
        if (filled)
        {
            brush = Fade(fill ?? CurrentColour(),
                Number(source, "fill-opacity", 1.0) * opacity);
        }

        // Strokes were ignored entirely, so any icon drawn as outlines rendered
        // almost nothing — which is indistinguishable from a tiny icon.
        Pen? pen = null;
        var stroke = Declared(source, "stroke");

        if (stroke is { Length: > 0 } && stroke != "none")
        {
            var width = Number(source, "stroke-width", 1.0);
            var colour = stroke.Equals("currentColor", StringComparison.OrdinalIgnoreCase)
                ? CurrentColour()
                : SafeBrush(stroke);

            if (colour is not null && width > 0)
                pen = new Pen(Fade(colour, Number(source, "stroke-opacity", 1.0) * opacity), width);
        }

        if (brush is null && pen is null) return;

        group.Children.Add(new GeometryDrawing { Geometry = geometry, Brush = brush, Pen = pen });
    }

    private static IBrush Fade(IBrush brush, double opacity)
    {
        if (opacity >= 0.99 || brush is not ISolidColorBrush solid) return brush;

        return new SolidColorBrush(Color.FromArgb(
            (byte)(solid.Color.A * Math.Clamp(opacity, 0, 1)),
            solid.Color.R, solid.Color.G, solid.Color.B));
    }

    /// <summary>An attribute, or the same property from an inline style.</summary>
    private static string? Declared(XElement element, string property)
    {
        var value = (string?)element.Attribute(property);

        if ((string?)element.Attribute("style") is { Length: > 0 } style)
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(':', 2);
                if (pair.Length == 2 && pair[0].Trim() == property) value = pair[1].Trim();
            }
        }

        return value;
    }

    private static IBrush? SafeBrush(string value)
    {
        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }


    /// <summary>points="x,y x,y ..." — a polygon closes, a polyline does not.</summary>
    private static Geometry? PolyGeometry(XElement element, bool close)
    {
        var raw = (string?)element.Attribute("points");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var numbers = raw.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0)
            .ToArray();

        if (numbers.Length < 4) return null;

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < numbers.Length; i += 2)
        {
            builder.Append(i == 0 ? 'M' : 'L')
                   .Append(numbers[i].ToString(CultureInfo.InvariantCulture))
                   .Append(',')
                   .Append(numbers[i + 1].ToString(CultureInfo.InvariantCulture))
                   .Append(' ');
        }

        if (close) builder.Append('Z');

        try { return Geometry.Parse(builder.ToString()); }
        catch { return null; }
    }

    private static IBrush? ReadFill(XElement element, Dictionary<string, IBrush> gradients)
    {
        var value = Declared(element, "fill");

        if (string.IsNullOrWhiteSpace(value) || value == "none") return null;

        // url(#id) — a gradient defined elsewhere in the document.
        if (value.StartsWith("url(", StringComparison.Ordinal))
        {
            var id = value.Trim()[4..].TrimEnd(')').Trim().TrimStart('#').Trim('"', '\'');
            return gradients.TryGetValue(id, out var brush) ? brush : null;
        }

        // currentColor means "whatever the surrounding text is", resolved from
        // the live theme so symbolic icons follow the colour scheme.
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            return CurrentColour();

        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }

    private static readonly Dictionary<string, IImage> Fallbacks = new(StringComparer.Ordinal);

    /// <summary>
    /// The drawn glyph, as an image.
    ///
    /// It used to be Path elements sitting behind the themed icon in the same
    /// Panel, which meant both drew whenever a theme supplied one — three
    /// stacked layers relying on each other to mask, which they did not. One
    /// element with one source cannot overlap itself.
    /// **UI thread only**, like Load.
    /// </summary>
    public static IImage Fallback(bool isDirectory)
    {
        var accent = (Application.Current?.Resources["AccentColour"] as ISolidColorBrush)?.Color
                     ?? Colors.SteelBlue;
        var dim = (Application.Current?.Resources["ViewDimText"] as ISolidColorBrush)?.Color
                  ?? Colors.Gray;

        // Keyed by colour so a theme change produces a new drawing rather than
        // serving a stale one.
        var key = $"{isDirectory}|{accent}|{dim}";
        if (Fallbacks.TryGetValue(key, out var cached)) return cached;

        var group = new DrawingGroup();

        if (isDirectory)
        {
            // Two tones: a recessed back and a lit front panel.
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M1,3.5 L6,3.5 L7.5,5.5 L15,5.5 L15,13 L1,13 Z"),
                Brush = new SolidColorBrush(accent, 0.5),
            });
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M1.6,7 L14.4,7 L14.4,13 L1.6,13 Z"),
                Brush = new SolidColorBrush(accent, 0.95),
            });
        }
        else
        {
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M3,1.5 L10,1.5 L13,4.5 L13,14.5 L3,14.5 Z"),
                Brush = new SolidColorBrush(dim, 0.75),
            });
        }

        var image = new DrawingImage
        {
            Drawing = new DrawingGroup
            {
                ClipGeometry = new RectangleGeometry(new Rect(0, 0, 16, 16)),
                Children = { group },
            },
        };

        Fallbacks[key] = image;
        return image;
    }

    /// <summary>The live text colour, so symbolic icons follow the scheme.</summary>
    private static IBrush CurrentColour()
        => Application.Current?.Resources["ViewText"] as IBrush ?? Brushes.Gray;

    private static double Number(XElement element, string name, double fallback = 0)
        => double.TryParse((string?)element.Attribute(name),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
