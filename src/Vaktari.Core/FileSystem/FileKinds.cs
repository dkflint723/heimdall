namespace Vaktari.Core.FileSystem;

/// <summary>A kind of empty file the user can create directly.</summary>
/// <param name="Label">What the menu says.</param>
/// <param name="Extension">Including the dot; empty for a file with none.</param>
/// <param name="Executable">Set the executable bit on creation.</param>
public sealed record NewFileKind(string Label, string Extension, bool Executable = false);

/// <summary>
/// The built-in "new file" list.
///
/// Exists because <see cref="ITemplateProvider"/> only ever offers what is in
/// the user's XDG Templates folder, which is empty on a stock system — so the
/// menu was there and did nothing for anyone who had not populated it. These
/// need no files on disk and work on a fresh install.
///
/// **Deliberately short.** A menu of thirty extensions is a worse answer than a
/// menu of eight: the long tail is better served by creating a text file and
/// renaming it, which costs one keystroke more and needs no list at all. These
/// are the ones worth a click.
/// </summary>
public static class FileKinds
{
    public static readonly IReadOnlyList<NewFileKind> Common =
    [
        new("Text file", ".txt"),
        new("Markdown document", ".md"),
        new("Shell script", ".sh", Executable: true),
        new("Python script", ".py", Executable: true),
        new("JSON file", ".json"),
        new("CSV spreadsheet", ".csv"),
        new("HTML page", ".html"),

        // Last, and with no extension: the escape hatch for everything the list
        // above does not cover.
        new("Empty file", ""),
    ];
}
