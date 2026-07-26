using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Search;

namespace Heimdall.Linux;

/// <summary>
/// Baloo when it's indexing, a recursive walk when it isn't.
///
/// Building an indexer was never the plan — KDE already runs one and has been
/// indexing this machine for months. The walk exists so search still returns
/// something on a box where Baloo is switched off, just slowly and with a
/// visible warning rather than silently empty results.
/// </summary>
public sealed class LinuxSearchProvider : ISearchProvider
{
    // KDE Frameworks 6 suffixes its CLI tools so they can coexist with KF5, so
    // the plain name is not what Fedora KDE actually installs.
    private static readonly Lazy<string?> BalooBinary =
        new(() => Locate("baloosearch6") ?? Locate("baloosearch"));

    public bool IsAvailable => true;

    public string BackendName => BalooBinary.Value is null ? "walk" : "baloo";

    /// <summary>Only the index can search inside files; the walk matches names.</summary>
    public bool SupportsContentSearch => BalooBinary.Value is not null;

    private static string? Locate(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    /// <summary>True if the query is a shell-style pattern rather than a substring.</summary>
    private static bool IsGlob(string text)
        => text.Contains('*') || text.Contains('?');

    public IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct)
        // Baloo indexes words, not filename patterns, so a glob has to go
        // through the walk — that is what MatchesSimpleExpression is for.
        => BalooBinary.Value is { } baloo && !IsGlob(query.Text)
            ? SearchWithBalooAsync(baloo, query, ct)
            : SearchByWalkingAsync(query, ct);

    private static async IAsyncEnumerable<FileEntry> SearchWithBalooAsync(
        string binary, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(query.Text);

        Process? process = null;
        try { process = Process.Start(info); }
        catch { /* index present at detection but unusable now — yield nothing */ }

        if (process is null) yield break;

        var count = 0;

        using (process)
        using (var reader = process.StandardOutput)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (count >= query.MaxResults) break;

                // Output carries timing and summary lines as well as paths.
                var path = line.Trim();
                if (path.Length == 0 || path[0] != '/') continue;

                // Baloo indexes the whole home; the scope is applied here rather
                // than trusting a flag whose name differs across KDE versions.
                if (query.ScopePath is { Length: > 0 } scope &&
                    !path.StartsWith(scope, StringComparison.Ordinal)) continue;

                if (Describe(path) is { } entry)
                {
                    count++;
                    yield return entry;
                }
            }
        }
    }

    private static async IAsyncEnumerable<FileEntry> SearchByWalkingAsync(
        SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var root = query.ScopePath is { Length: > 0 } scope
            ? scope
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var text = query.Text;
        var glob = IsGlob(text);
        var ignoreCase = !query.CaseSensitive;

        var walk = new FileSystemEnumerable<string>(
            root,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            })
        {
            // A pattern is matched as a pattern; anything else is treated as a
            // substring, which is what people expect when they just type a word.
            ShouldIncludePredicate = glob
                ? (ref FileSystemEntry entry) =>
                    FileSystemName.MatchesSimpleExpression(text, entry.FileName, ignoreCase)
                : (ref FileSystemEntry entry) =>
                    entry.FileName.ToString().Contains(text, comparison),
        };

        var count = 0;

        // The walk is blocking and can run for a long time, so it is pulled on
        // the thread pool in chunks rather than holding the caller.
        using var enumerator = walk.GetEnumerator();

        while (true)
        {
            if (ct.IsCancellationRequested || count >= query.MaxResults) yield break;

            string? path = null;
            var moved = await Task.Run(() =>
            {
                if (!enumerator.MoveNext()) return false;
                path = enumerator.Current;
                return true;
            }, ct).ConfigureAwait(false);

            if (!moved) yield break;
            if (path is null) continue;

            if (Describe(path) is { } entry)
            {
                count++;
                yield return entry;
            }
        }
    }

    private static FileEntry? Describe(string path)
    {
        try
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return null;

            var name = Path.GetFileName(path);
            var flags = EntryFlags.None;
            if (isDir) flags |= EntryFlags.Directory;
            if (name.StartsWith('.')) flags |= EntryFlags.Hidden;

            var info = new FileInfo(path);

            return new FileEntry(
                name,
                path,
                isDir ? 0 : info.Length,
                info.LastWriteTimeUtc,
                flags);
        }
        catch
        {
            // Indexed but since deleted, or unreadable — skip it silently.
            return null;
        }
    }
}
