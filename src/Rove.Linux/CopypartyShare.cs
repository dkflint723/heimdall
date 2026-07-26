using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Rove.Core.Sharing;

namespace Rove.Linux;

/// <summary>
/// Serves folders by driving copyparty, which is launched as an ordinary child
/// process and talked to over its command line — no library, no embedding.
///
/// The reason for a process rather than an implementation: copyparty is Python,
/// Rove is trimmed NativeAOT C#. Embedding a Python runtime would cost the
/// single-binary story and the startup time for a feature most sessions never
/// use. A subprocess costs nothing until it runs, and the two can be upgraded
/// independently.
///
/// Same reasoning as the scripts menu: the useful thing already exists as a
/// program, so run the program.
/// </summary>
public sealed class CopypartyShare : IFileSharing
{
    private sealed record Running(Process Process, ShareSession Session, string ConfigPath);

    private readonly ConcurrentDictionary<Guid, Running> _running = new();
    private string? _command;
    private string[] _prefixArgs;

    public CopypartyShare()
    {
        (_command, _prefixArgs) = Locate();
    }

    /// <summary>Re-runs discovery, so an install takes effect without a restart.</summary>
    private void Rescan()
    {
        (_command, _prefixArgs) = Locate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAvailable => _command is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "copyparty is not installed — try 'python3 -m pip install --user copyparty'";

    public IReadOnlyList<ShareSession> Active =>
        _running.Values.Select(r => r.Session).ToList();

    public event EventHandler? Changed;

    /// <summary>
    /// Found rather than bundled, and in the order a user would expect: a real
    /// executable first, then the module, then a downloaded sfx.
    /// </summary>
    private static (string? Command, string[] Prefix) Locate()
    {
        if (Which("copyparty") is { } binary) return (binary, []);

        if (Which("python3") is { } python)
        {
            // Only claim the module if it is actually importable, or every
            // share would fail at launch with an unhelpful message.
            if (Run(python, ["-c", "import copyparty"]) == 0)
                return (python, ["-m", "copyparty"]);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var candidate in new[]
                     {
                         Path.Combine(home, "copyparty-sfx.py"),
                         Path.Combine(home, ".local", "bin", "copyparty-sfx.py"),
                         Path.Combine(home, "Downloads", "copyparty-sfx.py"),
                     })
            {
                if (File.Exists(candidate)) return (python, [candidate]);
            }
        }

        return (null, []);
    }

    private static string? Which(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];

        foreach (var directory in paths)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            var candidate = Path.Combine(directory, name);

            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Unreadable PATH entry; keep looking.
            }
        }

        return null;
    }

    private static int Run(string file, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return -1;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(10_000) ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Installs copyparty, trying the approaches a careful person would, in
    /// order of how much they disturb the system.
    ///
    /// pipx first: it isolates the package in its own environment and leaves
    /// the system Python alone. Then a plain user install. Only if that is
    /// refused because the distro marks its Python externally managed (PEP 668
    /// — Fedora does) do we pass --break-system-packages, and a --user install
    /// cannot damage system packages even then.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (IsAvailable) return true;

        var attempts = new List<(string File, string[] Args, string Describe)>();

        if (Which("pipx") is { } pipx)
            attempts.Add((pipx, ["install", "copyparty"], "pipx install copyparty"));

        if (Which("python3") is { } python)
        {
            attempts.Add((python,
                ["-m", "pip", "install", "--user", "--upgrade", "copyparty"],
                "pip install --user copyparty"));

            attempts.Add((python,
                ["-m", "pip", "install", "--user", "--upgrade", "--break-system-packages", "copyparty"],
                "pip install --user --break-system-packages copyparty"));
        }

        if (attempts.Count == 0)
        {
            progress.Report("no python3 or pipx found — cannot install");
            return false;
        }

        foreach (var (file, args, describe) in attempts)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"running {describe}…");

            var (code, output) = await Task.Run(() => Capture(file, args, ct), ct)
                                           .ConfigureAwait(false);

            if (code == 0)
            {
                Rescan();

                if (IsAvailable)
                {
                    progress.Report("copyparty installed");
                    return true;
                }

                // Installed but not on PATH — the usual cause is ~/.local/bin
                // missing from PATH, and the module form still works.
                progress.Report("installed, but not runnable yet — check that ~/.local/bin is on PATH");
                return false;
            }

            // Only worth trying the next approach if this one failed for the
            // reason the next one addresses.
            var externallyManaged = output.Contains("externally-managed-environment",
                StringComparison.OrdinalIgnoreCase);

            if (!externallyManaged && attempts.IndexOf((file, args, describe)) < attempts.Count - 1)
                progress.Report($"{describe} failed, trying another way…");
        }

        progress.Report("install failed — try 'python3 -m pip install --user copyparty' in a terminal");
        return false;
    }

    private static (int Code, string Output) Capture(string file, string[] args, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return (-1, "");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            // Generous: a cold pip install of a package this size can take a
            // while on a slow connection.
            if (!process.WaitForExit(300_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, "timed out");
            }

            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Writes the server config for one share.
    ///
    /// A config file rather than <c>-v src:dst:perm</c> on the command line,
    /// for two reasons. That syntax is colon-separated, so a folder named
    /// "notes:2026" would parse into something other than what the user picked
    /// — a path is not safe to interpolate into it. And the file states the
    /// whole policy in one place where it can be read back, rather than as a
    /// string of flags whose interaction has to be reasoned about.
    /// </summary>
    private static string WriteConfig(string path, int port, bool writable)
    {
        var file = Path.Combine(Path.GetTempPath(), $"rove-share-{Guid.NewGuid():N}.conf");

        var config = $"""
            # generated by Rove; deleted when the share stops

            [global]
              p: {port}
              no-thumb
              no-robots
              z
              q

            [/]
              {path}
              accs:
                {(writable ? "rw" : "r")}: *
              flags:
                # Nothing outside this folder, ever. xvol ignores symlinks that
                # leave the volume's top directory and xdev refuses to cross
                # into another filesystem; since copyparty 1.7.0 both block
                # access at request time, not just during indexing. Without
                # these, one symlink inside a shared folder exposes wherever it
                # points.
                xvol
                xdev

                # No index database, so an ad-hoc share leaves no .hist folder
                # behind in the user's directory.
                no_idx: .

            """;

        File.WriteAllText(file, config);
        return file;
    }

    public Task<ShareSession> StartAsync(string path, bool writable, CancellationToken ct)
    {
        if (_command is null)
            throw new InvalidOperationException(UnavailableReason);

        path = Path.GetFullPath(path).TrimEnd('/');

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);

        if (path.Length == 0 || path == "/")
            throw new InvalidOperationException("refusing to share the whole filesystem");

        var port = FreePort();
        var configPath = WriteConfig(path, port, writable);

        var info = new ProcessStartInfo(_command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // Deliberately NOT the shared folder: copyparty with no volume
            // defined serves its working directory read-write, so pointing it
            // at the share would make a config failure quietly permissive.
            // Temp is empty and boring.
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach (var arg in _prefixArgs) info.ArgumentList.Add(arg);

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(configPath);

        var process = Process.Start(info)
                      ?? throw new InvalidOperationException("could not start copyparty");

        var session = new ShareSession
        {
            Path = path,
            Url = $"http://{LocalAddress()}:{port}/",
            Port = port,
            Writable = writable,
            Handle = Guid.NewGuid(),
        };

        _running[(Guid)session.Handle] = new Running(process, session, configPath);

        // A server that dies silently would leave a dead entry in the UI
        // promising a URL that answers nothing.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _running.TryRemove((Guid)session.Handle, out _);
            Forget(configPath);
            Changed?.Invoke(this, EventArgs.Empty);
        };

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(session);
    }

    private static void Forget(string configPath)
    {
        try { if (File.Exists(configPath)) File.Delete(configPath); }
        catch { /* a leftover temp file is not worth surfacing */ }
    }

    public Task StopAsync(ShareSession session)
    {
        if (session.Handle is Guid id && _running.TryRemove(id, out var running))
        {
            Kill(running.Process);
            Forget(running.ConfigPath);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        foreach (var id in _running.Keys.ToList())
        {
            if (!_running.TryRemove(id, out var running)) continue;

            Kill(running.Process);
            Forget(running.ConfigPath);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Already gone, or not ours to kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Asks the OS for a free port rather than guessing one.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// The address another machine can actually reach. Loopback would be
    /// useless in a share URL, which is the whole point of one.
    /// </summary>
    private static string LocalAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        return address.Address.ToString();
                }
            }
        }
        catch
        {
            // Fall through to loopback; at least the local machine works.
        }

        return "127.0.0.1";
    }
}
