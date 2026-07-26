namespace Rove.Core.Sharing;

/// <summary>A folder currently being served, and where to reach it.</summary>
public sealed record ShareSession
{
    public required string Path { get; init; }
    public required string Url { get; init; }
    public required int Port { get; init; }
    public required bool Writable { get; init; }

    /// <summary>Opaque handle the provider uses to stop this share.</summary>
    public required object Handle { get; init; }

    public string Label => System.IO.Path.GetFileName(Path.TrimEnd('/')) is { Length: > 0 } name
        ? name
        : Path;
}

/// <summary>
/// Serves a local folder over the network.
///
/// Rove does not implement this itself — it drives an existing server
/// (copyparty). Writing an HTTP/WebDAV server with resumable uploads, dedup and
/// a browser UI is a project in its own right, and a good one already exists
/// under a permissive licence. This interface is the seam that lets Rove use it
/// without depending on its internals.
///
/// Platform-specific because *locating and launching* a server differs by OS,
/// even though the concept does not.
/// </summary>
public interface IFileSharing
{
    /// <summary>False when no server is installed; the UI hides the feature.</summary>
    bool IsAvailable { get; }

    /// <summary>What the user needs to install, when unavailable.</summary>
    string? UnavailableReason { get; }

    IReadOnlyList<ShareSession> Active { get; }

    /// <summary>
    /// Installs the backend, reporting progress as it goes.
    ///
    /// Deliberately explicit rather than automatic on first use: this installs
    /// software on the user's machine, which should be something they chose,
    /// not something that happened while they were trying to do something else.
    /// </summary>
    Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>
    /// Starts serving a folder. Read-only unless <paramref name="writable"/> —
    /// the safe default matters, because this opens a folder to the network.
    /// </summary>
    Task<ShareSession> StartAsync(string path, bool writable, CancellationToken ct);

    Task StopAsync(ShareSession session);

    /// <summary>Stops everything. Called on shutdown so nothing outlives the app.</summary>
    Task StopAllAsync();

    event EventHandler? Changed;
}
