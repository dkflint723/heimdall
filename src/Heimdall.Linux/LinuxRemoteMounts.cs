using System.Diagnostics;
using Heimdall.Core.Places;

using Heimdall.Core;

namespace Heimdall.Linux;

/// <summary>
/// Reads gvfs and kio-fuse mount points straight off the filesystem.
///
/// Both projects exist to make remote locations look like directories, which is
/// exactly what a file manager wants — so the whole of Heimdall's network support
/// is a directory listing plus a name parser. Anything the desktop can mount,
/// Heimdall can browse, including protocols that did not exist when this was
/// written.
/// </summary>
public sealed partial class LinuxRemoteMounts : IRemoteMounts
{
    // Source-generated marshalling, so it survives trimming and AOT — same
    // reasoning as the xattr calls in LinuxTagStore.
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    private readonly string _runtimeDir;

    public LinuxRemoteMounts()
    {
        _runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                      ?? $"/run/user/{GetUid()}";
    }

    public bool IsAvailable => Directory.Exists(_runtimeDir);

    public IReadOnlyList<RemoteMount> Discover()
    {
        var found = new List<RemoteMount>();

        try
        {
            // gvfs names each mount by its connection parameters:
            //   smb-share:server=nas,share=media
            //   sftp:host=example.com,user=flint
            var gvfs = Path.Combine(_runtimeDir, "gvfs");

            if (Directory.Exists(gvfs))
            {
                foreach (var entry in Directory.EnumerateDirectories(gvfs))
                    found.Add(FromGvfs(entry));
            }

            // kio-fuse uses a nested layout instead: <root>/smb/nas/media
            foreach (var root in Directory.EnumerateDirectories(_runtimeDir, "kio-fuse-*"))
            {
                foreach (var scheme in SafeDirectories(root))
                {
                    var protocol = Path.GetFileName(scheme);

                    // One level down is the host; two is the share. Stopping at
                    // the host would list every share as one entry called "smb".
                    foreach (var host in SafeDirectories(scheme))
                    {
                        var shares = SafeDirectories(host).ToList();

                        if (shares.Count == 0)
                        {
                            found.Add(Build(host, Path.GetFileName(host), protocol));
                            continue;
                        }

                        foreach (var share in shares)
                            found.Add(Build(share,
                                $"{Path.GetFileName(share)} on {Path.GetFileName(host)}", protocol));
                    }
                }
            }
        }
        catch
        {
            // A runtime dir we cannot read means no remotes, not an error.
        }

        return found
            .GroupBy(m => m.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return []; }
    }

    private static RemoteMount FromGvfs(string path)
    {
        var name = Path.GetFileName(path);
        var protocol = name.Split(':')[0].Split('-')[0];

        // Turn "server=nas,share=media" into "media on nas".
        var parts = name.Contains(':')
            ? name[(name.IndexOf(':') + 1)..]
                .Split(',')
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase)
            : [];

        parts.TryGetValue("server", out var server);
        parts.TryGetValue("host", out var host);
        parts.TryGetValue("share", out var share);

        var where = server ?? host;

        var label = (share, where) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{share} on {where}",
            (_, { Length: > 0 }) => where,
            _ => name,
        };

        return Build(path, label, protocol);
    }

    private static RemoteMount Build(string path, string label, string protocol) => new()
    {
        Path = path,
        Label = label,
        Protocol = protocol,

        // Cheapest possible liveness check: enumerating a dead FUSE mount
        // throws or hangs briefly, and that is precisely what we want to know.
        Reachable = IsReachable(path),
    };

    private static bool IsReachable(string path)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            enumerator.MoveNext();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RemoteMount> MountAsync(string uri, CancellationToken ct)
    {
        var before = Discover().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);

        // gio drives the same gvfs backends the desktop uses, so a share mounted
        // here appears in Nautilus and Dolphin too, and vice versa.
        var code = await Task.Run(() => RunGio(uri, ct), ct).ConfigureAwait(false);

        // Poll rather than parse: gio's output format is not a contract, but a
        // new directory appearing under the mount root is.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var mount = Discover().FirstOrDefault(m => !before.Contains(m.Path));
            if (mount is not null) return mount;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new IOException(code == 0
            ? "the mount did not appear — it may need credentials"
            : "could not mount; if it needs a password, connect once from your file manager so the desktop stores it");
    }

    public async Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct)
    {
        // kio-fuse mounts belong to KIO, which manages their lifetime itself;
        // gio has no authority over them and would just fail confusingly.
        if (mount.Path.Contains("kio-fuse-", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "this is a KDE connection — disconnect it from the Plasma network panel");

        var code = await Task.Run(() => RunGioArgs(["mount", "-u", mount.Path], ct), ct)
                             .ConfigureAwait(false);

        return code == 0;
    }

    private static int RunGio(string uri, CancellationToken ct)
        => RunGioArgs(["mount", uri], ct);

    private static int RunGioArgs(string[] args, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("gio")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return -1;

            // Concurrently — sequential drains deadlock if the child fills the
            // stream we are not reading yet, and the timeout below never runs.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            // Short: gio cannot prompt for a password without a terminal, so if
            // it needs one it fails fast rather than hanging.
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("mounts", ex); }
                return -1;
            }

            Task.WaitAll(new Task[] { stdout, stderr }, 5_000);

            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
