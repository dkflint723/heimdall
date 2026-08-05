using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core;
using Heimdall.Core.FileSystem;

namespace Heimdall.Windows;

/// <summary>The tag index as it is stored. A record so the JSON source generator
/// can see it; the live copy is rebuilt with a case-insensitive comparer.</summary>
public sealed record TagIndex
{
    public Dictionary<string, List<string>> Files { get; init; } = [];
    public List<string> Known { get; init; } = [];
}

[JsonSerializable(typeof(TagIndex))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TagIndexJsonContext : JsonSerializerContext;

/// <summary>
/// Tags, stored in a sidecar index keyed by path.
///
/// **The decision WINDOWS.md §4 said to make deliberately, made.** It framed the
/// choice as NTFS alternate data streams versus a path-keyed sidecar. Sidecar,
/// for four reasons, in the order they matter:
///
/// **1. ADS loses data silently and irrecoverably.** A copy to FAT or exFAT, a
/// zip round-trip, most archivers, most cloud sync clients — every one of them
/// drops the stream and reports success. A sidecar can go *stale*, which is
/// visible and repairable; it cannot go silently empty.
///
/// **2. ADS cannot be read in the listing path.** <see cref="ITagStore.GetAsync"/>
/// is called for every row in the viewport. An alternate stream costs a
/// CreateFile per file to read; this costs a dictionary lookup. `FileEntry`'s
/// own rule — nothing on the listing path may require a follow-up stat — is the
/// same constraint one level down, and it is what keeps a large folder usable.
///
/// **3. ADS does not exist off NTFS.** Tagging a file on an exFAT USB stick
/// simply fails, and that is a normal thing to want to do.
///
/// **4. The promise ADS appears to keep, it does not.** The Linux README says
/// tags "live on the file itself as extended attributes, so they travel with it
/// and other tools can read them". No other Windows tool reads a private
/// alternate stream, so ADS buys the fragility of on-file storage without the
/// interoperability that justifies it.
///
/// **What this costs, stated plainly:** tags do not travel with a file to
/// another machine, and a file renamed or moved by *another* program loses its
/// tags. Renames and moves made through this application keep them — see
/// <see cref="Retarget"/> — which covers the common case, not every case.
///
/// **The upgrade path is not ADS.** It is the Windows property system's
/// <c>System.Keywords</c>, which is the field Explorer shows as "Tags", which
/// Windows Search indexes, and which genuinely is read by other tools. It only
/// works for formats with a metadata container — JPEG, Office documents, MP3 —
/// never for a .txt or a folder, so it can only ever mirror this index, not
/// replace it. It needs IPropertyStore, which is COM.
/// </summary>
public sealed class WindowsTagStore : ITagStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    private Dictionary<string, List<string>> _files;
    private List<string> _known;

    public event EventHandler? TagsChanged;

    public WindowsTagStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "tags.json");

        var loaded = Load();

        // Rebuilt with the platform's comparer: the deserialiser has no way to
        // know that C:\A\B and c:\a\b are one key here.
        _files = new Dictionary<string, List<string>>(
            loaded.Files, StringComparer.OrdinalIgnoreCase);

        _known = loaded.Known;
    }

    public IReadOnlyList<string> KnownTags
    {
        get { lock (_gate) return [.. _known]; }
    }

    public ValueTask<IReadOnlyList<string>> GetAsync(string path, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(
                _files.TryGetValue(Key(path), out var tags) ? [.. tags] : []);
        }
    }

    public ValueTask SetAsync(string path, IReadOnlyList<string> tags, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = Key(path);

            if (tags.Count == 0) _files.Remove(key);
            else _files[key] = [.. tags];

            Remember(tags);
            Save();
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask ToggleAsync(
        IReadOnlyList<string> paths, string tag, bool add, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var path in paths)
            {
                var key = Key(path);

                if (add)
                {
                    if (!_files.TryGetValue(key, out var tags)) _files[key] = tags = [];
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                }
                else if (_files.TryGetValue(key, out var tags))
                {
                    tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                    if (tags.Count == 0) _files.Remove(key);
                }
            }

            if (add) Remember([tag]);
            Save();
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stops offering a tag WITHOUT touching any file that carries it — the
    /// interface is explicit that forgetting is reversible and deleting is not.
    /// </summary>
    public void ForgetKnown(string tag)
    {
        lock (_gate)
        {
            _known.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Follows a rename or a move, including everything beneath a folder.
    ///
    /// **This is what makes a path-keyed store tolerable**, and it is why
    /// <see cref="WindowsFileOperations"/> holds a reference to this: without it
    /// renaming a tagged file through this application would lose its tags,
    /// which is the one case entirely within our control.
    /// </summary>
    public void Retarget(string oldPath, string newPath)
    {
        var from = Key(oldPath);
        var to = Key(newPath);
        var prefix = from + Path.DirectorySeparatorChar;

        lock (_gate)
        {
            var moved = new List<(string From, string To)>();

            foreach (var key in _files.Keys)
            {
                if (string.Equals(key, from, StringComparison.OrdinalIgnoreCase))
                    moved.Add((key, to));
                else if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    moved.Add((key, to + key[from.Length..]));
            }

            if (moved.Count == 0) return;

            foreach (var (source, target) in moved)
            {
                if (!_files.Remove(source, out var tags)) continue;
                _files[target] = tags;
            }

            Save();
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops tags for a path and everything beneath it, for a permanent delete.
    ///
    /// **Not called when something is recycled.** A recycled file can come back,
    /// and forgetting its tags the moment it goes to the bin would mean
    /// restoring it from Explorer returned an untagged file.
    /// </summary>
    public void Forget(string path)
    {
        var key = Key(path);
        var prefix = key + Path.DirectorySeparatorChar;

        lock (_gate)
        {
            var gone = _files.Keys
                .Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                            || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (gone.Count == 0) return;

            foreach (var k in gone) _files.Remove(k);
            Save();
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One spelling per folder, so a path typed with forward slashes
    /// finds the tags set on the same folder typed with backslashes.</summary>
    private static string Key(string path) => PathRules.Normalise(path);

    private void Remember(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
            if (!_known.Contains(tag, StringComparer.OrdinalIgnoreCase))
                _known.Add(tag);
    }

    private TagIndex Load()
    {
        try
        {
            if (!File.Exists(_path)) return new TagIndex();

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, TagIndexJsonContext.Default.TagIndex)
                   ?? new TagIndex();
        }
        catch (Exception ex)
        {
            // A corrupt index must not stop the application starting. It is
            // reported rather than silently replaced, because starting with no
            // tags is exactly what a user would want to know about.
            Quiet.Swallowed("tags", ex);
            return new TagIndex();
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            var index = new TagIndex { Files = _files, Known = _known };

            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, index, TagIndexJsonContext.Default.TagIndex);

            // Written aside and moved into place, so a crash mid-write leaves
            // the previous index rather than a truncated one.
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("tags", ex);
        }
    }
}
