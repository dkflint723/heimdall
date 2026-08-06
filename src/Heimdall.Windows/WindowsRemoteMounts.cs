using System.Runtime.InteropServices;
using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Places;

namespace Heimdall.Windows;

/// <summary>
/// Connects and lists network shares through the Windows redirector.
///
/// The same bargain LinuxRemoteMounts strikes with gvfs: Heimdall does not speak
/// SMB, and does not need to. The redirector has spoken it since before this
/// application existed, exposes every share as an ordinary path, and gains
/// WebDAV as well whenever the WebClient service is running. So the whole of
/// network browsing is a path and an enumeration.
///
/// **Deviceless connections, not mapped drive letters.** WNetAddConnection2 can
/// map `\\nas\media` to a free letter, and this deliberately does not: a
/// lettered connection is a DriveType.Network drive, and WindowsPlacesProvider
/// already lists those under Network in the sidebar. Mapping one here would put
/// the same share on screen twice, in two groups, under two names. So Heimdall
/// connects without a letter, browses the UNC path directly, and
/// <see cref="Discover"/> reports only the letterless connections — leaving
/// anything the user mapped themselves exactly where they already expect it.
///
/// **Credentials are Windows' business.** A share needing a password is
/// retried with CONNECT_INTERACTIVE | CONNECT_PROMPT, which brings up the
/// system credential dialog with its own "remember me". Heimdall never sees,
/// stores or transmits the password — and gets Credential Manager for free,
/// which is the same reason LinuxRemoteMounts tells the user to connect once
/// from their file manager and let the desktop keep it.
/// </summary>
public sealed class WindowsRemoteMounts : IRemoteMounts
{
    /// <summary>
    /// mpr.dll ships with Windows and the redirector is always running, so
    /// unlike the Linux side there is no helper whose absence to report.
    /// </summary>
    public bool IsAvailable => true;

    public string AddressPrefill => @"\\";

    public string AddressHint => @"\\server\share · smb:// · http:// for WebDAV";

    public IReadOnlyList<RemoteMount> Discover()
    {
        var found = new List<RemoteMount>();

        foreach (var (local, remote) in Connections())
        {
            // A connection WITH a drive letter is already a drive, and Places
            // lists it. Reporting it here too would duplicate it in the sidebar.
            if (!string.IsNullOrEmpty(local)) continue;
            if (string.IsNullOrEmpty(remote)) continue;

            found.Add(Build(remote));
        }

        return found
            .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every current connection, lettered or not, as (localName, remoteName).
    ///
    /// The buffer is grown rather than guessed: WNetEnumResource answers
    /// ERROR_MORE_DATA and writes the size it wanted, and the strings live
    /// inside the same buffer the structures do, so one entry can need far more
    /// than the structure's own 56 bytes.
    /// </summary>
    private static List<(string Local, string Remote)> Connections()
    {
        var found = new List<(string, string)>();

        var status = Native.WNetOpenEnum(
            Native.RESOURCE_CONNECTED, Native.RESOURCETYPE_DISK, 0, 0, out var handle);

        if (status != Native.NO_ERROR) return found;

        var size = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            while (true)
            {
                var count = uint.MaxValue;
                var bytes = (uint)size;

                status = Native.WNetEnumResource(handle, ref count, buffer, ref bytes);

                if (status == Native.ERROR_MORE_DATA)
                {
                    // Grow to what it asked for and ask again; the enumeration
                    // has not advanced, so nothing is skipped.
                    Marshal.FreeHGlobal(buffer);
                    size = (int)bytes;
                    buffer = Marshal.AllocHGlobal(size);
                    continue;
                }

                if (status != Native.NO_ERROR) break;

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<Native.NETRESOURCEW>(
                        buffer + i * Marshal.SizeOf<Native.NETRESOURCEW>());

                    found.Add((
                        entry.lpLocalName == 0 ? "" : Marshal.PtrToStringUni(entry.lpLocalName) ?? "",
                        entry.lpRemoteName == 0 ? "" : Marshal.PtrToStringUni(entry.lpRemoteName) ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("mounts", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.WNetCloseEnum(handle);
        }

        return found;
    }

    private static RemoteMount Build(string remote) => new()
    {
        Path = remote,
        Label = LabelFor(remote),
        Protocol = Protocol(remote),
        Reachable = IsReachable(remote),
    };

    /// <summary>"media on nas", matching the phrasing the gvfs reader produces.</summary>
    internal static string LabelFor(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length switch
        {
            0 => unc,
            1 => parts[0],
            _ => $"{parts[^1]} on {parts[0]}",
        };
    }

    /// <summary>
    /// WebDAV shares arrive as `\\host@SSL\path` or `\\host@8080\path`, which is
    /// how the redirector spells an HTTP endpoint. Everything else here is SMB.
    /// </summary>
    internal static string Protocol(string unc)
    {
        var host = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return host.Contains('@', StringComparison.Ordinal) ? "dav" : "smb";
    }

    private static bool IsReachable(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch
        {
            // A share whose far end has gone lists as an empty folder otherwise,
            // which is indistinguishable from an empty share.
            return false;
        }
    }

    /// <summary>
    /// Accepts what a Windows user would type and what the rest of Heimdall
    /// passes around.
    ///
    /// `smb://nas/media` is the form the discovery side produces — DNS-SD
    /// advertises services, not UNC paths — so it has to be understood here or
    /// double-clicking a discovered share would do nothing. `http://` is left
    /// alone: the redirector hands those to WebClient, which is how WebDAV is
    /// mounted on Windows.
    /// </summary>
    internal static string ToUnc(string uri)
    {
        var trimmed = (uri ?? "").Trim();

        if (trimmed.Length == 0)
            throw new ArgumentException("Type an address to connect to.", nameof(uri));

        // Already a UNC path, or a drive-relative one; hand it back unchanged.
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)) return TrimTrailing(trimmed);

        var at = trimmed.IndexOf("://", StringComparison.Ordinal);

        var scheme = at >= 0 ? trimmed[..at].ToLowerInvariant() : "";
        var rest = at >= 0 ? trimmed[(at + 3)..] : trimmed;

        switch (scheme)
        {
            // The redirector speaks these itself.
            case "http":
            case "https":
            case "dav":
            case "davs":
                return trimmed;

            case "smb":
            case "cifs":
            case "file":
            case "":
                break;

            // Named rather than swallowed: "could not connect" would send the
            // user looking for a network fault that is not there.
            default:
                throw new NotSupportedException(
                    $"Windows cannot mount {scheme}:// — it connects to SMB shares and, with the "
                    + "WebClient service, WebDAV over http://.");
        }

        // "//nas/media" and "nas/media" both mean the same thing here.
        var unc = @"\\" + rest.Replace('/', '\\').TrimStart('\\');

        return TrimTrailing(unc);
    }

    private static string TrimTrailing(string unc)
        => unc.Length > 2 ? unc.TrimEnd('\\') : unc;

    public async Task<RemoteMount> MountAsync(string uri, CancellationToken ct)
    {
        var unc = ToUnc(uri);

        var status = await Task.Run(() => Connect(unc, prompt: false), ct).ConfigureAwait(false);

        // Only the failures a password would actually fix are worth a dialog.
        // Prompting for a name that does not resolve just moves the error.
        if (status is Native.ERROR_ACCESS_DENIED
                   or Native.ERROR_INVALID_PASSWORD
                   or Native.ERROR_LOGON_FAILURE
                   or Native.ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            ct.ThrowIfCancellationRequested();
            status = await Task.Run(() => Connect(unc, prompt: true), ct).ConfigureAwait(false);
        }

        if (status != Native.NO_ERROR) throw new IOException(Explain(status, unc));

        return Build(unc);
    }

    private static int Connect(string unc, bool prompt)
    {
        var remote = Marshal.StringToHGlobalUni(unc);

        try
        {
            var resource = new Native.NETRESOURCEW
            {
                dwType = Native.RESOURCETYPE_DISK,
                dwDisplayType = Native.RESOURCEDISPLAYTYPE_SHARE,
                dwUsage = Native.RESOURCEUSAGE_CONNECTABLE,

                // Null: deviceless, so this does not become a drive letter.
                lpLocalName = 0,
                lpRemoteName = remote,
            };

            var flags = prompt ? Native.CONNECT_INTERACTIVE | Native.CONNECT_PROMPT : 0;

            // Null credentials mean "whoever I am already", which is what makes
            // a domain or Microsoft-account share connect without asking.
            return Native.WNetAddConnection2(ref resource, null, null, flags);
        }
        finally
        {
            Marshal.FreeHGlobal(remote);
        }
    }

    private static string Explain(int status, string unc) => status switch
    {
        Native.ERROR_BAD_NET_NAME or Native.ERROR_BAD_NETPATH =>
            $"could not find {unc} — check the server name and that the share exists",

        Native.ERROR_ACCESS_DENIED or Native.ERROR_INVALID_PASSWORD
            or Native.ERROR_LOGON_FAILURE =>
            "the server refused those credentials",

        Native.ERROR_SESSION_CREDENTIAL_CONFLICT =>
            "already connected to that server as a different user — disconnect that "
            + "connection first, since Windows allows only one set of credentials per server",

        Native.ERROR_CANCELLED => "cancelled",

        _ => $"could not connect to {unc} (error {status})",
    };

    public async Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct)
    {
        var status = await Task.Run(
            () => Native.WNetCancelConnection2(mount.Path, 0, force: false), ct).ConfigureAwait(false);

        // Worth saying rather than retrying behind the user's back, which is
        // what the interface asks for.
        if (status is Native.ERROR_OPEN_FILES or Native.ERROR_DEVICE_IN_USE)
            throw new IOException(
                "something still has a file open on that share — close it and try again");

        return status == Native.NO_ERROR;
    }
}
