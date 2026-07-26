namespace Heimdall.Core.Places;

/// <summary>A service announcing itself on the local network.</summary>
public sealed record DiscoveredService
{
    /// <summary>The advertised name, e.g. the machine's hostname.</summary>
    public required string Name { get; init; }

    /// <summary>DNS-SD service type, e.g. <c>_webdav._tcp</c>.</summary>
    public required string ServiceType { get; init; }

    public required string Host { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }

    /// <summary>
    /// The URI to hand to the mounter. Chosen from the service type, because a
    /// service that speaks WebDAV should be mounted as WebDAV rather than
    /// opened in a browser.
    /// </summary>
    public required string MountUri { get; init; }

    /// <summary>Something to show: "partybox — webdav on 3923".</summary>
    public string Label => $"{Name} · {Friendly}";

    public string Friendly => ServiceType switch
    {
        "_webdav._tcp" or "_webdavs._tcp" => "webdav",
        "_smb._tcp" => "smb",
        "_sftp-ssh._tcp" => "sftp",
        "_ftp._tcp" => "ftp",
        "_http._tcp" or "_https._tcp" => "http",
        _ => ServiceType.Trim('_').Split('.')[0],
    };
}

/// <summary>
/// Finds file services announcing themselves on the LAN.
///
/// Heimdall implements neither mDNS nor SSDP — the desktop already runs a responder
/// (avahi on Linux) that has been listening since boot, and it knows about
/// services that appeared before Heimdall started. Asking it is both less code and
/// more correct than starting our own listener and waiting.
/// </summary>
public interface INetworkDiscovery
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// One sweep of the network. Takes a second or two, so callers should run
    /// it off the UI thread and on demand rather than continuously.
    /// </summary>
    Task<IReadOnlyList<DiscoveredService>> BrowseAsync(CancellationToken ct);
}
