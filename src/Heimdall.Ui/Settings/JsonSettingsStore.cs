using System.Text.Json;
using Heimdall.Core.Settings;

namespace Heimdall.Ui.Settings;

/// <summary>
/// Preferences on disk, beside the session but never inside it.
///
/// Same two rules as the session store, for the same reasons: write atomically,
/// because a truncated file reads as amnesia rather than as corruption; and
/// never let a bad file prevent startup.
///
/// One rule dropped, deliberately: no debounce timer. The session changes on
/// every navigation and needs one. Settings change when a person clicks
/// something in a dialog, which is rare enough that a write per change is both
/// affordable and what they expect — closing the dialog and having the file
/// already be right.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    // A plain object rather than System.Threading.Lock: the newer type would be
    // fine on this target, but nothing here needs it and this cannot be wrong.
    private readonly object _writeLock = new();

    public JsonSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _tempPath = _path + ".tmp";
        _backupPath = _path + ".bak";
    }

    /// <summary>
    /// Synchronous, like the session load and for a sharper reason: the startup
    /// setting decides whether the session is read at all, so this has to have
    /// finished before anything else looks at disk.
    ///
    /// Returns defaults rather than null. There is always a valid set of
    /// preferences — an absent file means a first run, not a failure.
    /// </summary>
    public SettingsState Load()
    {
        var state = TryLoad(_path) ?? TryLoad(_backupPath);

        // A file from a future version is ignored rather than partially read.
        // Silently running with half the settings someone chose is worse than
        // visibly running with none of them.
        return state?.Version == SettingsState.CurrentVersion ? state : new SettingsState();
    }

    private static SettingsState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.SettingsState);
        }
        catch
        {
            // Corrupt, truncated, unreadable — all the same answer.
            return null;
        }
    }

    public void Save(SettingsState settings)
    {
        lock (_writeLock)
        {
            try
            {
                using (var stream = File.Create(_tempPath))
                {
                    JsonSerializer.Serialize(
                        stream, settings, SettingsJsonContext.Default.SettingsState);
                    stream.Flush();
                }

                if (File.Exists(_path))
                    File.Copy(_path, _backupPath, overwrite: true);

                // Atomic on ext4, btrfs and NTFS: a crash mid-save leaves either
                // the old file or the new one, never a half-written one.
                File.Move(_tempPath, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Unlike a lost session write, this one is worth saying out loud —
                // the user just changed a setting and has a right to know it did
                // not stick.
                Console.Error.WriteLine($"[heimdall] settings write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes the defaults out once, on a first run, so the file exists and can
    /// be read or hand-edited before any dialog is built. Never overwrites.
    /// </summary>
    public void EnsureFileExists(SettingsState settings)
    {
        if (!File.Exists(_path)) Save(settings);
    }
}
