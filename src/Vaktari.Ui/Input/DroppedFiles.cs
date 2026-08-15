using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Vaktari.Ui.Input;

/// <summary>
/// What a drop is actually carrying, and — when the answer is nothing usable —
/// why.
///
/// **A drop that cannot be taken used to do nothing at all**, which is
/// indistinguishable from a drop that missed the pane, or from a bug. Dragging
/// out of a zip opened in Explorer does exactly that, and reads as the
/// application being unreliable.
/// </summary>
/// <param name="Paths">Real paths, ready to copy or move.</param>
/// <param name="Refusal">Empty when there is nothing to explain.</param>
public readonly record struct DroppedFiles(IReadOnlyList<string> Paths, string Refusal)
{
    public bool Any => Paths.Count > 0;
}

public static class DroppedFileReader
{
    /// <summary>
    /// Reads a drop.
    ///
    /// **Two lines of Avalonia, then a decision.** The decision is where the
    /// behaviour lives and it is separated deliberately: Avalonia's storage
    /// items cannot be implemented outside the framework, so anything that
    /// takes one cannot be tested, and burying the reasoning inside such a
    /// method would put it out of reach along with them.
    /// </summary>
    public static DroppedFiles Read(IDataTransfer data, string destination)
    {
        var paths = (data.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        return Decide(paths, [.. data.Formats.Select(f => f.Identifier)], destination);
    }

    /// <summary>
    /// What a drop of these paths, offered in these formats, means here.
    /// </summary>
    /// <param name="offered">Local paths the drop carried, which may be none
    /// even when it carried files.</param>
    /// <param name="formats">Format identifiers, which is how a drop carrying
    /// files with no paths is told from one carrying nothing.</param>
    internal static DroppedFiles Decide(
        IReadOnlyList<string> offered, IReadOnlyList<string> formats, string destination)
    {
        // A file dropped into the folder it already lives in is a no-op whether
        // copying or moving — the guard that used to do this covered only
        // copies, so a move produced "name (1)".
        var usable = offered
            .Where(p => p != destination
                        && !string.Equals(Path.GetDirectoryName(p), destination, StringComparison.Ordinal))
            .ToList();

        if (usable.Count > 0) return new DroppedFiles(usable, "");

        if (offered.Count > 0) return new DroppedFiles(usable, "that is already here");

        if (HasVirtualFiles(formats))
            return new DroppedFiles(usable,
                "those files are inside an archive and have no location on disk yet — "
                + "extract them first, or drag them from a program that unpacks as it copies");

        return new DroppedFiles(usable, formats.Count == 0
            ? "that drop carried nothing"
            : "there are no files in that");
    }

    /// <summary>
    /// **Files that exist only inside another program.** Windows offers these
    /// as a descriptor plus one stream per item rather than as paths, which is
    /// how Explorer presents the contents of a zip.
    ///
    /// Vaktari cannot take them, and that is a limit of what a drop handler is
    /// given rather than a decision: reading the contents needs the native data
    /// object, one stream at a time, by index, and Avalonia exposes formats and
    /// bytes but not the object. Recognising the case is what turns a drop that
    /// silently does nothing into one that says why.
    /// </summary>
    private static bool HasVirtualFiles(IReadOnlyList<string> formats) =>
        formats.Any(f =>
            f.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase)
            || f.Contains("FileContents", StringComparison.OrdinalIgnoreCase));
}
