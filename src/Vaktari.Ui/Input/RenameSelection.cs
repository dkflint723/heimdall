namespace Vaktari.Ui.Input;

/// <summary>
/// How much of a name is selected when a rename begins.
///
/// **Explorer selects the name and not the extension**, and it is one of those
/// behaviours nobody can describe until it is missing: press F2, type, and in
/// Vaktari the .txt went with it. Every file renamed by typing over the
/// selection lost its extension unless the whole name was retyped.
/// </summary>
public static class RenameSelection
{
    /// <summary>
    /// How many characters to select, from the start.
    ///
    /// A folder is selected whole — folders do not have extensions, and the
    /// dots in "v1.2.3" are part of the name. So is a dotfile: the leading dot
    /// of .gitignore begins the name rather than separating one, which is why
    /// the test is for a dot PAST the first character rather than for any dot
    /// at all.
    ///
    /// The LAST dot, so archive.tar.gz offers "archive.tar" — matching the
    /// shell's idea of an extension rather than the first thing that looks
    /// like one.
    /// </summary>
    public static int LengthFor(string? name, bool isDirectory)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (isDirectory) return name.Length;

        var dot = name.LastIndexOf('.');

        // A name that is nothing but an extension — ".gitignore" — and a name
        // ending in a dot both mean the whole thing.
        return dot > 0 && dot < name.Length - 1 ? dot : name.Length;
    }
}
