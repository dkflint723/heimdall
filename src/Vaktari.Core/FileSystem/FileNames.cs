namespace Vaktari.Core.FileSystem;

/// <summary>
/// Tidies a name somebody typed, the way the desktop would.
///
/// **Explorer strips leading and trailing spaces silently**, and Windows itself
/// drops trailing spaces and dots at the API level — so a name typed with one
/// produces a file that does not match what was asked for, and on a bad day one
/// that other tools cannot open or delete at all.
///
/// Applied only to text a person typed. A name read from disk is used exactly as
/// it is: a file already called "report " exists, and quietly looking for
/// "report" instead would fail to find it.
/// </summary>
public static class FileNames
{
    /// <summary>
    /// The name as the filesystem would have it, or empty where nothing is
    /// left — which the caller should refuse rather than act on.
    /// </summary>
    public static string Clean(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return "";

        var name = typed.Trim();

        // **Only on Windows.** A trailing dot or space is legal on a
        // freedesktop filesystem and somebody may well have meant it; on
        // Windows the API discards them, so keeping them would ask for one name
        // and get another.
        if (OperatingSystem.IsWindows()) name = name.TrimEnd(' ', '.');

        return name;
    }
}
