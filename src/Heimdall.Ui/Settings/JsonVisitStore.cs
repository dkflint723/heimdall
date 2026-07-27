using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.Settings;

/// <summary>
/// Visit counts in one file, flushed on close.
///
/// Same shape as <see cref="JsonFolderViewStore"/>: a dirty flag rather than a
/// write per change, because navigation is frequent and a disk write per folder
/// opened would be absurd.
/// </summary>
public sealed class JsonVisitStore : IVisitStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    private Dictionary<string, int> _counts;
    private bool _dirty;

    public event EventHandler? Changed;

    public JsonVisitStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "visits.json");
        _tempPath = _path + ".tmp";

        _counts = Load();
    }

    private Dictionary<string, int> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);

            using var stream = File.OpenRead(_path);

            return JsonSerializer.Deserialize(stream, VisitJsonContext.Default.VisitFile)
                   ?.Folders ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // A bad file must never block startup.
            return new(StringComparer.Ordinal);
        }
    }

    public void Record(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var key = Normalise(path);

        lock (_gate)
        {
            _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
            _dirty = true;

            // Bounded, so a long-lived install does not accumulate every folder
            // ever opened. Trimmed to the strongest entries rather than the
            // newest, because the whole point is what gets used repeatedly.
            if (_counts.Count > 500)
                _counts = _counts.OrderByDescending(pair => pair.Value)
                                 .Take(200)
                                 .ToDictionary(pair => pair.Key, pair => pair.Value,
                                               StringComparer.Ordinal);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<VisitedFolder> Top(int count)
    {
        lock (_gate)
        {
            return _counts
                // A folder opened once is not a habit, and a list of things you
                // touched once is just noise.
                .Where(pair => pair.Value > 1)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(pair => new VisitedFolder(pair.Key, Label(pair.Key), pair.Value))
                .ToList();
        }
    }

    public void Forget(string path)
    {
        lock (_gate)
        {
            if (!_counts.Remove(Normalise(path))) return;

            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;

            try
            {
                using (var stream = File.Create(_tempPath))
                {
                    JsonSerializer.Serialize(
                        stream,
                        new VisitFile { Folders = _counts },
                        VisitJsonContext.Default.VisitFile);

                    stream.Flush();
                }

                File.Move(_tempPath, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[heimdall] visits write failed: {ex.Message}");
            }
        }
    }

    private static string Normalise(string path)
        => path.Length > 1 ? path.TrimEnd('/') : path;

    /// <summary>The folder's own name, falling back to the full path for a
    /// root, which has no name of its own.</summary>
    private static string Label(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

public sealed record VisitFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, int> Folders { get; init; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(VisitFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class VisitJsonContext : JsonSerializerContext;
