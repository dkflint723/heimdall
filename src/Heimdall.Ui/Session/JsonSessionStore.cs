using System.Text.Json;
using Avalonia.Threading;
using Heimdall.Core.Session;

namespace Heimdall.Ui.Session;

/// <summary>
/// Crash-safe session storage. Three rules, each of which exists because
/// breaking it is how file managers end up "randomly forgetting":
///
///   1. Save continuously, not on exit. Saving in a shutdown handler loses
///      everything to a crash, a force-kill, or an update reboot.
///   2. Write atomically. A truncated file fails to parse on next launch,
///      which the user experiences as amnesia rather than as corruption.
///   3. Never let a bad session file prevent startup. Any load failure
///      returns null and the app opens empty.
/// </summary>
public sealed class JsonSessionStore : ISessionStore, IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SessionState? _pending;
    private bool _disposed;

    public JsonSessionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "session.json");
        _tempPath = _path + ".tmp";
        _backupPath = _path + ".bak";

        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += OnTick;
    }

    /// <summary>~/.local/state/heimdall on Linux, %LOCALAPPDATA%\heimdall on Windows.</summary>
    public static string DefaultDirectory()
    {
        var directory = Path.Combine(StateRoot(), "heimdall");

        // The app was called ROVE until it was renamed to Heimdall — the
        // direction this comment used to state backwards, which is exactly the
        // kind of thing nobody notices until they are debugging a migration.
        // Adopt the old state rather than starting empty: losing every tab,
        // pinned place and window position to a rename would be a poor trade
        // for a new name.
        Heimdall.Core.PreviousName.Adopt(directory, Path.Combine(StateRoot(), "rove"));

        return directory;
    }

    private static string StateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

            if (string.IsNullOrWhiteSpace(stateHome))
                stateHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "state");

            return stateHome;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }


    /// <summary>
    /// Synchronous by design. The window needs its geometry before it is shown,
    /// and an async load means restoring size and position after first paint —
    /// a visible jump on every launch. The file is a few kilobytes.
    /// </summary>
    public SessionState? Load()
    {
        var state = TryLoad(_path) ?? TryLoad(_backupPath);
        return state?.Version == SessionState.CurrentVersion ? state : null;
    }

    private static SessionState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SessionJsonContext.Default.SessionState);
        }
        catch
        {
            // Corrupt, truncated, unreadable — all the same answer.
            return null;
        }
    }

    public void NotifyChanged(SessionState state)
    {
        if (_disposed) return;

        _pending = state with { SavedAt = DateTimeOffset.UtcNow };
        _timer.Stop();
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        // async void: anything escaping here terminates the process. WriteAsync
        // catches its own failures, but taking the write lock can still throw
        // if disposal races this tick.
        try
        {
            _timer.Stop();
            await WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] session write failed: {ex.Message}");
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        _timer.Stop();
        await WriteAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        var state = _pending;
        if (state is null || _disposed) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stream = File.Create(_tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, state, SessionJsonContext.Default.SessionState, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(_path))
                File.Copy(_path, _backupPath, overwrite: true);

            // Rename is atomic on both ext4/btrfs and NTFS, so a crash mid-save
            // leaves either the old file or the new one, never a half-written one.
            File.Move(_tempPath, _path, overwrite: true);
        }
        catch
        {
            // Losing a session write is not worth interrupting the user over.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Async because the write lock must be drained before the semaphore is
    /// disposed — tearing it down mid-write would throw into the swallowing
    /// catch above and silently lose the final save.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _timer.Stop();
        _timer.Tick -= OnTick;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        _disposed = true;
        _writeLock.Release();
        _writeLock.Dispose();
    }
}
