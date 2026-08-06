using System.Runtime.InteropServices;
using Heimdall.Core;
using Heimdall.Core.Places;

namespace Heimdall.Windows;

/// <summary>
/// Browses the LAN by asking Windows' own mDNS responder, through
/// DnsServiceBrowse.
///
/// INetworkDiscovery's rule is that Heimdall implements neither mDNS nor SSDP,
/// because the machine already runs a responder that has been listening since
/// boot and knows about services that appeared before Heimdall started. That
/// argument was written about avahi and holds here unchanged: Windows 10 has
/// shipped an mDNS responder since 1703, and DnsServiceBrowse is the documented
/// way to ask it. The same query finds copyparty — including a share started by
/// Heimdall on another machine, which announces itself over mDNS — as well as
/// WebDAV, SMB and SFTP from NAS boxes and other desktops.
///
/// **Not the SMB network neighbourhood.** WNetOpenEnum over RESOURCE_GLOBALNET
/// is the more obviously "Windows" answer and is the wrong one: it depends on
/// the Computer Browser service and SMB1, both off by default since Windows 10,
/// so on a current network it returns an empty list. It would also never find a
/// Heimdall share, which advertises over mDNS and speaks HTTP.
/// </summary>
public sealed class WindowsNetworkDiscovery : INetworkDiscovery
{
    /// <summary>The same list AvahiDiscovery asks for, in the same order.</summary>
    private static readonly string[] Types =
    [
        "_webdav._tcp",
        "_webdavs._tcp",
        "_smb._tcp",
        "_sftp-ssh._tcp",
        "_ftp._tcp",
        "_http._tcp",
    ];

    /// <summary>
    /// How long to let a browse answer. mDNS has no "no more replies" signal —
    /// a browse runs until cancelled — so a sweep is a deadline rather than a
    /// completion.
    ///
    /// This is the deadline for ALL the types at once, not for each, because
    /// they are browsed concurrently. Sequentially at 400ms each plus their
    /// resolves, six types measured at five seconds against a live network;
    /// the interface promises a second or two, and a person waiting for a
    /// share list will believe the second number and not the first.
    /// </summary>
    private static readonly TimeSpan BrowseWindow = TimeSpan.FromMilliseconds(700);

    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063);

    public string? UnavailableReason => IsAvailable
        ? null
        : "network discovery needs the mDNS resolver in Windows 10 version 1703 or newer";

    public async Task<IReadOnlyList<DiscoveredService>> BrowseAsync(CancellationToken ct)
    {
        if (!IsAvailable) return [];

        ct.ThrowIfCancellationRequested();

        // Every type at once, and every instance within a type at once. Each
        // browse is a deadline and each resolve a round trip, so doing them in
        // sequence adds up to the whole sweep rather than the slowest part of
        // it.
        var perType = await Task.WhenAll(Types.Select(async type =>
        {
            var instances = await BrowseTypeAsync(type, ct).ConfigureAwait(false);

            return await Task.WhenAll(
                instances.Select(i => ResolveAsync(i, type, ct))).ConfigureAwait(false);
        })).ConfigureAwait(false);

        var found = perType.SelectMany(r => r).OfType<DiscoveredService>().ToList();

        // One machine answers on IPv4 and IPv6 and often on several interfaces;
        // one entry per host and port is what a person expects. Same reduction
        // AvahiDiscovery makes, for the same reason.
        return found
            .GroupBy(s => $"{s.Host}:{s.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- The browse ---------------------------------------------------------

    /// <summary>
    /// Sweeps in progress, by id.
    ///
    /// **The context is an id into this rather than a GCHandle to the list**,
    /// which is the safe half of a lesson learned the hard way. A browse does
    /// not stop the instant DnsServiceBrowseCancel returns, so a reply can
    /// still be in flight afterwards; handing the callback a GCHandle that the
    /// sweep has since freed means dereferencing freed memory on a DNS worker
    /// thread, and an access violation there is not catchable — it takes the
    /// process with it. An id that is simply no longer in the dictionary is a
    /// failed lookup and a return.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, List<string>> Sweeps = new();

    /// <summary>Resolves in flight, by the same discipline and the same counter.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        long, TaskCompletionSource<(string?, string?, int)?>> Resolves = new();

    private static long _nextSweep;

    /// <summary>How long to wait for one instance to resolve.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Instance names answering for one service type, e.g.
    /// "partybox._http._tcp.local".
    ///
    /// The callback is a plain function pointer into an
    /// <see cref="UnmanagedCallersOnlyAttribute"/> static, because a managed
    /// delegate marshalled to native would need the reflection marshaller this
    /// assembly does not have under AOT.
    /// </summary>
    private static async Task<List<string>> BrowseTypeAsync(string type, CancellationToken ct)
    {
        var names = new List<string>();
        var id = Interlocked.Increment(ref _nextSweep);

        Sweeps[id] = names;

        var query = Marshal.StringToHGlobalUni($"{type}.local");
        var cancel = new Native.DNS_SERVICE_CANCEL();
        var started = false;

        try
        {
            var request = new Native.DNS_SERVICE_BROWSE_REQUEST
            {
                Version = Native.DNS_QUERY_REQUEST_VERSION1,
                InterfaceIndex = 0,
                QueryName = query,
                pQueryContext = (nint)id,
            };

            unsafe
            {
                delegate* unmanaged<uint, nint, nint, void> callback = &OnBrowse;
                request.pBrowseCallback = (nint)callback;
            }

            var status = Native.DnsServiceBrowse(ref request, ref cancel);

            // Anything else — including a plain success — means no browse is
            // running and there is nothing to wait for or cancel.
            if (status != Native.DNS_REQUEST_PENDING) return names;

            started = true;

            await Task.Delay(BrowseWindow, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Quiet.Swallowed("discovery", ex);
        }
        finally
        {
            if (started) Native.DnsServiceBrowseCancel(ref cancel);

            Sweeps.TryRemove(id, out _);
            Marshal.FreeHGlobal(query);
        }

        lock (names) return [.. names];
    }

    [UnmanagedCallersOnly]
    private static void OnBrowse(uint status, nint context, nint records)
    {
        // Runs on a DNS worker thread, so it must not throw across the native
        // boundary — an escaping exception there tears the process down.
        try
        {
            if (!Sweeps.TryGetValue(context, out var names)) return;

            for (var record = records; record != 0; record = Read.Pointer(record, Native.DnsRecord.Next))
            {
                // **Check the type before reading Data.** The list is not all
                // PTR records: a browse answers with SRV, TXT and A alongside,
                // and Data is a union. In a TXT record the first four bytes are
                // a string COUNT, so reading them as a pointer and following it
                // is an access violation — observed, not theorised, while
                // proving this API works at all.
                if (Read.UInt16(record, Native.DnsRecord.Type) != Native.DNS_TYPE_PTR) continue;

                var instance = Read.String(record, Native.DnsRecord.TargetName);
                if (instance is null) continue;

                lock (names)
                {
                    // Responders repeat themselves, and every interface answers
                    // separately, so the same instance arrives many times.
                    if (!names.Contains(instance, StringComparer.OrdinalIgnoreCase))
                        names.Add(instance);
                }
            }
        }
        catch
        {
            // Nothing safe to report from here; a missed service is the cost.
        }
        finally
        {
            // The record list belongs to us once the callback has it, whether
            // or not anything above wanted it.
            if (records != 0) Native.DnsFree(records, Native.DnsFreeRecordList);
        }
    }

    // ---- Turning an instance into something mountable -----------------------

    /// <summary>
    /// Turns one instance name into something mountable, by asking the
    /// responder to resolve it.
    /// </summary>
    private static async Task<DiscoveredService?> ResolveAsync(
        string instance, string type, CancellationToken ct)
    {
        var resolved = await ResolveInstanceAsync(instance, ct).ConfigureAwait(false);

        if (resolved is not var (host, address, port) || port == 0) return null;

        var where = address ?? host?.TrimEnd('.');
        if (string.IsNullOrEmpty(where)) return null;

        // The instance name carries the human-readable part before the service
        // type: "partybox._http._tcp.local" is a machine called partybox.
        var name = instance.Split('.')[0];
        if (name.Length == 0) name = where;

        return new DiscoveredService
        {
            Name = name,
            ServiceType = type,
            Host = host?.TrimEnd('.') ?? where,
            Address = where,
            Port = port,
            MountUri = MountUri(type, where, port),
        };
    }

    /// <summary>
    /// One DnsServiceResolve, awaited.
    ///
    /// Same id-in-a-dictionary discipline as the browse, and for the same
    /// reason: the completion runs on a DNS worker thread and can arrive after
    /// this method has given up waiting.
    /// </summary>
    private static async Task<(string? Host, string? Address, int Port)?> ResolveInstanceAsync(
        string instance, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextSweep);
        var completion = new TaskCompletionSource<(string?, string?, int)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Resolves[id] = completion;

        var query = Marshal.StringToHGlobalUni(instance);
        var cancel = new Native.DNS_SERVICE_CANCEL();
        var started = false;

        try
        {
            var request = new Native.DNS_SERVICE_RESOLVE_REQUEST
            {
                Version = Native.DNS_QUERY_REQUEST_VERSION1,
                InterfaceIndex = 0,
                QueryName = query,
                pQueryContext = (nint)id,
            };

            unsafe
            {
                delegate* unmanaged<uint, nint, nint, void> callback = &OnResolve;
                request.pResolveCompletionCallback = (nint)callback;
            }

            var status = Native.DnsServiceResolve(ref request, ref cancel);

            if (status != Native.DNS_REQUEST_PENDING) return null;

            started = true;

            // The responder answers in milliseconds when the instance is real;
            // this bound is for the one that has just gone off the network.
            var finished = await Task.WhenAny(
                completion.Task, Task.Delay(ResolveTimeout, ct)).ConfigureAwait(false);

            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Quiet.Swallowed("discovery", ex);
            return null;
        }
        finally
        {
            if (started) Native.DnsServiceResolveCancel(ref cancel);

            Resolves.TryRemove(id, out _);
            Marshal.FreeHGlobal(query);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnResolve(uint status, nint context, nint instance)
    {
        try
        {
            if (!Resolves.TryGetValue(context, out var completion)) return;

            if (status != 0 || instance == 0)
            {
                completion.TrySetResult(null);
                return;
            }

            var host = Read.String(instance, Native.DnsServiceInstance.HostName);
            var port = Read.UInt16(instance, Native.DnsServiceInstance.Port);

            // ip4Address is a POINTER to the address, and is null when the
            // instance resolved to a name but no A record.
            string? address = null;
            var ip4 = Read.Pointer(instance, Native.DnsServiceInstance.Ip4Address);

            if (ip4 != 0)
            {
                var raw = Read.UInt32(ip4, 0);

                // IP4_ADDRESS is in network order, so the bytes come out in the
                // order they are written.
                address = string.Join('.',
                    raw & 0xFF, (raw >> 8) & 0xFF, (raw >> 16) & 0xFF, (raw >> 24) & 0xFF);
            }

            completion.TrySetResult((host, address, port));
        }
        catch
        {
            // Nothing safe to report from a callback; the timeout covers it.
        }
        finally
        {
            if (instance != 0) Native.DnsServiceFreeInstance(instance);
        }
    }

    /// <summary>
    /// What to hand the mounter. A service that speaks WebDAV should be mounted
    /// as WebDAV rather than opened in a browser, which is the same choice
    /// DiscoveredService documents — but spelled for the Windows redirector,
    /// which takes http:// for WebDAV and a UNC path for SMB.
    /// </summary>
    internal static string MountUri(string type, string address, int port) => type switch
    {
        "_smb._tcp" => $@"\\{address}",
        "_webdavs._tcp" or "_https._tcp" => $"https://{address}:{port}",
        "_sftp-ssh._tcp" => $"sftp://{address}:{port}",
        "_ftp._tcp" => $"ftp://{address}:{port}",
        _ => $"http://{address}:{port}",
    };

    /// <summary>
    /// Reads DNS_RECORDW by offset. See Native.DnsRecord for why the structure
    /// is not declared: its Data member is a union, and only three arms matter.
    /// </summary>
    private static class Read
    {
        internal static nint Pointer(nint record, int offset)
            => Marshal.ReadIntPtr(record, offset);

        internal static ushort UInt16(nint record, int offset)
            => unchecked((ushort)Marshal.ReadInt16(record, offset));

        internal static uint UInt32(nint record, int offset)
            => unchecked((uint)Marshal.ReadInt32(record, offset));

        internal static string? String(nint record, int offset)
        {
            var pointer = Marshal.ReadIntPtr(record, offset);
            return pointer == 0 ? null : Marshal.PtrToStringUni(pointer);
        }
    }
}
