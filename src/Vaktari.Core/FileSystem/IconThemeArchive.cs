using System.Formats.Tar;
using System.IO.Compression;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Unpacks a downloaded icon theme, safely.
///
/// **This exists because Windows will not make the symbolic links a theme is
/// built out of.** Papirus expresses its whole structure in about forty
/// thousand of them — Papirus-Dark is very nearly nothing but links back into
/// Papirus — and creating one needs Developer Mode or an elevated process. Any
/// ordinary extraction therefore fails forty thousand times, says so at length,
/// and leaves a theme with holes exactly where files and folders are.
///
/// Doing the unpacking here rather than handing the archive to the shell is what
/// removes that: a link becomes a line in a small index Vaktari reads, which
/// needs no privilege at all and costs nothing on disk. See
/// <see cref="AliasIndex"/>.
///
/// **Nothing that is not an icon comes out of the archive.** A theme is
/// index.theme, .svg and .png; everything else in the file — scripts, makefiles,
/// anything at all — is skipped rather than written and then ignored. That, the
/// containment check on every path, and the size caps are the whole safety
/// story, and they are deliberately boring: this reads an archive off the
/// internet.
/// </summary>
public static class IconThemeArchive
{
    /// <summary>The per-theme file that carries what the symbolic links meant.
    /// One line per alias, tab separated, both sides relative to the theme
    /// folder it sits in.</summary>
    public const string AliasIndex = ".vaktari-aliases";

    /// <summary>
    /// Bounds, so a malformed or hostile archive cannot fill a disk. Papirus is
    /// the largest of the common themes by a distance — roughly 51,000 entries
    /// and 110 MB — so these leave it several times over rather than fitting it
    /// exactly.
    /// </summary>
    private const int MaxEntries = 250_000;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 32L * 1024 * 1024;

    public sealed record Installed(IReadOnlyList<string> Themes, int Icons, int Aliases, long Bytes);

    /// <summary>
    /// Reads a .tar.gz and leaves the themes inside it in <paramref name="destination"/>,
    /// one folder each.
    ///
    /// **Unpacked beside the destination and moved in at the end**, so a
    /// download that fails halfway cannot leave a half-theme behind — a theme
    /// with some of its icons is worse than no theme, because it looks like it
    /// worked.
    /// </summary>
    public static Installed Install(Stream archive, string destination, CancellationToken token = default)
    {
        // **Normalised once, here, and everything downstream is then comparable.**
        // Paths are compared against each other all through this — is this entry
        // under the destination, is this link inside the theme — and one side of
        // those comparisons has been through GetFullPath while the other has
        // not. A destination written with forward slashes, which Windows accepts
        // everywhere, therefore matched nothing: every one of Papirus's fifty
        // thousand links was silently dropped and the theme came out with holes,
        // which is the exact failure this class exists to prevent.
        destination = Path.GetFullPath(destination);

        Directory.CreateDirectory(destination);

        // A sibling of the destination rather than the system temp folder: the
        // last step is a directory move, and a move across volumes is a copy of
        // every one of fifty thousand files.
        var staging = Path.Combine(destination, ".unpacking-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            var (icons, bytes, aliases) = Unpack(archive, staging, token);
            var themes = Publish(staging, destination, aliases, token);

            return new Installed(themes, icons, aliases.Count, bytes);
        }
        finally
        {
            Delete(staging);
        }
    }

    private static (int Icons, long Bytes, List<(string Alias, string Target)> Aliases) Unpack(
        Stream archive, string staging, CancellationToken token)
    {
        Directory.CreateDirectory(staging);

        var aliases = new List<(string, string)>();
        var icons = 0;
        var bytes = 0L;
        var entries = 0;

        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry)
        {
            token.ThrowIfCancellationRequested();

            if (++entries > MaxEntries)
                throw new InvalidDataException("that archive holds more files than an icon theme should.");

            if (Contained(staging, entry.Name) is not { } path) continue;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;

                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    // **Recorded, never created.** The link is what Windows
                    // refuses; the meaning of the link is all Vaktari needs,
                    // and a line of text carries it.
                    if (entry.LinkName is { Length: > 0 } target) aliases.Add((entry.Name, target));
                    break;

                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    if (!IsThemeContent(path)) break;
                    if (entry.Length > MaxEntryBytes)
                        throw new InvalidDataException($"'{entry.Name}' is far too large to be an icon.");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    using (var file = File.Create(path))
                    {
                        entry.DataStream?.CopyTo(file);
                        bytes += file.Length;
                    }

                    if (bytes > MaxTotalBytes)
                        throw new InvalidDataException("that archive unpacks to more than an icon theme should.");

                    icons++;
                    break;

                // Devices, fifos, anything else: an icon theme has none of them
                // and there is no reason to be the first thing to write one.
                default:
                    break;
            }
        }

        return (icons, bytes, aliases);
    }

    /// <summary>
    /// Moves the themes out of the staging tree, dropping whatever else the
    /// archive carried.
    ///
    /// **A theme is a folder with an index.theme in it**, found rather than
    /// assumed: a repository archive wraps everything in a name like
    /// papirus-icon-theme-master, and one download commonly holds several
    /// themes — Papirus, Papirus-Dark, Papirus-Light. Landing them side by side
    /// is also what keeps the links between them working, since those are
    /// written relative to one another.
    /// </summary>
    private static List<string> Publish(
        string staging, string destination, List<(string Alias, string Target)> aliases, CancellationToken token)
    {
        var published = new List<string>();

        foreach (var folder in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, "index.theme")))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();

            var name = Path.GetFileName(folder);
            if (name.Length == 0) continue;

            WriteAliases(staging, folder, aliases);

            var landing = Path.Combine(destination, name);

            // Replacing a theme already there, rather than merging into it:
            // a half-updated theme resolves to a mixture of two versions.
            Delete(landing);
            Directory.Move(folder, landing);

            published.Add(landing);
        }

        return published;
    }

    /// <summary>
    /// Writes the links that belong to one theme, as paths relative to it.
    ///
    /// Relative on both sides so the folder stays portable — a theme that is
    /// moved, or unpacked here and read from there, keeps working — and because
    /// a link into a sibling theme has to survive the pair being published
    /// together.
    /// </summary>
    private static void WriteAliases(
        string staging, string theme, List<(string Alias, string Target)> aliases)
    {
        var lines = new List<string>();

        foreach (var (alias, target) in aliases)
        {
            if (Contained(staging, alias) is not { } from) continue;
            if (!Inside(theme, from)) continue;

            // The link's target is written relative to the folder the link is
            // in, which is how tar records it and how the filesystem reads it.
            var directory = Path.GetDirectoryName(from);
            if (directory is null) continue;

            if (Contained(staging, Path.Combine(
                    Path.GetRelativePath(staging, directory), target)) is not { } to) continue;

            // Icons, and whole folders of them — a variant links a size
            // directory at a time, and a folder has no extension to recognise
            // it by, so testing content alone dropped exactly the links that
            // carry a dark theme.
            if (!Directory.Exists(to) && !IsThemeContent(to) && !IsThemeContent(from)) continue;

            lines.Add(string.Join('\t',
                Path.GetRelativePath(theme, from).Replace('\\', '/'),
                Path.GetRelativePath(theme, to).Replace('\\', '/')));
        }

        if (lines.Count > 0)
            File.WriteAllLines(Path.Combine(theme, AliasIndex), lines);
    }

    /// <summary>
    /// Where an archive entry may be written, or null if it may not be written
    /// at all.
    ///
    /// **The check every archive reader needs and many do not have.** An entry
    /// is free to call itself ..\..\Windows\System32\something, and a reader
    /// that simply combines paths will write exactly there. The rule is not
    /// that the name looks harmless but that the resolved path is genuinely
    /// underneath the destination.
    /// </summary>
    internal static string? Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal)) return null;

        string full, anchored;

        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            anchored = Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        return Inside(anchored, full) ? full : null;
    }

    /// <summary>
    /// Whether one path is genuinely underneath another.
    ///
    /// **Both sides normalised, because comparing a normalised path with an
    /// unnormalised one is how this went wrong.** C:/x and C:\x are the same
    /// folder and share no common prefix.
    /// </summary>
    private static bool Inside(string root, string path)
    {
        root = Path.GetFullPath(root);

        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(root, Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// What an icon theme is made of, and nothing else.
    ///
    /// A whitelist rather than a list of things to avoid: the archive is
    /// somebody else's, and the set of file types worth refusing is not one
    /// anybody can finish writing down.
    /// </summary>
    private static bool IsThemeContent(string path)
    {
        var leaf = Path.GetFileName(path);

        if (leaf.Equals("index.theme", StringComparison.OrdinalIgnoreCase)) return true;

        var extension = Path.GetExtension(leaf);

        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Staging left behind is untidy, not broken, and throwing from a
            // cleanup would replace a real error with this one.
        }
    }
}
