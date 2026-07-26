using Avalonia.Data.Converters;
using Rove.Core.FileSystem;

namespace Rove.Ui.ViewModels;

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
    public static readonly IValueConverter Size =
        new FuncValueConverter<FileEntry, string>(entry =>
        {
            if (entry.FullPath is null) return "";
            if (entry.IsDirectory) return "—";

            return entry.Length switch
            {
                < 1024 => $"{entry.Length} B",
                < 1024L * 1024 => $"{entry.Length / 1024.0:0.#} KiB",
                < 1024L * 1024 * 1024 => $"{entry.Length / (1024.0 * 1024):0.#} MiB",
                _ => $"{entry.Length / (1024.0 * 1024 * 1024):0.##} GiB",
            };
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

            // Today gets a time, this year drops the year, older keeps it —
            // the same progression Dolphin uses, and it earns column width back.
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Year == now.Year) return local.ToString("dd MMM HH:mm");

            return local.ToString("dd MMM yyyy");
        });

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
