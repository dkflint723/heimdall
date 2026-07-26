using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rove.Core;

namespace Rove.Linux;

[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class KnownTagsJsonContext : JsonSerializerContext;

/// <summary>
/// Tags stored in the <c>user.xdg.tags</c> extended attribute — the same place
/// Dolphin and Baloo keep them, so a file tagged here is tagged for the whole
/// desktop rather than only for this application. Same reasoning as using the
/// XDG trash and the freedesktop thumbnail cache instead of private copies.
///
/// The convention is a comma-separated list of names in one xattr.
/// </summary>
public sealed partial class LinuxTagStore : ITagStore
{
    private const string Attribute = "user.xdg.tags";

    private readonly string _knownPath;
    private List<string> _known;

    public LinuxTagStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _knownPath = Path.Combine(stateDirectory, "tags.json");
        _known = LoadKnown();
    }

    public IReadOnlyList<string> KnownTags => _known;

    public event EventHandler? TagsChanged;

    // .NET has no extended-attribute API, so these come straight from libc.
    // LibraryImport rather than DllImport: the marshalling is source-generated
    // and therefore survives trimming and AOT.
    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattr(string path, string name, byte[]? value, nint size);

    [LibraryImport("libc", EntryPoint = "setxattr", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetXattr(string path, string name, byte[] value, nint size, int flags);

    [LibraryImport("libc", EntryPoint = "removexattr", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RemoveXattr(string path, string name);

    /// <summary>
    /// Genuinely asynchronous. It used to return an already-completed ValueTask
    /// wrapping a synchronous read, so every caller's await finished inline —
    /// two getxattr syscalls per visible row, on the UI thread.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> GetAsync(string path, CancellationToken ct)
        => new(Task.Run(() => Read(path), ct));

    private static IReadOnlyList<string> Read(string path)
    {
        try
        {
            // Size query first: the attribute may be any length, and asking
            // with a zero buffer is how you find out.
            var size = GetXattr(path, Attribute, null, 0);
            if (size <= 0) return [];

            var buffer = new byte[size];
            var read = GetXattr(path, Attribute, buffer, size);
            if (read <= 0) return [];

            return Encoding.UTF8.GetString(buffer, 0, (int)read)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // No xattr support on this filesystem, or no permission. Tags are
            // simply absent rather than an error the user has to dismiss.
            return [];
        }
    }

    public async ValueTask SetAsync(string path, IReadOnlyList<string> tags, CancellationToken ct)
    {
        await Task.Run(() => Write(path, tags), ct).ConfigureAwait(false);

        Remember(tags);
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Write(string path, IReadOnlyList<string> tags)
    {
        try
        {
            if (tags.Count == 0)
            {
                RemoveXattr(path, Attribute);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(string.Join(",", tags));
            SetXattr(path, Attribute, bytes, bytes.Length, 0);
        }
        catch
        {
            // Read-only mount, unsupported filesystem — silently unsupported
            // rather than a failure dialog on every attempt.
        }
    }

    public async ValueTask ToggleAsync(
        IReadOnlyList<string> paths, string tag, bool add, CancellationToken ct)
    {
        // One hop off the UI thread for the whole batch rather than per file.
        await Task.Run(() => ToggleCore(paths, tag, add, ct), ct).ConfigureAwait(false);

        if (add) Remember([tag]);

        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleCore(
        IReadOnlyList<string> paths, string tag, bool add, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var current = Read(path).ToList();
            var has = current.Contains(tag, StringComparer.OrdinalIgnoreCase);

            if (add == has) continue;

            if (add) current.Add(tag);
            else current.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

            Write(path, current);
        }
    }

    /// <summary>
    /// Names are remembered locally only so the menu can offer them. The tags
    /// themselves live on the files; losing this list costs nothing but
    /// convenience.
    /// </summary>
    private void Remember(IReadOnlyList<string> tags)
    {
        var changed = false;

        foreach (var tag in tags)
        {
            if (_known.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            _known.Add(tag);
            changed = true;
        }

        if (!changed) return;

        _known = _known.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            using var stream = File.Create(_knownPath);
            JsonSerializer.Serialize(stream, _known, KnownTagsJsonContext.Default.ListString);
        }
        catch { }
    }

    private List<string> LoadKnown()
    {
        try
        {
            if (!File.Exists(_knownPath)) return [];
            using var stream = File.OpenRead(_knownPath);
            return JsonSerializer.Deserialize(stream, KnownTagsJsonContext.Default.ListString) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
