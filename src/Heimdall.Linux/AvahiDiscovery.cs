using System.Diagnostics;
using Heimdall.Core.Places;

namespace Heimdall.Linux;

/// <summary>
/// Browses the LAN by asking avahi, the mDNS responder already running on the
/// machine.
///
/// copyparty announces itself over mDNS when started with <c>-z</c> (which is
/// how Heimdall starts its own shares), so a copyparty on any machine here shows up
/// without configuration. The same query finds WebDAV, SMB and SFTP from
/// anything else on the network — NAS boxes, other desktops — because they all
/// use the same announcements.
/// </summary>
public sealed class AvahiDiscovery : INetworkDiscovery
{
    /// <summary>
    /// Service types worth asking about, most specific first. Plain HTTP is
    /// included last because copyparty serves WebDAV and HTTP on one port and
    /// only advertises the latter in some configurations.
    /// </summary>
    private static readonly string[] Types =
    [
        "_webdav._tcp",
        "_webdavs._tcp",
        "_smb._tcp",
        "_sftp-ssh._tcp",
        "_ftp._tcp",
        "_http._tcp",
    ];

    private readonly string? _browse;

    public AvahiDiscovery()
    {
        _browse = Which("avahi-browse");
    }

    public bool IsAvailable => _browse is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "network discovery needs avahi — try 'sudo dnf install avahi-tools'";

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Unreadable PATH entry.
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<DiscoveredService>> BrowseAsync(CancellationToken ct)
    {
        if (_browse is null) return [];

        var found = new List<DiscoveredService>();

        foreach (var type in Types)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var line in await Task.Run(() => Query(type, ct), ct).ConfigureAwait(false))
            {
                if (Parse(line, type) is { } service) found.Add(service);
            }
        }

        // The same machine answers on IPv4 and IPv6, and often on several
        // interfaces; one entry per host and port is what a person expects.
        return found
            .GroupBy(s => $"{s.Host}:{s.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string[] Query(string type, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(_browse!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // -p parsable, -r resolve to host/address/port, -t stop once the
            // cache is exhausted rather than browsing forever.
            info.ArgumentList.Add("-prt");
            info.ArgumentList.Add(type);

            using var process = Process.Start(info);
            if (process is null) return [];

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses avahi's parsable format:
    /// <c>=;iface;IPv4;name;_http._tcp;local;host.local;192.168.1.5;3923;"txt"</c>
    /// Only '=' lines are resolved; the rest are announcements without detail.
    /// </summary>
    private static DiscoveredService? Parse(string line, string type)
    {
        var parts = line.Split(';');

        if (parts.Length < 9 || parts[0] != "=") return null;

        // IPv6 link-local addresses carry a scope that is meaningless to
        // another process, so prefer IPv4 and let the hostname do the work.
        if (parts[2] != "IPv4") return null;

        if (!int.TryParse(parts[8], out var port) || port <= 0) return null;

        var name = Unescape(parts[3]);
        var host = parts[6];
        var address = parts[7];

        return new DiscoveredService
        {
            Name = name.Length > 0 ? name : host,
            ServiceType = type,
            Host = host,
            Address = address,
            Port = port,
            MountUri = ToMountUri(type, host, port),
        };
    }

    /// <summary>
    /// Picks the scheme to mount with. HTTP maps to <c>dav://</c> because the
    /// thing announcing it here is almost always a WebDAV server on the same
    /// port — copyparty serves both — and a browser tab is not what a file
    /// manager should offer.
    /// </summary>
    private static string ToMountUri(string type, string host, int port) => type switch
    {
        "_smb._tcp" => $"smb://{host}/",
        "_sftp-ssh._tcp" => $"sftp://{host}:{port}/",
        "_ftp._tcp" => $"ftp://{host}:{port}/",
        "_webdavs._tcp" or "_https._tcp" => $"davs://{host}:{port}/",
        _ => $"dav://{host}:{port}/",
    };

    /// <summary>avahi escapes spaces and punctuation as \\nnn decimal.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;

        var builder = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && int.TryParse(value.AsSpan(i + 1, 3), out var code))
            {
                builder.Append((char)code);
                i += 3;
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
