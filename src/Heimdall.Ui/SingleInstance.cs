using System.Net.Sockets;
using System.Text;

using Heimdall.Core;

namespace Heimdall.Ui;

/// <summary>
/// Ensures one Heimdall, and gives later launches a way to hand their paths to
/// it instead of starting a second copy.
///
/// This matters far more once Heimdall is the desktop's default file manager:
/// every "open containing folder" becomes a launch, and without this each one
/// started another full application that then ignored the folder it was asked
/// for. A caller that waits for its handler to finish waits forever.
///
/// The guard is an exclusive file lock, NOT a named Mutex. .NET's named mutexes
/// do not provide cross-process exclusion here — the previous implementation
/// used one and was silently inert, which is why two instances were running.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private FileStream? _lock;
    private Socket? _listener;
    private CancellationTokenSource? _stopping;

    /// <summary>Paths sent by a later launch, already on the UI thread.</summary>
    public event EventHandler<string[]>? PathsReceived;

    private static string RuntimeDirectory
    {
        get
        {
            // XDG_RUNTIME_DIR is per-session and cleared on logout, so a stale
            // lock cannot survive a reboot and block every future start.
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

            if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
                return runtime;

            return Path.GetTempPath();
        }
    }

    private static string LockPath => Path.Combine(RuntimeDirectory, "heimdall.lock");
    private static string SocketPath => Path.Combine(RuntimeDirectory, "heimdall.sock");

    /// <summary>
    /// True when this process is the one and only. False means another already
    /// holds the lock and the caller should forward and exit.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }

        try
        {
            // A crash leaves the socket file behind; the lock proves nobody is
            // listening on it, so removing it is safe here and only here.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);

            _stopping = new CancellationTokenSource();
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);

            _ = Task.Run(() => AcceptAsync(_stopping.Token));
        }
        catch (Exception ex)
        {
            // Without the listener we are still the only instance; later
            // launches simply cannot hand anything over.
            Console.Error.WriteLine($"[heimdall] instance channel unavailable: {ex.Message}");
        }

        return true;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                using var client = await listener.AcceptAsync(ct).ConfigureAwait(false);

                var buffer = new byte[8192];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, ct)
                                       .ConfigureAwait(false);

                if (read <= 0) continue;

                var paths = Encoding.UTF8.GetString(buffer, 0, read)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (paths.Length == 0) continue;

                // Raised on the UI thread: handlers open tabs and activate the
                // window, neither of which is safe from a socket thread.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => PathsReceived?.Invoke(this, paths));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[heimdall] instance channel: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hands paths to the running instance. Returns false if nothing answered,
    /// in which case the caller should start normally rather than vanish.
    /// </summary>
    public static bool TryForward(string[] paths)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);

            // Short: the whole point is to return before the calling
            // application notices it launched anything.
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\n', paths)));

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _stopping?.Cancel(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
        try { _listener?.Dispose(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
        try { _lock?.Dispose(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }

        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
    }
}
