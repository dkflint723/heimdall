namespace Vaktari.Core.FileSystem;

/// <summary>
/// One way to render a size in bytes.
///
/// There were four, and they did not agree: 500 bytes read as "500 B" in the
/// properties window, "0.5 KB" in the sidebar, and the details panel said "KiB"
/// while everything else said "KB" for exactly the same 1024-based arithmetic.
/// Four copies is a maintenance problem; four copies that disagree is a bug the
/// user can see.
///
/// **Binary units, labelled honestly.** Every one of the four divided by 1024,
/// so three of them were labelling binary quantities with decimal names. KDE's
/// own KFormat does the same as this, which matters because the point of the
/// project is to sit beside the rest of the desktop rather than argue with it.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Whole bytes below a kibibyte — "512 B" rather than "0.5 KiB", because a
    /// fraction of a unit is harder to read than the exact number it replaced.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return $"{bytes:N0} B";

        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // One decimal up to gibibytes, two beyond: at that scale the first
        // decimal is worth several hundred megabytes and rounding it away
        // loses something real.
        return unit >= 3
            ? $"{size:0.##} {Units[unit]}"
            : $"{size:0.#} {Units[unit]}";
    }
}
