using System.Runtime.CompilerServices;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Search;

namespace Heimdall.Windows;

/// <summary>
/// Name search by walking the tree.
///
/// **No index behind it, and it says so.** <see cref="ISearchProvider"/> is
/// documented as sitting on an index someone else maintains — Everything on
/// Windows — and this is not that. Everything is third-party, may not be
/// installed, and talks over an IPC protocol worth its own decision; Windows
/// Search is COM. A managed walk is honest, has no dependency, and is the same
/// thing the interface says the UI falls back to.
///
/// <see cref="IsAvailable"/> is nonetheless true. It means "will this return
/// results", not "is it fast", and returning false would send the UI to its own
/// fallback walk — the same work, done twice as far as the user can tell.
/// </summary>
public sealed class WindowsSearchProvider : ISearchProvider
{
    public bool IsAvailable => true;

    public string BackendName => "directory walk";

    /// <summary>
    /// False. Reading every file to match text is a different order of cost from
    /// matching names, and doing it without an index would be indistinguishable
    /// from a hang on any real folder.
    /// </summary>
    public bool SupportsContentSearch => false;

    public async IAsyncEnumerable<FileEntry> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var root = query.ScopePath;

        // A null scope means "everywhere indexed", and with no index the honest
        // reading is every fixed drive.
        var roots = string.IsNullOrEmpty(root) ? FixedDrives() : [root];

        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var found = 0;

        foreach (var start in roots)
        {
            foreach (var path in Walk(start, ct))
            {
                if (found >= query.MaxResults) yield break;
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(path);
                if (!name.Contains(query.Text, comparison)) continue;

                if (Describe(path) is not { } entry) continue;

                found++;
                yield return entry;

                // The walk is synchronous and CPU-bound on the directory reads.
                // Yielding lets the panel paint what it has rather than
                // appearing frozen until the first batch is complete.
                await Task.Yield();
            }
        }
    }

    private static List<string> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// An explicit stack rather than <c>SearchOption.AllDirectories</c>.
    /// The built-in recursive enumeration throws out the whole walk when it hits
    /// one unreadable directory, and on a Windows drive it always does —
    /// System Volume Information sits at the root of C:\ and is denied to a
    /// non-elevated process. IgnoreInaccessible covers the enumeration itself,
    /// but a per-directory loop is what keeps a failure local.
    /// </summary>
    private static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var directory = pending.Pop();

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory, "*", options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            // Materialised per directory so a mid-enumeration failure costs this
            // folder rather than everything still on the stack.
            List<string> batch;
            try { batch = entries.ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in batch)
            {
                yield return entry;

                var isDirectory = false;
                try { isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                if (isDirectory) pending.Push(entry);
            }
        }
    }

    private static FileEntry? Describe(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            var flags = EntryFlags.None;
            if (isDirectory) flags |= EntryFlags.Directory;
            if ((attributes & FileAttributes.Hidden) != 0) flags |= EntryFlags.Hidden;
            if ((attributes & FileAttributes.System) != 0) flags |= EntryFlags.System;
            if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= EntryFlags.Symlink;
            if ((attributes & FileAttributes.ReadOnly) != 0) flags |= EntryFlags.ReadOnly;

            var info = new FileInfo(path);

            return new FileEntry(
                Path.GetFileName(path),
                path,
                isDirectory ? 0 : info.Length,
                info.LastWriteTimeUtc,
                flags);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
