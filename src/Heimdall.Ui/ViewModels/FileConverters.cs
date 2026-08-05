using Avalonia.Data.Converters;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Presentation of the raw values in <see cref="FileEntry"/>.
///
/// The entry deliberately carries a raw <c>long</c> and a <c>DateTimeOffset</c>
/// — it is produced by the enumerator and must stay allocation-free — so the
/// formatting belongs here rather than in the model.
/// </summary>
public static class FileConverters
{
    /// <summary>
    /// Human-readable size. Folders get an em dash rather than "0", which is
    /// actively misleading: a folder is not empty just because its own inode
    /// has no length.
    /// </summary>
    /// <summary>
    /// The folder an entry lives in, with the home directory shown as `~`.
    ///
    /// Only used by the recent listings, where rows span the whole filesystem
    /// and a bare filename identifies nothing. Abbreviating home is what makes
    /// the column narrow enough to be worth having — most rows are under it.
    /// </summary>
    public static readonly IValueConverter ParentPath =
        new FuncValueConverter<FileEntry, string>(entry =>
        {
            if (string.IsNullOrEmpty(entry.FullPath)) return "";

            // Normalised first, so the comparisons below see one spelling of the
            // separator. On Windows a path can arrive with either.
            var parent = PathRules.Parent(entry.FullPath);
            if (string.IsNullOrEmpty(parent)) return PathRules.LeafName(entry.FullPath);

            var home = PathRules.Normalise(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (!string.IsNullOrEmpty(home))
            {
                if (PathRules.Same(parent, home)) return "~";

                // The separator is part of the test on purpose: without it,
                // "/home/flintstone" would match a home of "/home/flint".
                // Through the platform's own constant, and case-insensitively on
                // Windows, for the same reasons PathRules.Same exists.
                if (parent.StartsWith(home + Path.DirectorySeparatorChar, PathRules.Comparison))
                    return "~" + parent[home.Length..];
            }

            return parent;
        });

    public static readonly IValueConverter Size =
        new FuncValueConverter<FileEntry, string>(entry =>
        {
            if (entry.FullPath is null) return "";
            if (entry.IsDirectory) return "—";

            // The sixth and last copy of this. It was the only one already
            // using binary unit names, which is why the Size column and the
            // status bar beside it disagreed about the same file.
            return ByteSize.Format(entry.Length);
        });

    /// <summary>
    /// Local time, and compact. The default rendering of a DateTimeOffset is a
    /// full timestamp with a UTC offset — accurate, unreadable in a column, and
    /// wrong for a person looking at their own files.
    /// </summary>
    public static readonly IValueConverter Modified =
        new FuncValueConverter<DateTimeOffset, string>(value =>
        {
            var local = value.ToLocalTime();
            var now = DateTimeOffset.Now;

            // Absolute is one fixed shape regardless of when the file is from,
            // which is what you want when comparing dates rather than reading
            // them.
            if (Settings.AppSettings.Current.Views.Details.DateStyle
                == Core.Settings.DateStyle.Absolute)
                return local.ToString("yyyy-MM-dd HH:mm");

            // Relative: today gets a time, this year drops the year, older keeps
            // it. Relative in the sense that matters — it omits what you can
            // infer from today's date — and it earns column width back.
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Year == now.Year) return local.ToString("dd MMM HH:mm");

            return local.ToString("dd MMM yyyy");
        });

    /// <summary>
    /// Upper-cases a label for display only. The sidebar's group headings are
    /// set in small caps with tracking; <c>Place.Label</c> is data read off the
    /// desktop's places list and is never rewritten to suit a heading.
    /// </summary>
    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string>(s => s?.ToUpperInvariant() ?? "");

    /// <summary>
    /// A wash of the accent behind the open place's row. Nothing but the edge
    /// bar carried "this is where you are", and on a one-line row that bar is a
    /// 2x14px mark — too small to find at a glance.
    /// </summary>
    public static readonly IValueConverter CurrentRowFill =
        new FuncValueConverter<bool, Avalonia.Media.IBrush?>(current =>
            current && Avalonia.Application.Current?.Resources["AccentDim"]
                is Avalonia.Media.ISolidColorBrush accent
                // 7% — the design's own rgba(...,.07). Enough to find the row,
                // not enough to read as a selection.
                ? new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(18, accent.Color.R, accent.Color.G, accent.Color.B))
                : Avalonia.Media.Brushes.Transparent);

    /// <summary>Accent along the active side's tab bar, transparent on the other.</summary>
    public static readonly IValueConverter ActiveEdge =
        new FuncValueConverter<bool, object?>(active =>
            Avalonia.Application.Current?.Resources[active ? "AccentColour" : "EdgeHighlight"]);

    /// <summary>The current folder is the one you are in; the ancestors are
    /// links. Weight carries that, so it still reads without colour.</summary>
    public static readonly IValueConverter CrumbWeight =
        new FuncValueConverter<bool, Avalonia.Media.FontWeight>(
            isLast => isLast ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    public static readonly IValueConverter CrumbBrush =
        new FuncValueConverter<bool, object?>(isLast =>
            Avalonia.Application.Current?.Resources[isLast ? "ViewText" : "ViewDimText"]);
}
